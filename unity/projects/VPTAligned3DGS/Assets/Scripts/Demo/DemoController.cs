using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// デモ展示モードの進行制御（→ Specification.md §15 / ImplementationSpec.md §14）。
///
/// 一度求めた位置合わせを、アプリを閉じても・電源を切っても再利用して来場者に見せる。
/// 保存と復元そのものは [[AnchorPersistence]]（空間アンカー基準）が行い、ここは「いつ見せるか」
/// 「誰に何を押させるか」を決める。
///
/// **UX の原則（Specification.md §15.4）**
///   1. 合っている確信が持てない姿勢は来場者に見せない。確信が無い間は非表示のまま「見回して」と出す。
///   2. 来場者が押せるのは X（表示切替）だけ。判断が要る操作は Y＋B 長押しで開くスタッフパネルの奥に置く。
///   3. 最後の手段は PC で約 1 分の再位置合わせ。一度保存できれば、展示中は PC が要らない。
///
/// 状態遷移:
/// <code>
/// 起動 → Locating（非表示）─ 多数決が揃う ─────────→ Showing（フェードイン）
///                          └ 保存なし／待ち時間切れ ─→ NeedsAlignment（裏では探し続ける）
/// Showing ─ 原点の移動・スリープ復帰 → 揃っていればそのまま／揃わなければ Relocating（非表示）
/// NeedsAlignment / Showing ─[スタッフ: Re-align]→ Aligning ─ 確定 → Saving → Showing
/// Aligning の中: Collect（PC 採用 24 枚 or 60s）→ Wait（PC の処理と結果を待つ）
///                → 未確定なら Top-up（+6 枚）→ Wait … 3 回足しても駄目なら理由を出して保存済みへ戻る
/// Aligning ─ 原点の移動 → Quest と PC を対でリセットして集め直す
/// </code>
///
/// 入力（物理ボタン。HUD の全ボタンに対応がある）:
///   来場者      : X = 3DGS の表示切替（AlignmentManager が受ける）。他は何もしない。
///   Y＋B 長押し : スタッフパネルの開閉（手動調整中は不可。先に Y で取り消すか A で確定する）
///   スタッフ    : A = Re-align / Cancel、A 長押し = 保存の消去、B = Show anyway、Y = 手動調整、
///                 両スティック押込 3 秒 = 実験へ戻る（実験の最初の画面からデモへ入る操作と同じ。[[AppMode]]）
///   手動調整中  : スティック / トリガー / A（Lock して保存）は AlignmentManager、Y = 取り消し。
///                 B（初期姿勢へ戻す）は無効（AlignmentManager.ResetButtonEnabled=false）
/// </summary>
public class DemoController : MonoBehaviour
{
    public enum DemoState
    {
        /// <summary>起動直後。保存したアンカーを探している（非表示）。</summary>
        Locating,
        /// <summary>表示中（アンカーの多数決で追従）。</summary>
        Showing,
        /// <summary>原点の移動・スリープ復帰のあと、アンカーで確かめ直している（非表示）。</summary>
        Relocating,
        /// <summary>保存が無い・見つからない。スタッフの操作待ち（裏では探し続ける）。</summary>
        NeedsAlignment,
        /// <summary>PC で自動位置合わせ中（スタッフが歩く）。</summary>
        Aligning,
        /// <summary>確定した姿勢をアンカーに保存中。</summary>
        Saving,
        /// <summary>スタッフの手動調整中。</summary>
        Manual,
    }

    [Header("References（未設定なら自動取得）")]
    [SerializeField] private AlignmentManager align;
    [SerializeField] private AnchorPersistence anchors;
    [SerializeField] private KeyframeUploader uploader;
    [SerializeField] private UDPManager udp;
    [Tooltip("トラッキング原点の監視。未設定なら実行時に自作する（シーンを編集しなくても効く）。")]
    [SerializeField] private TrackingOriginWatcher tracking;

    [Header("Restore（起動・復帰）")]
    [Tooltip("見つからないまま、この秒数が経ったら来場者に「見回して」と出す。")]
    [SerializeField] private float lookAroundHintS = 3f;
    [Tooltip("この秒数で見つからなければスタッフの操作待ちにする。裏では探し続け、見つかれば自動で表示する。要実機調整。")]
    [SerializeField] private float restoreWaitS = 30f;
    [Tooltip("原点の移動・スリープ復帰のあと、多数決が揃わないままこの秒数が経ったら非表示にして確かめ直す。")]
    [SerializeField] private float reverifyGraceS = 0.5f;
    [Tooltip("表示するときのフェードイン[s]。")]
    [SerializeField] private float fadeInS = 0.5f;

    [Header("Re-align")]
    [Tooltip("RESET 送信後、PC が新セッションを作るのを待つ時間[s]（ExperimentController.resetSettleS と同じ理由）。")]
    [SerializeField] private float resetSettleS = 1.0f;
    [Tooltip("保存の完了をこの秒数待っても返らなければ、保存失敗として表示に進む。")]
    [SerializeField] private float saveTimeoutS = 40f;

    [Header("Re-align: 集める → 待つ → 足す（Specification.md §15.6b）")]
    [Tooltip("PC が**採用した**キーフレームがこの枚数に達したら送信を止めて結果を待つ。\n" +
             "PC が解き直しに使う範囲（cv.json の window、既定 24）と揃える。Quest は品質を判定せず、" +
             "PC の採用枚数（PONG の cv_ok）だけを見る（品質しきい値を Quest に複製しない）。要実機調整。")]
    [SerializeField] private int collectTargetAccepted = 24;
    [Tooltip("最初の収集の上限[s]。**実際に送信していた時間**で数える（PC 待ちで一時停止している間は数えない）。" +
             "採用枚数が届かなくても、ここで止めて結果を待つ。")]
    [SerializeField] private float collectMaxS = 60f;
    [Tooltip("確定しなかったときに追加で集める採用枚数。")]
    [SerializeField] private int topUpAccepted = 6;
    [Tooltip("追加収集 1 回あたりの上限[s]（送信していた時間で数える）。")]
    [SerializeField] private float topUpMaxS = 30f;
    [Tooltip("追加収集の最大回数。足しても確定しなければ、理由を出して保存済みの位置合わせへ戻る。")]
    [SerializeField] private int maxTopUps = 3;
    // **PC が遅いことを理由に打ち切る上限は置かない**（収集の合計時間・待機の合計時間）。
    // PC が生きていて処理が進んでいる限り待つ。止めるのは「止まった」と判断できたときだけ（下の Stall / 固まり）。
    // 以前は合計 300s / 180s の上限があり、十分に遅い PC では正常に処理していても打ち切られた。

    [Header("Re-align: PC が追いつくまで送らない（バックプレッシャー）")]
    [Tooltip("PC の処理待ち（受け取ったが未処理 = kf − cv_done）がこの枚数以上になったら送信を一時停止する。\n" +
             "送り続けると PC に溜まり（実機で 25 枚溜まった）、溜まった分を処理し終えるまで CV は次のセッションへ" +
             "移れない・結果も遅れる。PC は届いた分をまとめて処理するので、数枚ずつ渡すのが速い。")]
    [SerializeField] private int backlogPauseAt = 4;
    [Tooltip("処理待ちがこの枚数以下まで減ったら送信を再開する。")]
    [SerializeField] private int backlogResumeAt = 1;

    [Header("Re-align: 待機と PC の生存")]
    [Tooltip("送信を止めてから、最後のキーフレームが PC に届き PONG に反映されるまでの猶予[s]。" +
             "この間は「処理し終えたか」を判定しない（PONG は 2 秒ごと）。")]
    [SerializeField] private float inFlightS = 2.5f;
    [Tooltip("PC が全部処理し終えてから、結果が届いて一致判定されるのを待つ猶予[s]（run.py は結果を 1 秒ごとに返送）。")]
    [SerializeField] private float resultGraceS = 2.5f;
    [Tooltip("**止まった**の判定: CV は生きていて処理中でもないのに、このセッションのキーフレームが溜まったまま" +
             "処理済み枚数がこの秒数増えない（＝ localize がこのセッションを処理していない。--out と --watch の食い違いなど）。\n" +
             "PC が遅いだけなら処理中フラグが立っているので、これには当たらない。")]
    [SerializeField] private float waitStallS = 30f;
    [Tooltip("**固まった**の判定: このセッションで処理中のまま、この秒数 1 枚も処理が終わらない。\n" +
             "PC 待ちで送信を止めるので 1 回の処理は数枚まで。1 枚 3 分かかってもこれには当たらない。")]
    [SerializeField] private float busyNoProgressFailS = 900f;
    [Tooltip("run.py が応答しない、または CV の生存信号が途絶えた状態がこの秒数続いたら諦める。\n" +
             "CV の生存信号は別スレッドが処理中も書き続ける（heartbeat.py）ので、途絶えるのはプロセスが死んだときだけ。")]
    [SerializeField] private float pcLostFailS = 10f;
    [Tooltip("CV の生存信号がどこにも無い（新しいセッションにも、ベースにも）状態を待つ上限[s]。\n" +
             "新しい localize なら、前のセッションを処理中でもベースの生存信号で分かる（cv_on）ので、" +
             "ここに来るのは localize が起動していない・別のフォルダを見ている・古い localize のときだけ。")]
    [SerializeField] private float cvStartWaitS = 120f;
    [Tooltip("古い localize（生存信号を処理中に書かない版）との互換: 処理中フラグのまま生存信号がこの秒数古ければ固まったとみなす。")]
    [SerializeField] private float cvBusyHangS = 300f;

    [Header("Staff input")]
    [Tooltip("スタッフパネルの開閉: Y（左）と B（右）を同時にこの秒数押し続ける。来場者が偶然押さない組み合わせ。")]
    [SerializeField] private float staffComboHoldS = 3f;
    [Tooltip("デモに入ってからこの秒数は、来場者向けの案内にスタッフパネルの開き方を出す。\n" +
             "デモに切り替えた本人（＝スタッフ）が読むため。スタッフも Y＋B を忘れるので UI に書く。")]
    [SerializeField] private float staffHintOnEntryS = 10f;
    [Tooltip("保存の消去: スタッフパネルを開いている間に A をこの秒数押し続ける。")]
    [SerializeField] private float clearHoldS = 3f;
    [Tooltip("これより短い押下を「短押し」とみなす[s]。")]
    [SerializeField] private float tapMaxS = 0.8f;
    [Tooltip("スタッフパネルを開いている間に左右のスティックを同時にこの秒数押し込むと、実験（GSTestScene）へ戻る。\n" +
             "実験側からデモへ入る操作と同じ（ExperimentController.demoSwitchHoldS）。")]
    [SerializeField] private float experimentSwitchHoldS = 3f;

    private readonly AppMode.SticksHold _experimentHold = new AppMode.SticksHold();

    // ---- 状態 ----
    private DemoState _state = DemoState.Locating;
    private float _stateT;
    private bool  _staff;
    private bool  _reverifyPending;
    private float _reverifyT;
    private bool  _showOnVerify;          // 確かめ直しが通ったら表示し直す（前の来場者が X で消した場合など）
    private bool  _originMovedWhileSaving;
    private bool  _lastSaveFailed;
    private bool  _busy;                  // 再位置合わせの開始処理中
    private float _enteredT;              // デモに入った時刻（スタッフ向けの案内を出す期間の起点）

    // ---- 再位置合わせの段階（Aligning の中） ----
    /// <summary>再位置合わせの段階。Collect = キーフレームを送っている / Wait = 送信を止めて結果を待っている。</summary>
    public enum AlignPhase { Collect, Wait }
    private AlignPhase _alignPhase = AlignPhase.Collect;
    private bool  _alignActive;           // RESET 後の待ちが終わり、段階の判定を始めたか
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
    private int   _alignAttempt;
    private Coroutine _alignCo;

    private PressTracker _a, _b, _y;                       // tapMaxS が決まる Awake で作る
    private readonly PressTracker _combo = new PressTracker();   // Y＋B 同時押し

    // ================= 公開状態（HUD 用） =================

    public DemoState State => _state;
    /// <summary>スタッフパネルを開いているか。</summary>
    public bool  StaffMode => _staff;
    public float StateElapsedS => Time.realtimeSinceStartup - _stateT;
    public AnchorPersistence Anchors => anchors;

    /// <summary>いまの姿勢がアンカーの多数決で確かめられているか。</summary>
    public bool Verified => anchors != null && anchors.HasConsensus && align != null && align.IsAnchored;
    /// <summary>再位置合わせに必要な PC（run.py と CV ワーカ）が動いているか。</summary>
    public bool PcReady => udp != null && udp.ServerAlive && udp.CvAlive;
    /// <summary>直近の保存が失敗したか（失敗した姿勢は再起動で失われる）。</summary>
    public bool LastSaveFailed => _lastSaveFailed;

    public string Notice { get; private set; } = "";
    private float _noticeT = -1f;
    public float NoticeAgeS => _noticeT < 0f ? float.MaxValue : Time.realtimeSinceStartup - _noticeT;

    private bool Idle =>
        _state == DemoState.Locating || _state == DemoState.Showing ||
        _state == DemoState.Relocating || _state == DemoState.NeedsAlignment;

    public bool CanRealign     => !_busy && Idle && PcReady;
    public bool CanCancelAlign => _state == DemoState.Aligning;
    public bool CanShowAnyway  => anchors != null && anchors.CanForceShow && Idle && _state != DemoState.Showing;
    public bool CanManual      => !_busy && Idle;
    public bool InManual       => _state == DemoState.Manual;
    public bool CanClearSaved  => anchors != null && anchors.HasSave && Idle;
    /// <summary>実験へ戻れるか。スタッフパネルを開いていて、作業（再位置合わせ・保存・手動調整）の途中でないとき。
    /// 手動調整中はスティック押込が呼び寄せ・水平化に取られている。</summary>
    public bool CanSwitchToExperiment => _staff && Idle && !_busy;

    /// <summary>再位置合わせできない理由（HUD 用・英語）。できるなら空。
    /// 処理中フラグが立っているのに生存信号が古い（＝長い処理の最中）ときを「CV 停止」と書かない。</summary>
    public string RealignBlockReason =>
        udp == null || !udp.ServerAlive ? "PC server not running (run.py)"
        : udp.CvBusy && !udp.CvAlive ? "the PC is still processing earlier keyframes — wait a moment"
        : !udp.CvAlive ? "CV worker not running — check the run.py window" : "";

    private PcStatus GetPcStatus()
    {
        if (udp == null || !udp.ServerAlive) return PcStatus.Down;
        // CV が別のセッション（前の再位置合わせ・実験の試行）をまだ処理している。生きているので待つ
        string on = udp.CvOnSession;
        bool elsewhere = !string.IsNullOrEmpty(on) && on != udp.ServerSession;
        if (udp.CvAlive) return elsewhere ? PcStatus.Switching : udp.CvBusy ? PcStatus.Busy : PcStatus.Ok;
        // 古い localize（処理中に生存信号を書かない版）との互換: 処理中フラグがあれば長い処理の最中
        if (udp.CvBusy) return elsewhere ? PcStatus.Switching
                             : udp.CvAgeS > cvBusyHangS ? PcStatus.Stale : PcStatus.Busy;
        return udp.CvAgeS < 0f ? PcStatus.Starting : PcStatus.Stale;
    }

    private static string PcIssueText(PcStatus s) =>
        s == PcStatus.Down     ? "the PC server stopped responding (run.py)"
      : s == PcStatus.Starting ? "the CV worker is not running for this folder — check the [cv] lines in the run.py window"
      :                          "the CV worker stopped — check the [cv] lines in the run.py window";

    /// <summary>来場者向けの 1 行（英語。無ければ空）。スタッフパネルを開いている間は出さない。</summary>
    public string VisitorHint
    {
        get
        {
            switch (_state)
            {
                case DemoState.Locating:       return StateElapsedS >= lookAroundHintS ? "Look around the room" : "";
                case DemoState.Relocating:     return StateElapsedS >= 1f ? "Look around the room" : "";
                case DemoState.NeedsAlignment: return "Please ask a staff member";
                case DemoState.Aligning:       return "Aligning…";
                default:                       return "";
            }
        }
    }

    /// <summary>来場者向けの案内に、スタッフパネルの開き方（Y＋B 長押し）を添えるか。
    ///
    /// **スタッフが要る場面だけ**出す: デモに入った直後（切り替えた本人が読む）・保存が無い／見つからない
    /// （`Please ask a staff member`）・再位置合わせ中・確かめ直しが長引いたとき。
    /// 表示中（来場者が見ている間）は出さない — 開き方が来場者に知れると、パネルの奥の操作
    /// （手動調整して保存・保存の消去）に手が届いてしまう。</summary>
    public bool ShowStaffHint =>
        !_staff && _state != DemoState.Manual &&
        (_state == DemoState.NeedsAlignment || _state == DemoState.Aligning
         || (_state == DemoState.Relocating && StateElapsedS > restoreWaitS)
         || Time.realtimeSinceStartup - _enteredT < staffHintOnEntryS);

    public float StaffComboHoldS => staffComboHoldS;

    /// <summary>スタッフパネルを開くための Y＋B 長押しの進み具合 0..1（押していなければ 0）。
    /// 3 秒は反応が無いと「効いているのか」分からない長さなので、HUD が押している間だけ出す。</summary>
    public float StaffComboProgress =>
        _staff || _combo.LongFired ? 0f : _combo.Progress(staffComboHoldS);

    /// <summary>来場者向けの補足（英語）。</summary>
    public string VisitorHintSub
    {
        get
        {
            switch (_state)
            {
                case DemoState.Locating:
                case DemoState.Relocating:     return "Finding where the 3D scene belongs";
                case DemoState.NeedsAlignment: return "The 3D scene has not been aligned to this room yet";
                case DemoState.Aligning:
                    // 歩いているスタッフはパネルを閉じていることが多いので、ここにも進み具合を出す
                    if (!_alignActive) return "Starting…";
                    if (WaitingForCv) return "Waiting for the PC…";
                    if (_alignPhase == AlignPhase.Wait) return "Computing on the PC — you can stop walking";
                    return _paused
                        ? $"PC is catching up — keep walking slowly  ·  {CollectAccepted} / {CollectTarget}"
                        : $"Walk slowly around the room  ·  {CollectAccepted} / {CollectTarget}";
                default:                       return "";
            }
        }
    }

    // ---------------- 再位置合わせの進み具合（HUD 用） ----------------

    public AlignPhase CurrentAlignPhase => _alignPhase;
    /// <summary>再位置合わせの段階の判定を始めているか（RESET 後の待ちが終わったか）。</summary>
    public bool  AlignActive     => _state == DemoState.Aligning && _alignActive;
    public int   TopUpCount      => _topUps;
    public int   MaxTopUps       => maxTopUps;
    /// <summary>この収集で PC が採用した枚数 / 目標枚数。</summary>
    public int   CollectAccepted => Mathf.Max(0, FreshAccepted - _collectStartAccepted);
    public int   CollectTarget   => Mathf.Max(1, _targetAccepted - _collectStartAccepted);
    public float CollectProgress => Mathf.Clamp01(CollectAccepted / (float)CollectTarget);
    public float PhaseElapsedS   => Time.realtimeSinceStartup - _phaseT;
    /// <summary>収集では送信していた時間の上限、待機では待機の上限。</summary>
    public float PhaseLimitS     => _topUps == 0 ? collectMaxS : topUpMaxS;   // 収集の上限（送信していた時間）。待機に上限は無い
    /// <summary>この収集で実際に送信していた時間[s]（PC 待ちの一時停止は除く）。</summary>
    public float CollectActiveS  => _collectActiveS;
    /// <summary>PC の処理待ちが溜まったので送信を一時停止しているか。</summary>
    public bool  CapturePaused   => _alignActive && _alignPhase == AlignPhase.Collect && _paused;
    /// <summary>PC が受け取ったがまだ処理していない枚数（kf − cv_done）。新しいセッションの PONG が来るまでは 0。</summary>
    public int   PcBacklog       => SessionFresh ? Mathf.Max(0, udp.ServerKeyframeCount - udp.CvDoneCount) : 0;
    /// <summary>新しいセッションで CV がまだ動き出していない（前の処理を片付けている最中など）。</summary>
    public bool  WaitingForCv    => _alignActive && (GetPcStatus() == PcStatus.Switching || GetPcStatus() == PcStatus.Starting);
    /// <summary>直近の再位置合わせを諦めた理由（英語。次の再位置合わせを始めるまで残す）。</summary>
    public string LastAlignFailure { get; private set; } = "";

    /// <summary>PONG が RESET 後の新しいセッションのものか。RESET 直後は前のセッションの採用枚数（例: 30）が
    /// 残っていることがあり（PONG は 2 秒ごと）、それを信じると集め始めた瞬間に「目標到達」と誤判定する。</summary>
    private bool SessionFresh => udp != null && udp.ServerAlive && udp.ServerSession != _sessionBeforeReset;
    private string _sessionBeforeReset = "";
    /// <summary>このセッションで PC が採用したキーフレーム数（新しいセッションの PONG が来るまでは 0）。</summary>
    private int FreshAccepted => SessionFresh ? udp.CvAcceptedCount : 0;

    /// <summary>歩いているスタッフへの指示（英語）。PC の棄却理由（`gates.check_geometry` の文言）から選ぶ。
    /// 文言の先頭が gates.py と一致していることは `tests/test_gates.py` が検査する。</summary>
    public string AlignGuidance
    {
        get
        {
            if (!_alignActive) return "Starting — resetting the PC session";
            if (WaitingForCv) return "Waiting for the PC to finish earlier work…";
            if (_alignPhase == AlignPhase.Wait) return "Capture paused — computing the alignment on the PC";
            if (_paused) return $"PC is catching up ({PcBacklog} queued) — keep walking slowly";
            string why = SessionFresh ? udp.CvReason : "";
            if (!string.IsNullOrEmpty(why) && !udp.CvPublished)
            {
                if (why.StartsWith(GuideNotEnoughMovement)) return "Walk farther — cover more of the room";
                if (why.StartsWith(GuidePathTooStraight))   return "Walk a curved path, not a straight line";
                return "Keep moving slowly and look at textured surfaces";
            }
            if (FewRecognized) return "Few keyframes recognized — check the lighting, avoid blank walls";
            return _topUps == 0 ? "Walk a curved path around the room while looking around"
                                : "Walk to a part of the room you have not covered yet";
        }
    }

    // PC の棄却理由（gates.check_geometry）の先頭。変えるときは gates.py と対で直す（test_gates.py が検査）
    private const string GuideNotEnoughMovement = "not enough movement";
    private const string GuidePathTooStraight   = "path too straight";

    /// <summary>送った枚数に比べて PC が採用した枚数が少ない（照明・模様の無い壁などで照合できていない）。
    /// 品質の判定ではなく、指示を選ぶための目安（判定は PC 側）。</summary>
    private bool FewRecognized
    {
        get
        {
            int sent = uploader != null ? uploader.SentCount : 0;
            return sent >= 8 && FreshAccepted * 2 < sent;
        }
    }

    // ================= ライフサイクル =================

    private void Awake()
    {
        _a = new PressTracker(tapMaxS);
        _b = new PressTracker(tapMaxS);
        _y = new PressTracker(tapMaxS);
        if (align    == null) align    = FindFirstObjectByType<AlignmentManager>();
        if (anchors  == null) anchors  = FindFirstObjectByType<AnchorPersistence>();
        if (uploader == null) uploader = FindFirstObjectByType<KeyframeUploader>();
        if (udp      == null) udp      = FindFirstObjectByType<UDPManager>();
        if (align == null) Debug.LogError("[DemoController] AlignmentManager が見つかりません。");
        if (anchors == null) Debug.LogWarning("[DemoController] AnchorPersistence が無いので、位置合わせは保存されません。");

        // **保存ファイルを実験と分ける**（同じアプリなので persistentDataPath は共通）。
        // 分けないと、実験の試行開始（ClearSaved）がデモの保存を消してしまう。
        // AnchorPersistence は Start で復元を始めるので、Awake のうちに設定する（Awake は全 Start より先）。
        if (anchors != null) anchors.FileKey = AppMode.DemoFileKey;
    }

    private void Start()
    {
        if (anchors != null)
        {
            anchors.AutoSaveOnLock = false;          // 保存の時機はここで決める（SaveNow）
            anchors.OnSaveFinished += OnSaveFinished;
        }
        if (align != null)
        {
            align.OnAutoAligned += OnAutoAligned;
            align.AcceptAutoResults = false;         // 再位置合わせを始めるまで受け付けない
            align.ResetButtonEnabled = false;        // 初期姿勢は展示では無意味。手動調整中の Y＋B で姿勢が飛ぶのも防ぐ
        }
        if (uploader != null)
        {
            uploader.ManualGripEnabled = false;      // 左グリップは来場者が握るので送信に使わない
            uploader.SetAuto(false);
        }

        // 原点の監視はシーンに置かれていなくても必ず効かせる（ExperimentController と同じ方針）
        if (tracking == null) tracking = FindFirstObjectByType<TrackingOriginWatcher>();
        if (tracking == null)
        {
            tracking = gameObject.AddComponent<TrackingOriginWatcher>();
            Debug.Log("[DemoController] TrackingOriginWatcher が無かったので実行時に追加しました");
        }
        tracking.OnOriginChanged += OnOriginChanged;
        OVRManager.HMDMounted   += OnHmdMounted;
        OVRManager.HMDUnmounted += OnHmdUnmounted;

        _enteredT = Time.realtimeSinceStartup;
        Enter(DemoState.Locating);
        Debug.Log("[DemoController] デモ展示モードで起動（保存した位置合わせを探します。スタッフパネル: Y+B 長押し）");
    }

    private void OnDestroy()
    {
        if (anchors != null) anchors.OnSaveFinished -= OnSaveFinished;
        if (align != null) align.OnAutoAligned -= OnAutoAligned;
        if (tracking != null) tracking.OnOriginChanged -= OnOriginChanged;
        OVRManager.HMDMounted   -= OnHmdMounted;
        OVRManager.HMDUnmounted -= OnHmdUnmounted;
    }

    private void Update()
    {
        if (align == null) return;
        HandleInput();
        ApplyInputMode();

        switch (_state)
        {
            case DemoState.Locating:
                if (Verified) { Enter(DemoState.Showing); break; }
                if (anchors == null || anchors.State == AnchorPersistence.RestoreState.NoSave
                    || anchors.State == AnchorPersistence.RestoreState.ModelMismatch
                    || anchors.State == AnchorPersistence.RestoreState.Disabled)
                { Enter(DemoState.NeedsAlignment); break; }
                if (StateElapsedS > restoreWaitS) Enter(DemoState.NeedsAlignment);   // 裏では探し続ける
                break;

            case DemoState.NeedsAlignment:
                if (Verified) { SetNotice("Saved alignment found"); Enter(DemoState.Showing); }
                break;

            case DemoState.Showing:
                if (_reverifyPending)
                {
                    if (Verified)
                    {
                        _reverifyPending = false;
                        if (_showOnVerify) { _showOnVerify = false; align.FadeInSplats(fadeInS); }
                    }
                    else if (Time.realtimeSinceStartup - _reverifyT > reverifyGraceS) Enter(DemoState.Relocating);
                }
                break;

            case DemoState.Relocating:
                if (Verified) Enter(DemoState.Showing);
                break;

            case DemoState.Aligning:
                UpdateAligning();
                break;

            case DemoState.Saving:
                if (StateElapsedS > saveTimeoutS)
                {
                    _lastSaveFailed = true;
                    SetNotice("Saving timed out — this alignment will be lost after a restart");
                    Enter(DemoState.Showing);
                }
                break;

            case DemoState.Manual:
                // A（AlignmentManager.Lock）で確定したら保存する
                if (align.State == AlignmentManager.AlignState.Locked)
                {
                    Enter(DemoState.Saving);
                    _originMovedWhileSaving = false;
                    if (anchors != null) anchors.SaveNow("manual");
                }
                break;
        }
    }

    /// <summary>状態に応じて、手動の姿勢操作・裏の復元の採用を許すかを毎フレーム同期する。</summary>
    private void ApplyInputMode()
    {
        align.PoseControls = _state == DemoState.Manual
            ? AlignmentManager.PoseControlMode.Full
            : AlignmentManager.PoseControlMode.Locked;   // 来場者の誤操作で姿勢が動かないように（X は残る）
        if (uploader != null) uploader.ManualGripEnabled = false;
        // 再位置合わせ・手動調整・保存の最中に、遅れて見つかった古いアンカーが姿勢を奪わないように
        if (anchors != null) anchors.AllowAdopt = Idle;
    }

    private void Enter(DemoState s)
    {
        DemoState prev = _state;
        _state = s;
        _stateT = Time.realtimeSinceStartup;
        switch (s)
        {
            // 合っている確信が無い局面では見せない（Specification.md §15.4-1）
            case DemoState.Locating:
            case DemoState.Relocating:
            case DemoState.NeedsAlignment:
            case DemoState.Aligning:
                align.SetSplatVisible(false);
                break;
            case DemoState.Showing:
            case DemoState.Saving:          // 確定した姿勢なので保存を待たずに見せる
                _reverifyPending = false; _showOnVerify = false;
                align.FadeInSplats(fadeInS);
                break;
            case DemoState.Manual:
                align.SetSplatVisible(true);
                break;
        }
        Debug.Log(prev == s ? $"[DemoController] → {s}" : $"[DemoController] {prev} → {s}");
    }

    private void SetNotice(string s)
    {
        Notice = s;
        _noticeT = Time.realtimeSinceStartup;
        Debug.Log("[DemoController] notice: " + s);
    }

    // ================= 入力 =================

    private void HandleInput()
    {
        _a.Update(OVRInput.Get(OVRInput.RawButton.A));
        _b.Update(OVRInput.Get(OVRInput.RawButton.B));
        _y.Update(OVRInput.Get(OVRInput.RawButton.Y));

        // Y＋B 同時長押し: スタッフパネルの開閉
        _combo.Update(_y.Down && _b.Down);
        if (_combo.Down)
        {
            _y.Taint(); _b.Taint();   // この押下は短押しとして扱わない
            if (_combo.LongPress(staffComboHoldS))
            {
                if (_state == DemoState.Manual) RejectPulse();   // 手動調整を開いたまま閉じない（先に Y か A で終える）
                else SetStaffMode(!_staff);
            }
        }

        if (!_staff) return;   // 来場者の間は A/B/Y に何も割り当てない（X は AlignmentManager が受ける）

        // 両スティック長押し: 実験へ戻る（スタッフパネルを開いているときだけ）
        if (_experimentHold.Update(experimentSwitchHoldS))
        {
            if (CanSwitchToExperiment) SwitchToExperiment(); else RejectPulse();
        }

        if (_state == DemoState.Manual)
        {
            // A（Lock して保存）は AlignmentManager が受ける。ここは Y（取り消し）だけ
            if (_y.Tapped) ToggleManual();
            return;
        }

        if (_a.LongPress(clearHoldS))
        {
            if (CanClearSaved) ClearSavedAlignment(); else RejectPulse();
        }
        if (_a.Tapped)
        {
            if (CanCancelAlign) CancelAlign();
            else if (CanRealign) Realign();
            else RejectPulse();
        }
        if (_b.Tapped) { if (CanShowAnyway) ShowAnyway(); else RejectPulse(); }
        if (_y.Tapped) { if (CanManual) ToggleManual(); else RejectPulse(); }
    }

    // ================= スタッフ操作（HUD のボタンからも呼ぶ） =================

    public void SetStaffMode(bool on)
    {
        if (_staff == on) return;
        if (!on && _state == DemoState.Manual) ToggleManual();   // 手動調整を開いたまま来場者に渡さない
        _staff = on;
        Pulse(0.6f);
        Debug.Log("[DemoController] staff mode = " + on);
    }

    /// <summary>実験（GSTestScene）へ戻る。保存した位置合わせはそのまま残り、次にデモへ入ったときに復元される。</summary>
    public void SwitchToExperiment()
    {
        if (!CanSwitchToExperiment) { RejectPulse(); return; }
        Debug.Log("[DemoController] 実験モードへ戻ります（デモの保存は残します）");
        AppMode.LoadExperiment();
    }

    /// <summary>PC で自動位置合わせをやり直す。古い保存は、新しい解が確定して保存できるまで消さない。</summary>
    public void Realign()
    {
        if (!CanRealign) { RejectPulse(); return; }
        if (_alignCo != null) StopCoroutine(_alignCo);
        _alignCo = StartCoroutine(StartAlignRoutine(restart: false));
    }

    private IEnumerator StartAlignRoutine(bool restart)
    {
        _busy = true;
        _alignActive = false;
        LastAlignFailure = "";
        if (uploader != null) uploader.SetAuto(false);
        align.AcceptAutoResults = false;
        _alignAttempt++;
        string label = $"demo-{DateTime.Now:yyyyMMdd-HHmmss}-{_alignAttempt:D2}";

        // **Quest と PC を対でリセットする**（片方だけでは前の収集が混ざる。ImplementationSpec.md §9.2）。
        // ラベルは実験の試行（trial-NN）と衝突しない形にする（解析は trial.json の label で照合する）。
        // RESET 前のセッション名を覚えておき、PONG がこれと変わるまで PC の枚数を信じない（SessionFresh）。
        _sessionBeforeReset = udp != null ? udp.ServerSession : "";
        if (udp != null) udp.SendReset("{\"trial\":0,\"label\":\"" + label + "\",\"mode\":\"demo\"}");
        align.ResetForNewTrial();
        if (anchors != null) anchors.ResetWalk();   // アンカーの置き場所はこれから歩く軌跡から選ぶ
        if (tracking != null) tracking.Mark();
        Enter(DemoState.Aligning);
        if (restart) SetNotice("Tracking moved — alignment restarted. Keep walking.");

        float t0 = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - t0 < resetSettleS) yield return null;   // PC が新セッションを切るのを待つ
        if (_state == DemoState.Aligning)
        {
            align.AcceptAutoResults = true;
            if (uploader != null) uploader.ResetForNewTrial(startAuto: true);
            // 最初の収集。新しいセッションは採用枚数 0 から始まるので、起点は 0 に固定する
            // （この時点の PONG はまだ前のセッションの枚数を返していることがある）。
            _topUps = 0;
            BeginCollect(0, collectTargetAccepted);
            _pcIssue = PcStatus.Ok; _pcIssueT = -1f;
            _lastDone = -1; _stallSince = -1f; _busySince = -1f;
            _alignActive = true;
            Debug.Log($"[DemoController] 再位置合わせ開始 label={label}（採用 {collectTargetAccepted} 枚 / {collectMaxS:F0}s まで集める）");
        }
        _busy = false;
        _alignCo = null;
    }

    /// <summary>キーフレームの収集を始める（最初の収集・追加収集）。目標は PC の採用枚数（累計）で持つ。</summary>
    private void BeginCollect(int startAccepted, int targetAccepted)
    {
        _alignPhase = AlignPhase.Collect;
        _collectStartAccepted = startAccepted;
        _targetAccepted = Mathf.Max(targetAccepted, startAccepted + 1);
        _phaseT = Time.realtimeSinceStartup;
        _caughtUpT = -1f;
        _collectActiveS = 0f;
        _paused = false;
        if (uploader != null) uploader.SetAuto(true);   // reqId は続き番号のまま（同じセッションに足す）
    }

    /// <summary>再位置合わせの段階を進める（Specification.md §15.6b）。
    ///
    /// Collect … PC の採用枚数が目標に達するか、送信していた時間が上限に達したら送信を止め、Wait へ。
    ///           **PC の処理待ちが溜まったら送信を一時停止する**（バックプレッシャー）。送り続けると
    ///           PC に溜まり、溜まった分を処理し終えるまで結果が遅れ、CV が次のセッションへ移れない
    ///           （実機で 25 枚溜まり、再試行が「CV worker not running」で失敗した）。
    /// Wait    … PC が届いた分をすべて処理し終え（cv_done ≥ kf かつ処理中でない）、結果が届くまで待つ。
    ///           確定すれば OnAutoAligned で Saving へ移っているので、ここに残っていれば未確定
    ///           → 追加収集（Top-up）。maxTopUps 回足しても駄目なら諦めて保存済みの位置合わせへ戻る。
    /// **止めて待つだけにはしない**: 新しい解は新しいキーフレームが届いたときにしか作られず、確定には
    /// 独立な解の連続一致が要るので、止めたままだと 2 つ目の解が二度と来ないことがある。
    ///
    /// **PC が遅くても正常に終わる**: PC が生きていて処理が進んでいる限り、時間では打ち切らない
    /// （収集の合計時間・待機の合計時間に上限を置かない）。止めるのは次のときだけ（<see cref="CheckPcStopped"/>）:
    ///   - run.py の無応答・CV の生存信号の途絶（CV は処理中も別スレッドで書くので、途絶える＝死んだ）
    ///   - このセッションのキーフレームが溜まっているのに、処理中でもなく処理済み枚数が増えない（＝処理していない）
    ///   - このセッションで処理中のまま、長時間 1 枚も終わらない（＝固まった）
    /// 前のセッションを片付けている間（cv_on が別のセッション）は、遅いだけなので待つ。</summary>
    private void UpdateAligning()
    {
        if (!_alignActive) return;   // RESET 後の待ち（resetSettleS）の間
        float now = Time.realtimeSinceStartup;

        PcStatus st = GetPcStatus();
        if (CheckPcStopped(st, now)) return;

        if (_alignPhase == AlignPhase.Collect)
        {
            // バックプレッシャー: 処理待ちが溜まったら止め、減ったら再開する（ヒステリシス）
            int backlog = PcBacklog;
            if (!_paused && backlog >= backlogPauseAt)
            {
                _paused = true;
                if (uploader != null) uploader.SetAuto(false);
                Debug.Log($"[DemoController] PC の処理待ち {backlog} 枚 → 送信を一時停止");
            }
            else if (_paused && backlog <= backlogResumeAt)
            {
                _paused = false;
                if (uploader != null) uploader.SetAuto(true);
                Debug.Log($"[DemoController] PC が追いついた（処理待ち {backlog} 枚）→ 送信を再開");
            }
            if (!_paused) _collectActiveS += Time.unscaledDeltaTime;

            // 収集の区切りは「採用枚数」と「実際に送信していた時間」だけ。PC 待ちで止まっている時間は
            // 数えないので、PC が遅くても集める量は減らない（合計時間の上限は置かない）
            bool reached = FreshAccepted >= _targetAccepted;
            if (reached || _collectActiveS >= PhaseLimitS)
            {
                if (uploader != null) uploader.SetAuto(false);
                Debug.Log($"[DemoController] 収集を止めて結果を待つ（採用 {CollectAccepted}/{CollectTarget}・" +
                          $"送信 {_collectActiveS:F0}s / 全体 {PhaseElapsedS:F0}s・追加 {_topUps}/{maxTopUps}）");
                _alignPhase = AlignPhase.Wait;
                _phaseT = now;
                _caughtUpT = -1f;
                _paused = false;
                Pulse(0.6f);   // 歩いているスタッフに「止まってよい」を伝える
            }
            return;
        }

        // ---- Wait（上限時間なし。PC が処理し終えるまで待つ。止まったかどうかは CheckPcStopped が見る） ----
        bool caughtUp = PhaseElapsedS >= inFlightS && SessionFresh && !udp.CvBusy
                        && udp.CvDoneCount >= udp.ServerKeyframeCount;
        if (!caughtUp) _caughtUpT = -1f;
        else if (_caughtUpT < 0f) _caughtUpT = now;
        bool settled = _caughtUpT >= 0f && now - _caughtUpT >= resultGraceS;
        if (!settled) return;

        if (_topUps < maxTopUps)
        {
            _topUps++;
            int acc = FreshAccepted;
            BeginCollect(acc, acc + topUpAccepted);
            SetNotice($"Not aligned yet — walk a bit more ({_topUps}/{maxTopUps})");
            Pulse(0.6f);
            Debug.Log($"[DemoController] 未確定 → 追加収集 {_topUps}/{maxTopUps}（採用 {topUpAccepted} 枚）" +
                      $" PC: {(SessionFresh ? udp.CvReason : "")}");
        }
        else FailAlign(AlignFailureReason);
    }

    /// <summary>PC が**止まった**（遅いだけではない）と判断できたら、理由を出して諦め true を返す。
    /// 遅いだけ（処理中・前のセッションを片付け中）なら何もしない — 時間では打ち切らない。</summary>
    private bool CheckPcStopped(PcStatus st, float now)
    {
        // 1) run.py の無応答・CV の生存信号の途絶・CV がどこにもいない
        float limit = st == PcStatus.Down || st == PcStatus.Stale ? pcLostFailS
                    : st == PcStatus.Starting ? cvStartWaitS
                    : -1f;                                   // Ok / Busy / Switching は生きている
        if (limit < 0f) { _pcIssue = PcStatus.Ok; _pcIssueT = -1f; }
        else
        {
            if (st != _pcIssue) { _pcIssue = st; _pcIssueT = now; }   // 異常の種類が変わったら数え直す
            if (now - _pcIssueT > limit) { FailAlign(PcIssueText(st)); return true; }
        }

        // 処理済み枚数が増えたら「進んでいる」。止まり・固まりの判定はやり直す
        int done = SessionFresh ? udp.CvDoneCount : -1;
        bool progressed = done != _lastDone;
        _lastDone = done;

        // 2) 止まった: このセッションのキーフレームが溜まっているのに、CV は生きていて処理中でもなく、進まない
        //    （localize がこのセッションを見ていない。run.py --out と localize --watch の食い違いなど）。
        //    localize は 1 秒ごとに見に来るので、正常なら溜まった時点ですぐ処理中になる。
        if (st != PcStatus.Ok || PcBacklog <= 0 || progressed) _stallSince = -1f;
        else if (_stallSince < 0f) _stallSince = now;
        else if (now - _stallSince > waitStallS)
        {
            FailAlign($"the CV worker is not processing this session ({PcBacklog} keyframes waiting)"
                      + " — check the [cv] lines in the run.py window");
            return true;
        }

        // 3) 固まった: このセッションで処理中のまま、長時間 1 枚も終わらない。
        //    PC 待ちで送信を止めるので 1 回の処理は数枚まで。遅いだけならいずれ 1 枚は終わる。
        if (st != PcStatus.Busy) _busySince = -1f;
        else if (_busySince < 0f || progressed) _busySince = now;
        else if (now - _busySince > busyNoProgressFailS)
        {
            string span = busyNoProgressFailS >= 120f ? $"{busyNoProgressFailS / 60f:F0} min" : $"{busyNoProgressFailS:F0} s";
            FailAlign($"the PC seems stuck — no keyframe finished in {span}");
            return true;
        }
        return false;
    }

    /// <summary>諦めた理由（英語）。PC の棄却理由があればそれを、無ければ照合の状況から選ぶ。</summary>
    private string AlignFailureReason
    {
        get
        {
            string why = SessionFresh ? udp.CvReason : "";
            if (!string.IsNullOrEmpty(why) && !udp.CvPublished) return "the PC rejected the solution: " + why;
            if (FewRecognized) return $"the PC recognized only {FreshAccepted} of {uploader.SentCount} keyframes";
            return "the solutions did not agree — walk a larger loop around the room";
        }
    }

    private void FailAlign(string why)
    {
        if (_alignCo != null) { StopCoroutine(_alignCo); _alignCo = null; }
        _busy = false;
        _alignActive = false;
        if (uploader != null) uploader.SetAuto(false);
        align.AcceptAutoResults = false;
        LastAlignFailure = string.IsNullOrEmpty(why) ? "no confirmed alignment" : why;
        Debug.LogWarning("[DemoController] 再位置合わせを諦めました: " + LastAlignFailure);
        RejectPulse();
        ReturnToSaved("Could not align — " + LastAlignFailure);
    }

    public void CancelAlign()
    {
        if (_state != DemoState.Aligning) return;
        if (_alignCo != null) { StopCoroutine(_alignCo); _alignCo = null; }
        _busy = false;
        _alignActive = false;
        if (uploader != null) uploader.SetAuto(false);
        align.AcceptAutoResults = false;
        ReturnToSaved("Re-align cancelled");
    }

    /// <summary>手動調整の開始／取り消し。確定（保存）は A（AlignmentManager.Lock）で行う。</summary>
    public void ToggleManual()
    {
        if (_state == DemoState.Manual) { ReturnToSaved("Manual adjust cancelled"); return; }
        if (!CanManual) { RejectPulse(); return; }
        if (uploader != null) uploader.SetAuto(false);
        align.AcceptAutoResults = false;
        align.Unlock();   // Locked なら Adjusting へ（アンカー追従からも外れる）
        Enter(DemoState.Manual);
    }

    /// <summary>一致が揃わないが、見つかったアンカーで表示する（未検証。スタッフが目で確かめる前提）。</summary>
    public void ShowAnyway()
    {
        if (!CanShowAnyway) { RejectPulse(); return; }
        anchors.ForceShowAnyway();
        SetNotice("Showing without cross-check (unverified)");
    }

    /// <summary>保存した位置合わせを消す（A 長押し）。元に戻せないので長押しにしてある。</summary>
    public void ClearSavedAlignment()
    {
        if (!CanClearSaved) { RejectPulse(); return; }
        anchors.ClearSaved();
        align.Unlock();
        _lastSaveFailed = false;
        SetNotice("Saved alignment cleared");
        Pulse(1f);
        Enter(DemoState.NeedsAlignment);
    }

    /// <summary>HUD の「Clear saved」を Poke されたとき。元に戻せない操作なので Poke では実行せず、
    /// 物理ボタンの長押しを案内するだけにする。</summary>
    public void HintClearHold()
    {
        SetNotice($"Hold A for {clearHoldS:F0} s to clear the saved alignment");
        RejectPulse();
    }

    /// <summary>作業を取り消して、保存済みのアンカーの姿勢へ戻す。揃っていなければ探す／待つ。</summary>
    private void ReturnToSaved(string notice)
    {
        SetNotice(notice);
        if (anchors != null && anchors.TryAdoptCurrent()) { Enter(DemoState.Showing); return; }
        bool searching = anchors != null && (anchors.State == AnchorPersistence.RestoreState.Loading
                                             || anchors.State == AnchorPersistence.RestoreState.Searching);
        Enter(searching ? DemoState.Locating : DemoState.NeedsAlignment);
    }

    // ================= イベント =================

    private void OnAutoAligned(Sim3Result r)
    {
        if (_state != DemoState.Aligning) return;
        // 確定したら凍結する（来場者の目の前でシーンが跳ねるのを避ける。Specification.md §15.7）
        // 収集中でも待機中でも、確定した時点で段階の判定は終わり
        _alignActive = false;
        Debug.Log($"[DemoController] 確定（{_alignPhase}・追加 {_topUps}/{maxTopUps}・採用 {FreshAccepted} 枚）");
        if (uploader != null) uploader.SetAuto(false);
        align.AcceptAutoResults = false;
        _originMovedWhileSaving = false;
        Enter(DemoState.Saving);
        if (anchors != null) anchors.SaveNow("auto");
        else { _lastSaveFailed = true; SetNotice("No AnchorPersistence — alignment will not be saved"); Enter(DemoState.Showing); }
    }

    private void OnSaveFinished(bool ok)
    {
        if (_state != DemoState.Saving) return;
        if (_originMovedWhileSaving)
        {
            // 保存の途中で原点が動くと、splatRoot（旧座標系）と新しいアンカー（新座標系）の相対姿勢が壊れる。
            // 壊れた保存を次の起動で復元させないよう消して、やり直してもらう。
            anchors.ClearSaved();
            align.Unlock();
            _lastSaveFailed = true;
            SetNotice("Tracking moved while saving — please re-align");
            Enter(DemoState.NeedsAlignment);
            return;
        }
        _lastSaveFailed = !ok;
        SetNotice(ok ? $"Saved with {anchors.SavedAnchorCount} anchors — it will come back after a restart"
                     : "Could not save — this alignment will be lost after a restart");
        Pulse(ok ? 0.6f : 1f);
        Enter(DemoState.Showing);
    }

    /// <summary>トラッキング原点が動いた（再センタリング・トラッキングロスからの復帰・空間の再ローカライズ）。</summary>
    private void OnOriginChanged(TrackingOriginWatcher.Cause cause, float m, float deg)
    {
        if (anchors != null) anchors.ResetWalk();   // 動く前の軌跡は今の座標系では別の場所を指す
        switch (_state)
        {
            case DemoState.Aligning:
                // 収集したキーフレームが 2 つの座標系に分かれると Sim(3) が壊れる → 対でリセットして集め直す
                if (_alignCo != null) StopCoroutine(_alignCo);
                _alignCo = StartCoroutine(StartAlignRoutine(restart: true));
                break;
            case DemoState.Saving:
                _originMovedWhileSaving = true;
                break;
            case DemoState.Showing:
                // アンカーが追跡できていれば吸収される。揃わなければ非表示にして確かめ直す
                BeginReverify(show: false);
                break;
            case DemoState.Manual:
                SetNotice("Tracking moved — check the overlay before locking");
                break;
        }
    }

    private void OnApplicationPause(bool paused)
    {
        if (paused) return;
        // スリープからの復帰。原点が動いていることがあるので、表示中なら確かめ直し、収集中ならやり直す
        if (_state == DemoState.Showing) BeginReverify(show: false);
        else if (_state == DemoState.Aligning)
        {
            if (_alignCo != null) StopCoroutine(_alignCo);
            _alignCo = StartCoroutine(StartAlignRoutine(restart: true));
        }
    }

    private void OnHmdMounted()
    {
        // 次の来場者がかぶった。前の来場者が X で消したままなら、確かめてから出し直す
        if (_state == DemoState.Showing) BeginReverify(show: true);
    }

    private void OnHmdUnmounted()
    {
        if (_staff) SetStaffMode(false);   // スタッフパネルを開いたまま次の来場者に渡さない
    }

    private void BeginReverify(bool show)
    {
        _reverifyPending = true;
        _reverifyT = Time.realtimeSinceStartup;
        if (show) _showOnVerify = true;
    }

    // ================= 触覚 =================

    private void Pulse(float amp) => Haptics.PulseBoth(this, amp);
    private void RejectPulse() => Haptics.RejectBoth(this);
}
