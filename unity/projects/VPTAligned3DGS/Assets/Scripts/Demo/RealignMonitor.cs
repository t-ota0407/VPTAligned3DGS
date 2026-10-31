using UnityEngine;

/// <summary>
/// デモの再位置合わせ（集める → 待つ → 足す。Specification.md §15.6b）の判定。
///
/// 入力は PC の状態（PONG の値。<see cref="Observe"/>）と時刻だけで、送信の ON/OFF・振動・HUD の文言といった
/// 副作用は持たない。<see cref="Tick"/> が「次に何をすべきか」（<see cref="Step"/>）を返し、
/// [[DemoController]] がそれを実行する。以前は DemoController の中に入力処理・状態遷移と混ざっていた。
///
/// Collect … PC の採用枚数が目標に達するか、送信していた時間が上限に達したら送信を止め、Wait へ。
///           **PC の処理待ちが溜まったら送信を一時停止する**（バックプレッシャー）。送り続けると
///           PC に溜まり、溜まった分を処理し終えるまで結果が遅れ、CV が次のセッションへ移れない
///           （実機で 25 枚溜まり、再試行が「CV worker not running」で失敗した）。
/// Wait    … PC が届いた分をすべて処理し終え（cv_done ≥ kf かつ処理中でない）、結果が届くまで待つ。
///           確定すれば DemoController が <see cref="Stop"/> するので、ここに残っていれば未確定
///           → 追加収集（Top-up）。maxTopUps 回足しても駄目なら諦める（<see cref="Failure.NotConverged"/>）。
/// **止めて待つだけにはしない**: 新しい解は新しいキーフレームが届いたときにしか作られず、確定には
/// 独立な解の連続一致が要るので、止めたままだと 2 つ目の解が二度と来ないことがある。
///
/// **PC が遅くても正常に終わる**: PC が生きていて処理が進んでいる限り、時間では打ち切らない
/// （収集の合計時間・待機の合計時間に上限を置かない）。止めるのは次のときだけ（CheckPcStopped）:
///   - run.py の無応答・CV の生存信号の途絶（CV は処理中も別スレッドで書くので、途絶える＝死んだ）
///   - このセッションのキーフレームが溜まっているのに、処理中でもなく処理済み枚数が増えない（＝処理していない）
///   - このセッションで処理中のまま、長時間 1 枚も終わらない（＝固まった）
/// 前のセッションを片付けている間（cv_on が別のセッション）は、遅いだけなので待つ。
/// </summary>
public sealed class RealignMonitor
{
    /// <summary>Collect = キーフレームを送っている / Wait = 送信を止めて結果を待っている。</summary>
    public enum Phase { Collect, Wait }

    /// <summary><see cref="Tick"/> が返す「次にすること」。</summary>
    public enum Step
    {
        None,
        PauseCapture,    // PC の処理待ちが溜まった → 送信を一時停止
        ResumeCapture,   // PC が追いついた → 送信を再開
        StartWait,       // 集め終えた → 送信を止めて結果を待つ
        TopUp,           // 未確定 → 追加で集める（送信を再開）
        Fail,            // 諦める（理由は LastFailure）
    }

    /// <summary>諦めた理由。文言（英語）は DemoController が組み立てる。</summary>
    public enum Failure { None, PcDown, CvNotStarted, CvStopped, NotProcessing, Stuck, NotConverged }

    /// <summary>判定に使う PC の状態（UDPManager の PONG 由来の値）。</summary>
    public struct Pc
    {
        public bool   ServerAlive, CvAlive, CvBusy, CvPublished;
        public float  CvAgeS;
        public string ServerSession, CvOnSession, CvReason;
        public int    KeyframeCount, CvDoneCount, CvAcceptedCount;
    }

    /// <summary>しきい値（DemoController の Inspector の値。意味はそちらの Tooltip）。</summary>
    public struct Settings
    {
        public int   collectTargetAccepted, topUpAccepted, maxTopUps, backlogPauseAt, backlogResumeAt;
        public float collectMaxS, topUpMaxS, inFlightS, resultGraceS, waitStallS, busyNoProgressFailS,
                     pcLostFailS, cvStartWaitS, cvBusyHangS;
    }

    /// <summary>再位置合わせ中の PC の状態。**「止まった」と「遅いだけ」「前の処理を片付けている」を区別する。**
    /// 遅いだけ・片付け中は待ち続ける（時間では打ち切らない）。</summary>
    private enum PcStatus
    {
        Ok,        // CV が今のセッションで動いている（処理待ちなし、または次の処理に入る直前）
        Busy,      // CV が今のセッションを処理中
        Switching, // CV は生きていて、前のセッションの処理を片付けている（PONG の cv_on が別のセッション）
        Starting,  // CV の生存信号がどこにも無い（未起動・別フォルダを監視・古い localize）
        Stale,     // CV の生存信号が途絶えた
        Down,      // run.py が応答しない
    }

    private Settings _s;
    private Pc     _pc;
    private int    _sent;                 // Quest が送ったキーフレーム数
    private float  _now;
    private string _sessionBeforeReset = "";

    private Phase _phase = Phase.Collect;
    private bool  _active;                // RESET 後の待ちが終わり、段階の判定を始めたか
    private int   _topUps;                // 追加収集の回数
    private int   _collectStartAccepted;  // この収集を始めた時点の PC 採用枚数
    private int   _targetAccepted;        // この収集で目指す PC 採用枚数（累計）
    private float _phaseT;
    private float _caughtUpT = -1f;       // PC が届いた分をすべて処理し終えた（と見えた）時刻
    private float _collectActiveS;        // この収集で実際に送信していた時間（一時停止中は数えない）
    private bool  _paused;                // PC の処理待ちが溜まったので送信を一時停止している
    // 進み具合の監視（処理済み枚数）。「止まった」「固まった」の判定に使う
    private int   _lastDone = -1;
    private float _stallSince = -1f;      // 溜まっているのに処理中でもなく進まない状態が始まった時刻
    private float _busySince = -1f;       // このセッションで処理中のまま 1 枚も終わらない状態が始まった時刻
    private PcStatus _pcIssue = PcStatus.Ok;  // いま続いている PC の異常
    private float _pcIssueT = -1f;            // その異常が始まった時刻

    // ================= 進め方 =================

    /// <summary>毎フレーム、PC の状態と送信枚数・時刻を渡す（HUD 用の値もこれから出す）。</summary>
    public void Observe(Pc pc, int sentCount, float now)
    {
        _pc = pc; _sent = sentCount; _now = now;
    }

    /// <summary>RESET を送る直前に呼ぶ。このときのセッション名を覚え、PONG がこれと変わるまで PC の枚数を信じない
    /// （<see cref="SessionFresh"/>）。判定は <see cref="Begin"/> まで止める。</summary>
    public void Reset(string sessionBeforeReset)
    {
        _sessionBeforeReset = sessionBeforeReset ?? "";
        _active = false;
    }

    /// <summary>RESET 後の待ちが終わったら呼ぶ。最初の収集を始める（新しいセッションは採用枚数 0 から始まるので、
    /// 起点は 0 に固定する。この時点の PONG はまだ前のセッションの枚数を返していることがある）。</summary>
    public void Begin(Settings s, float now)
    {
        _s = s;
        _now = now;
        _topUps = 0;
        BeginCollect(0, s.collectTargetAccepted);
        _pcIssue = PcStatus.Ok; _pcIssueT = -1f;
        _lastDone = -1; _stallSince = -1f; _busySince = -1f;
        LastFailure = Failure.None;
        _active = true;
    }

    /// <summary>判定をやめる（確定・取り消し・諦め）。</summary>
    public void Stop() => _active = false;

    /// <summary>1 フレーム進める。dt は実時間の経過[s]（送信していた時間を数える）。</summary>
    public Step Tick(float dt)
    {
        if (!_active) return Step.None;   // RESET 後の待ち（resetSettleS）の間
        float now = _now;

        PcStatus st = Status;
        if (CheckPcStopped(st, now)) { _active = false; return Step.Fail; }

        if (_phase == Phase.Collect)
        {
            // バックプレッシャー: 処理待ちが溜まったら止め、減ったら再開する（ヒステリシス）
            Step step = Step.None;
            int backlog = PcBacklog;
            if (!_paused && backlog >= _s.backlogPauseAt)
            {
                _paused = true;
                step = Step.PauseCapture;
                Debug.Log($"[DemoController] PC の処理待ち {backlog} 枚 → 送信を一時停止");
            }
            else if (_paused && backlog <= _s.backlogResumeAt)
            {
                _paused = false;
                step = Step.ResumeCapture;
                Debug.Log($"[DemoController] PC が追いついた（処理待ち {backlog} 枚）→ 送信を再開");
            }
            if (!_paused) _collectActiveS += dt;

            // 収集の区切りは「採用枚数」と「実際に送信していた時間」だけ。PC 待ちで止まっている時間は
            // 数えないので、PC が遅くても集める量は減らない（合計時間の上限は置かない）
            bool reached = FreshAccepted >= _targetAccepted;
            if (reached || _collectActiveS >= PhaseLimitS)
            {
                Debug.Log($"[DemoController] 収集を止めて結果を待つ（採用 {CollectAccepted}/{CollectTarget}・" +
                          $"送信 {_collectActiveS:F0}s / 全体 {PhaseElapsedS:F0}s・追加 {_topUps}/{_s.maxTopUps}）");
                _phase = Phase.Wait;
                _phaseT = now;
                _caughtUpT = -1f;
                _paused = false;
                return Step.StartWait;
            }
            return step;
        }

        // ---- Wait（上限時間なし。PC が処理し終えるまで待つ。止まったかどうかは CheckPcStopped が見る） ----
        bool caughtUp = PhaseElapsedS >= _s.inFlightS && SessionFresh && !_pc.CvBusy
                        && _pc.CvDoneCount >= _pc.KeyframeCount;
        if (!caughtUp) _caughtUpT = -1f;
        else if (_caughtUpT < 0f) _caughtUpT = now;
        bool settled = _caughtUpT >= 0f && now - _caughtUpT >= _s.resultGraceS;
        if (!settled) return Step.None;

        if (_topUps < _s.maxTopUps)
        {
            _topUps++;
            int acc = FreshAccepted;
            BeginCollect(acc, acc + _s.topUpAccepted);
            Debug.Log($"[DemoController] 未確定 → 追加収集 {_topUps}/{_s.maxTopUps}（採用 {_s.topUpAccepted} 枚）" +
                      $" PC: {FreshReason}");
            return Step.TopUp;
        }
        LastFailure = Failure.NotConverged;
        _active = false;
        return Step.Fail;
    }

    /// <summary>キーフレームの収集を始める（最初の収集・追加収集）。目標は PC の採用枚数（累計）で持つ。</summary>
    private void BeginCollect(int startAccepted, int targetAccepted)
    {
        _phase = Phase.Collect;
        _collectStartAccepted = startAccepted;
        _targetAccepted = Mathf.Max(targetAccepted, startAccepted + 1);
        _phaseT = _now;
        _caughtUpT = -1f;
        _collectActiveS = 0f;
        _paused = false;
    }

    /// <summary>PC が**止まった**（遅いだけではない）と判断できたら、理由を LastFailure に入れて true を返す。
    /// 遅いだけ（処理中・前のセッションを片付け中）なら何もしない — 時間では打ち切らない。</summary>
    private bool CheckPcStopped(PcStatus st, float now)
    {
        // 1) run.py の無応答・CV の生存信号の途絶・CV がどこにもいない
        float limit = st == PcStatus.Down || st == PcStatus.Stale ? _s.pcLostFailS
                    : st == PcStatus.Starting ? _s.cvStartWaitS
                    : -1f;                                   // Ok / Busy / Switching は生きている
        if (limit < 0f) { _pcIssue = PcStatus.Ok; _pcIssueT = -1f; }
        else
        {
            if (st != _pcIssue) { _pcIssue = st; _pcIssueT = now; }   // 異常の種類が変わったら数え直す
            if (now - _pcIssueT > limit)
            {
                LastFailure = st == PcStatus.Down ? Failure.PcDown
                            : st == PcStatus.Starting ? Failure.CvNotStarted
                            : Failure.CvStopped;
                return true;
            }
        }

        // 処理済み枚数が増えたら「進んでいる」。止まり・固まりの判定はやり直す
        int done = SessionFresh ? _pc.CvDoneCount : -1;
        bool progressed = done != _lastDone;
        _lastDone = done;

        // 2) 止まった: このセッションのキーフレームが溜まっているのに、CV は生きていて処理中でもなく、進まない
        //    （localize がこのセッションを見ていない。--no-cv で手動起動したときの --out と --watch の食い違いなど）。
        //    localize は 1 秒ごとに見に来るので、正常なら溜まった時点ですぐ処理中になる。
        if (st != PcStatus.Ok || PcBacklog <= 0 || progressed) _stallSince = -1f;
        else if (_stallSince < 0f) _stallSince = now;
        else if (now - _stallSince > _s.waitStallS)
        {
            LastFailure = Failure.NotProcessing;
            FailBacklog = PcBacklog;
            return true;
        }

        // 3) 固まった: このセッションで処理中のまま、長時間 1 枚も終わらない。
        //    PC 待ちで送信を止めるので 1 回の処理は数枚まで。遅いだけならいずれ 1 枚は終わる。
        if (st != PcStatus.Busy) _busySince = -1f;
        else if (_busySince < 0f || progressed) _busySince = now;
        else if (now - _busySince > _s.busyNoProgressFailS)
        {
            LastFailure = Failure.Stuck;
            return true;
        }
        return false;
    }

    private PcStatus Status
    {
        get
        {
            if (!_pc.ServerAlive) return PcStatus.Down;
            // CV が別のセッション（前の再位置合わせ・実験の試行）をまだ処理している。生きているので待つ
            string on = _pc.CvOnSession;
            bool elsewhere = !string.IsNullOrEmpty(on) && on != _pc.ServerSession;
            if (_pc.CvAlive) return elsewhere ? PcStatus.Switching : _pc.CvBusy ? PcStatus.Busy : PcStatus.Ok;
            // 古い localize（処理中に生存信号を書かない版）との互換: 処理中フラグがあれば長い処理の最中
            if (_pc.CvBusy) return elsewhere ? PcStatus.Switching
                                 : _pc.CvAgeS > _s.cvBusyHangS ? PcStatus.Stale : PcStatus.Busy;
            return _pc.CvAgeS < 0f ? PcStatus.Starting : PcStatus.Stale;
        }
    }

    // ================= 状態（HUD・文言用） =================

    /// <summary>段階の判定をしているか（RESET 後の待ちが終わり、確定・取り消し・諦めの前）。</summary>
    public bool  Active          => _active;
    public Phase CurrentPhase    => _phase;
    /// <summary>追加収集の回数。</summary>
    public int   TopUps          => _topUps;
    /// <summary>PC の処理待ちが溜まったので送信を一時停止しているか（段階を問わない生の値）。</summary>
    public bool  Paused          => _paused;
    /// <summary>収集中に、PC の処理待ちで送信を一時停止しているか。</summary>
    public bool  CapturePaused   => _active && _phase == Phase.Collect && _paused;
    /// <summary>この収集で PC が採用した枚数 / 目標枚数。</summary>
    public int   CollectAccepted => Mathf.Max(0, FreshAccepted - _collectStartAccepted);
    public int   CollectTarget   => Mathf.Max(1, _targetAccepted - _collectStartAccepted);
    public float CollectProgress => Mathf.Clamp01(CollectAccepted / (float)CollectTarget);
    public float PhaseElapsedS   => _now - _phaseT;
    /// <summary>収集の上限（送信していた時間）。待機に上限は無い。</summary>
    public float PhaseLimitS     => _topUps == 0 ? _s.collectMaxS : _s.topUpMaxS;
    /// <summary>この収集で実際に送信していた時間[s]（PC 待ちの一時停止は除く）。</summary>
    public float CollectActiveS  => _collectActiveS;
    /// <summary>PC が受け取ったがまだ処理していない枚数（kf − cv_done）。新しいセッションの PONG が来るまでは 0。</summary>
    public int   PcBacklog       => SessionFresh ? Mathf.Max(0, _pc.KeyframeCount - _pc.CvDoneCount) : 0;
    /// <summary>新しいセッションで CV がまだ動き出していない（前の処理を片付けている最中など）。</summary>
    public bool  WaitingForCv    => _active && (Status == PcStatus.Switching || Status == PcStatus.Starting);

    /// <summary>PONG が RESET 後の新しいセッションのものか。RESET 直後は前のセッションの採用枚数（例: 30）が
    /// 残っていることがあり（PONG は 2 秒ごと）、それを信じると集め始めた瞬間に「目標到達」と誤判定する。</summary>
    public bool   SessionFresh   => _pc.ServerAlive && _pc.ServerSession != _sessionBeforeReset;
    /// <summary>このセッションで PC が採用したキーフレーム数（新しいセッションの PONG が来るまでは 0）。</summary>
    public int    FreshAccepted  => SessionFresh ? _pc.CvAcceptedCount : 0;
    /// <summary>このセッションの直近の解の採否理由（PC の棄却理由。新しいセッションの PONG が来るまでは空）。</summary>
    public string FreshReason    => SessionFresh ? _pc.CvReason : "";
    /// <summary>直近の解を PC が公開したか（false なら棄却されている）。</summary>
    public bool   PcPublished    => _pc.CvPublished;
    /// <summary>送った枚数に比べて PC が採用した枚数が少ない（照明・模様の無い壁などで照合できていない）。
    /// 品質の判定ではなく、指示を選ぶための目安（判定は PC 側）。</summary>
    public bool   FewRecognized  => _sent >= 8 && FreshAccepted * 2 < _sent;

    /// <summary>直近に諦めた理由（Begin で None に戻る）。</summary>
    public Failure LastFailure { get; private set; } = Failure.None;
    /// <summary><see cref="Failure.NotProcessing"/> で諦めたときの処理待ち枚数。</summary>
    public int     FailBacklog { get; private set; }
}
