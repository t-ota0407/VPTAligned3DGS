using System.Collections;
using System.Globalization;
using UnityEngine;

/// <summary>
/// 実験の試行制御。
///
/// 評価は「データ収集時間 vs 位置合わせ精度」の曲線に統一してある。
/// **1 試行 = 一定時間データを収集するだけ。** 精度は、保存済みキーフレームから時刻ごとに
/// 姿勢を解き直し、実験前に 1 回だけ測ったコントロールポイント（→ [[ControlPointMeasure]]）
/// と突き合わせて後から算出する。
/// ロックまでの時間は品質ゲートの閾値の関数でしかないため指標には使わない。
///
/// 解決する問題:
///   試行は独立でなければならないが、Quest 側の状態（採用済み Sim(3)・splat 姿勢・送信カウンタ）と
///   PC 側の状態（session フォルダ・Sim(3) 蓄積バッファ）の両方を捨てないと独立にならない。
///   素の系では 1 試行ごとに Quest アプリと run.py を両方再起動する必要があった。
///   本クラスはその一連を 1 アクションにまとめる:
///     1. 自動キャプチャを止め、送信中のコルーチンを畳む
///     2. PC へ RESET(type=4) を送る → PC は新しい session_* を切り、WSL2 の watch は
///        最新セッション追従で Cc/Cw を捨てる
///     3. Quest 側の状態を初期化（AlignmentManager / KeyframeUploader / アンカー）
///     4. ロガーの試行を開始し、自動キャプチャを再開してタイマーを回す
///
/// 収集は制限時間まで続ける。解が確定しても送信を止めない（曲線の横軸を最後まで埋めるため）。
///
/// 注意: AnchorPersistence.experimentMode は自動で ON にする。前試行の姿勢がアンカーから
///       復元されると、起動直後に「ロック済み」と記録され測定が壊れるため。
///
/// **トラッキング原点が動いた試行は自動的に破棄する**（→ [[TrackingOriginWatcher]]）。
/// Sim(3) は「Quest ワールドは固定」という前提の上に成り立っているので、recenter や
/// 再ローカライズで原点がヨー回転すると、収集中なら対応が壊れ、適用後なら splat が実世界から
/// ずれる。どちらも残差には現れないため、検出しないと「位置合わせの失敗」として静かに混入する。
/// 破棄した試行は番号を消費せず、ラベル（PC のセッションと突き合わせる鍵）だけを消費する。
/// </summary>
public class ExperimentController : MonoBehaviour
{
    // 評価は「収集時間 vs 精度」の 1 本の曲線に統一したので、E1/E2 というモードの区別は無い。
    // 1 試行 = 「一定時間データを収集」だけ。CP の測量は試行の外（実験前に 1 回）。
    // 試行に成否は無い。所定時間データを集めて終わり（曲線はその全区間から後で引く）。
    public enum TrialState { Idle, Running, Collected }

    [Header("References（未設定なら自動取得）")]
    [SerializeField] private AlignmentManager align;
    [SerializeField] private KeyframeUploader uploader;
    [SerializeField] private UDPManager udp;
    [SerializeField] private AnchorPersistence anchors;
    [SerializeField] private ExperimentLogger logger;
    [SerializeField] private ControlPointMeasure cpMeasure;
    [SerializeField] private ControlPointEdit cpEdit;
    [Tooltip("トラッキング原点の監視。未設定なら実行時に自作する（シーンを編集しなくても効く）。")]
    [SerializeField] private TrackingOriginWatcher tracking;

    [Header("Settings")]
    [Tooltip("計画している試行数。この数に達したら「全試行 完了」を表示し、以降は開始できない。\n" +
             "余分に回してログを濁らせないため。やり直したいときは値を増やす。")]
    [SerializeField] private int plannedTrials = 10;
    [Tooltip("1 試行のデータ収集時間[s]。この間ずっと歩き回ってもらう。\n" +
             "曲線の横軸の上限になるので、短くすると曲線が途中までしか描けない。")]
    [SerializeField] private float collectSeconds = 60f;
    [Tooltip("RESET 送信後、PC が新セッションを作るのを待つ時間[s]。この間の送信は前セッションに混ざる恐れがある。")]
    [SerializeField] private float resetSettleS = 1.0f;
    [Tooltip("試行開始時に自動キャプチャを ON にする（論文の「見回すだけ」運用）。")]
    [SerializeField] private bool autoCaptureOnStart = true;
    [Tooltip("実験中はアンカー永続化を止める（試行の独立性のため。通常は ON のままにする）。")]
    [SerializeField] private bool forceExperimentMode = true;

    [Header("Input")]
    [Tooltip("実験用のコントローラ割当を有効にする。ON の間、姿勢を変える手動操作は封じられる" +
             "（B=初期姿勢リセット等の誤爆で試行が無効になるのを防ぐ）。\n" +
             "  R トリガー : CP 測量中・編集中は「その点を仮置き」（何度でも置き直せる。確定は A）\n" +
             "  L トリガー : 仮置きがあれば「取り消し」、無ければ「1 つ前の点へ戻る」\n" +
             "  B（右）短押: CP 編集中は「この点をビルド値へ戻す」、それ以外は 試行 開始 / 次へ\n" +
             "  B（右）長押: CP 編集中のみ「動かした点をすべて戻す」\n" +
             "  A（右）短押: CP 測量中・編集中は「仮置きを確定して次の点へ」\n" +
             "               （編集中で仮置きが無ければ「置かずに次の点へ」）、\n" +
             "               どちらでもなければ CP 測量の開始。※測量の中断は Y に一本化\n" +
             "  A（右）長押: model 側 CP の VR 編集 開始 / 終了（位置合わせ前のみ）\n" +
             "  Y（左）    : 試行中は「中断」、測量中は「測量の中断」、CP 編集中は「編集の終了」、\n" +
             "               それ以外は「先端校正の開始/終了」（時間的に競合しない）\n" +
             "**HUD のボタンはすべてこのどれかに対応する。** Poke（パネルを手で突く）は\n" +
             "狙いにくいので、物理ボタンだけで一通り操作できる状態を保つこと。\n" +
             "手動で粗合わせしたいときはここを OFF にする。HUD に切替ボタンは置いていない\n" +
             "（実験中に誤爆すると、その試行の測定が無効になるため）。")]
    [SerializeField] private bool experimentInputEnabled = true;
    [Tooltip("A / B（右）を「長押し」と見なす秒数。")]
    [SerializeField] private float aLongPressS = 1.0f;
    [Tooltip("左右のスティックを同時にこの秒数押し込むと、デモ展示モード（DemoScene）へ切り替える。\n" +
             "デモは CP の設定も先端校正も要らないので、最初の画面からそのまま入れる。\n" +
             "この画面ではスティック押込に他の役割が無い（姿勢操作は封じられている）ので衝突しない。")]
    [SerializeField] private float demoSwitchHoldS = 3f;

    private readonly AppMode.SticksHold _demoHold = new AppMode.SticksHold();

    // A / B の短押し／長押し判定。押し始めの時刻と、長押しを既に発火したか。
    private float _aDownT = -1f;
    private bool  _aLongFired;
    private float _bDownT = -1f;
    private bool  _bLongFired;

    // ---- 試行の状態 ----
    private TrialState _state = TrialState.Idle;
    // 成立した試行の数。原点が動いて破棄した試行はここに数えない（＝やり直せる）。
    private int   _validTrials;
    // ラベル用の通し番号。**決して戻さない。** 破棄した試行にも固有のラベルを与えないと、
    // PC 側の session_*/trial.json と 1 対 1 に対応しなくなり、後から破棄分を除外できない。
    private int   _attempt;
    private float _trialStartT;      // 試行開始（RESET 直後）の時刻
    private float _firstKfT = -1f;   // 最初のキーフレーム送信時刻（収束時間の起点）
    private float _lockT = -1f;
    private int   _kfAtLock;
    private bool  _busy;
    private int   _lastSeenSent;
    // この試行で「結果確認のためにスプラットを出す」処理を済ませたか。
    // 一度きりにしないと、実験者が X で消しても毎フレーム出し直してしまう。
    private bool  _shownForMeasure;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public TrialState State => _state;
    /// <summary>計画した試行数を消化したか。HUD は完了表示にし、開始ボタンを塞ぐ。</summary>
    public bool AllTrialsDone => _validTrials >= plannedTrials && _state != TrialState.Running;
    public int PlannedTrials => plannedTrials;

    /// <summary>今すぐ次の試行を始めてよいか。HUD はこれでボタンの活性を決める。
    /// 編集中は始めない（編集は測量を無効化するので、どのみち SurveyReady が落ちる）。</summary>
    public bool CanStartTrial =>
        !_busy && !AllTrialsDone && _state != TrialState.Running && !EditingCPs && AllPreconditionsMet;

    /// <summary>model 側 CP を VR で編集中か。</summary>
    private bool EditingCPs => cpEdit != null && cpEdit.IsRunning;

    /// <summary>最初のキーフレーム送信から最初の自動ロックまでの秒数（未成立なら -1）。</summary>
    public float TimeToLock => (_lockT >= 0f && _firstKfT >= 0f) ? _lockT - _firstKfT : -1f;
    private int Sent => uploader != null ? uploader.SentCount : 0;

    // ---------------- HUD 用（表示に必要な素の値だけを出す。文言・色は HUD 側の責務） ----------------

    /// <summary>実験者に見せる大まかな局面。HUD はこれで表示を切り替える。</summary>
    public enum UiPhase { NeedsSetup, EditingCPs, Surveying, Ready, Running, Review, AllDone }

    public UiPhase Phase
    {
        get
        {
            if (EditingCPs) return UiPhase.EditingCPs;
            if (cpMeasure != null && cpMeasure.IsRunning) return UiPhase.Surveying;
            if (_state == TrialState.Running) return UiPhase.Running;
            // 収集が終わったら、結果を見て次へ進むだけ（CP は実験前に 1 回測ってある）
            if (_state == TrialState.Collected) return UiPhase.Review;
            if (AllTrialsDone) return UiPhase.AllDone;
            return AllPreconditionsMet ? UiPhase.Ready : UiPhase.NeedsSetup;
        }
    }

    /// <summary>いま何試行目か。破棄した試行は数えない。
    /// 収集が終わった時点で成立数に繰り入れるので、Collected 以降は加算しない
    /// （足すと最終試行の計測中に「11 / 10」と出てしまう）。</summary>
    public int   TrialIndex     => _state == TrialState.Running ? _validTrials + 1 : _validTrials;
    /// <summary>収集まで終わった試行の数（＝残り試行数の計算に使う）。</summary>
    public int   CompletedTrials => _validTrials;
    public int   KfAtLock       => _kfAtLock;

    // ---- トラッキング原点の異常（HUD 用） ----
    /// <summary>直近に試行を破棄した理由。破棄していなければ空。</summary>
    public string VoidNotice { get; private set; } = "";
    /// <summary>破棄してからの経過秒（未破棄なら大きな値）。HUD は一定時間だけ警告を出す。</summary>
    public float VoidNoticeAgeS =>
        _voidNoticeT < 0f ? float.MaxValue : Time.realtimeSinceStartup - _voidNoticeT;
    /// <summary>トラッキング原点が直近で安定しているか（準備チェック用）。</summary>
    public bool TrackingSteady => tracking == null || tracking.SinceLastEventS > 10f;
    /// <summary>この試行中に原点が動いた回数。</summary>
    public int TrackingEventsThisTrial => tracking != null ? tracking.EventsSinceMark : 0;
    private float _voidNoticeT = -1f;
    public int   KfSent         => Sent;
    public float CollectSeconds       => collectSeconds;
    public float Elapsed        => _state == TrialState.Running ? Time.realtimeSinceStartup - _trialStartT : 0f;
    /// <summary>収集時間に対する経過割合 0..1（プログレスバー用）。</summary>
    public float CollectProgress => collectSeconds > 0f ? Mathf.Clamp01(Elapsed / collectSeconds) : 0f;
    public bool  ExperimentInputEnabled => experimentInputEnabled;

    // 準備チェック。実験者が「なぜ始められないか」をその場で判断できるようにする。
    //
    // ServerReady は **実際に PONG が返っていること**で判定する。送信先 IP を知っているだけ
    // （HasTarget）では不十分: IP は前回起動時の学習値が PlayerPrefs に残るため、run.py を
    // 起動していなくても埋まってしまい、「✓ サーバ」と出たまま試行が全部失敗する。
    public bool ServerReady => udp != null && udp.ServerAlive;
    /// <summary>WSL2 の CV ワーカが動いているか。これが false だとキーフレームは届くのに
    /// Sim(3) が返らないので、試行を始めても必ずタイムアウトする。</summary>
    public bool CvReady     => udp != null && udp.CvAlive;
    public bool TipReady    => tipCalib != null && tipCalib.IsCalibrated;
    /// <summary>model 側の CP が Editor で置かれているか。</summary>
    public bool PointsReady => cpMeasure != null && cpMeasure.MarkerCount > 0;
    /// <summary>実環境側の CP を測ってあるか。**実験の前に 1 回だけ**行い、全試行で使い回す
    /// （model 側も実物側も試行では変わらないため）。ワールド原点が動いたら無効化される。</summary>
    public bool SurveyReady => cpMeasure != null && cpMeasure.IsSurveyed;
    public bool AllPreconditionsMet =>
        ServerReady && CvReady && PointsReady && TipReady && SurveyReady && TrackingSteady;

    [SerializeField] private ControllerTipCalibration tipCalib;

    private void Awake()
    {
        if (align    == null) align    = FindFirstObjectByType<AlignmentManager>();
        if (uploader == null) uploader = FindFirstObjectByType<KeyframeUploader>();
        if (udp      == null) udp      = FindFirstObjectByType<UDPManager>();
        if (anchors  == null) anchors  = FindFirstObjectByType<AnchorPersistence>();
        if (logger   == null) logger   = FindFirstObjectByType<ExperimentLogger>();
        if (cpMeasure == null) cpMeasure = FindFirstObjectByType<ControlPointMeasure>();
        if (cpEdit    == null) cpEdit    = FindFirstObjectByType<ControlPointEdit>();
        if (tipCalib  == null) tipCalib  = FindFirstObjectByType<ControllerTipCalibration>();

        // **永続化の停止は Awake で行う（Start では遅い）。** AnchorPersistence は Start で復元を始めるが、
        // Start どうしの実行順は不定なので、ここが Start だと止める前に復元が走りうる
        // （GaussianSplatController の不透明度と同じ問題。ImplementationSpec.md §6.3）。
        // Awake はすべての Start より前に走るので、この順序問題が消える。
        if (forceExperimentMode && anchors != null) anchors.ExperimentMode = true;
    }

    private void Start()
    {
        if (align != null) align.OnAutoAligned += OnAutoAligned;

        // 監視はシーンに置かれていなくても必ず効かせる。無ければ自作する。
        // 「原点が動いたのに気づかない」が最も高くつく失敗なので、設定漏れで無効化されないようにする。
        if (tracking == null) tracking = FindFirstObjectByType<TrackingOriginWatcher>();
        if (tracking == null)
        {
            tracking = gameObject.AddComponent<TrackingOriginWatcher>();
            Debug.Log("[ExperimentController] TrackingOriginWatcher が無かったので実行時に追加しました");
        }
        tracking.OnOriginChanged += OnTrackingOriginChanged;

        ApplyInputMode();
        Debug.Log($"[ExperimentController] ready. 計画 {plannedTrials} 試行 / 収集 {collectSeconds}s " +
                  "（B=試行開始 / Y=中断。HUD からも操作可）");
    }

    /// <summary>実験用のボタン割当と、姿勢を変える手動操作の受付範囲を同期させる。
    ///
    /// **CP 測量中だけはスティック操作を通す。** 測量する時点ではまだ位置合わせされておらず、
    /// model 上の CP は実世界と無関係な場所（壁の向こう・天井の上）に出る。そこへ歩いて
    /// 行くことはできないので、球が実物の近くに来るところまでシーンを寄せられないと
    /// 「どの実物が CP_01 か」を球から読み取れない。トリガー（記録・1 つ戻る）と
    /// A/B/Y（実験操作）は測量側が使っているので、渡すのはスティックだけ。
    ///
    /// **model 側 CP の VR 編集中も同じ**（→ [[ControlPointEdit]]）。編集は位置合わせ前にしか
    /// 許さないので、モデル上の特徴に先端を当てるにはシーンを手元へ寄せる必要がある。
    ///
    /// 局面（測量中・編集中かどうか）は毎フレーム変わるので Update から呼ぶ。</summary>
    private void ApplyInputMode()
    {
        if (align == null) return;
        bool needSticks = (cpMeasure != null && cpMeasure.IsRunning) || EditingCPs;
        align.PoseControls =
            !experimentInputEnabled ? AlignmentManager.PoseControlMode.Full
            : needSticks            ? AlignmentManager.PoseControlMode.SticksOnly
                                    : AlignmentManager.PoseControlMode.Locked;
    }

    private void OnDestroy()
    {
        if (align != null) align.OnAutoAligned -= OnAutoAligned;
        if (tracking != null) tracking.OnOriginChanged -= OnTrackingOriginChanged;
    }

    // ---------------- トラッキング原点が動いたときの処理 ----------------

    /// <summary>Quest のワールド座標系が動いた。進行中の試行は成立しないので破棄する。
    ///
    /// 収集中に起きた場合はキーフレームの姿勢が途中から別座標系になり、対応が壊れる。
    /// 加えて、実験前に測量した実物の位置（ワールド座標）も動く前の座標系の値になるので、
    /// 以降の全試行の評価が狂う。どちらも残差やインライア率には現れないため、
    /// **続行させてはいけない**。</summary>
    private void OnTrackingOriginChanged(TrackingOriginWatcher.Cause cause, float m, float deg)
    {
        string why = tracking != null ? tracking.LastDescription : cause.ToString();
        // **測量した実物の位置は、動く前のワールド座標系の値になる。** 黙って使い続けると、
        // 以降の全試行に原点の移動ぶんの誤差が乗り、しかも数字からは見分けがつかない。
        if (cpMeasure != null) cpMeasure.InvalidateSurvey("Tracking moved after the survey (" + why + ")");

        if (_state == TrialState.Idle)
        {
            // 試行外なら壊れる試行が無い。ただし測量はやり直しになる。
            Debug.LogWarning("[ExperimentController] 試行外でトラッキング原点が動きました（" + why +
                             "）。CP の測量をやり直してください。");
            return;
        }
        VoidTrial(why);
    }

    /// <summary>進行中の試行を「無かったこと」にする。試行番号は消費しない。</summary>
    private void VoidTrial(string why)
    {
        bool countedAsCollected = _state == TrialState.Collected;
        _state = TrialState.Idle;
        if (uploader != null) uploader.SetAuto(false);
        // 収集完了として数えていた分を戻す（結果を確認している最中に原点が動いた場合）。
        if (countedAsCollected && _validTrials > 0) _validTrials--;
        logger?.EndTrial("void_tracking", "\"why\":\"" + why.Replace("\"", "'") + "\"," + TrialMetricsJson());

        VoidNotice = why; _voidNoticeT = Time.realtimeSinceStartup;
        Debug.LogError($"[ExperimentController] トラッキング原点が動いたため試行を破棄しました（{why}）。" +
                       "ヘッドセットを外さず、同じ空間のまま始め直してください。");
    }

    /// <summary>収集中は 3DGS を消し、試行が終わったときに出す（実験者の目視確認用）。
    ///
    /// 出すのは「収集が終わった時点」ではなく「**姿勢が適用された時点**」。CV の結果は
    /// 収集終了より遅れて届くことがあり、その間に出しても初期姿勢のスプラットが見えるだけで、
    /// 合っているかどうかの判断材料にならない。
    /// 一度出したらそれきりにするので、実験者が X で消して見比べるのは妨げない。
    /// なお論文の数字はこの見た目ではなく、保存済みキーフレームからの再計算で出す。</summary>
    private void UpdateSplatVisibilityForPhase()
    {
        if (align == null || _shownForMeasure) return;
        if (_state != TrialState.Collected) return;
        bool poseApplied = align.HasAcceptedSim3 || align.State == AlignmentManager.AlignState.Locked;
        if (!poseApplied) return;
        _shownForMeasure = true;
        align.SetSplatVisible(true);
        Debug.Log("[ExperimentController] 姿勢が適用されたので 3DGS を表示しました（目視確認用）");
    }

    // ---------------- デモ展示モードへの切り替え ----------------

    /// <summary>デモへ切り替えられない理由（HUD 用・英語）。切り替えられるなら空。
    ///
    /// 作業の途中では切り替えさせない（シーンごと捨てるので、途中の試行・測量・編集・校正が失われる）。
    ///
    /// **測り直しの効かない記録が PC へ未送達のときも不可**: Real CP の実測値は測り直すと座標系が変わって
    /// やり直しが効かず、端末の送信待ち（UDPManager の outbox）はシーンと一緒に消える（端末内の JSONL には残るが、
    /// `adb pull` しないと PC 側の解析に載らない）。
    /// 送信待ちがあるだけでは止めない — 起動時の `session_start` などは PC からの ACK を待って常に残っており、
    /// PC を起動していないデモでは永遠に切り替えられなくなる（実機確認前に Editor で判明）。</summary>
    public string DemoSwitchBlockReason
    {
        get
        {
            if (_busy || _state == TrialState.Running) return "Finish or abort the trial first";
            if (cpMeasure != null && cpMeasure.IsRunning) return "Finish the Real CP survey first";
            if (EditingCPs) return "Finish editing the Virtual CPs first";
            if (tipCalib != null && tipCalib.IsRunning) return "Finish the tip calibration first";
            if (HasMeasuredData && udp != null && udp.PendingEventCount > 0)
                return "Measured records are not on the PC yet — keep the app running";
            return "";
        }
    }

    /// <summary>このセッションで測り直しの効かないデータ（Real CP の実測・Virtual CP の編集・試行）を記録したか。</summary>
    private bool HasMeasuredData =>
        (cpMeasure != null && (cpMeasure.IsSurveyed || cpMeasure.DoneCount > 0))
        || (cpEdit != null && cpEdit.EditedCount > 0)
        || _attempt > 0;

    public bool  CanSwitchToDemo    => string.IsNullOrEmpty(DemoSwitchBlockReason);
    public float DemoSwitchProgress => _demoHold.Progress(demoSwitchHoldS);
    public float DemoSwitchHoldS    => demoSwitchHoldS;

    /// <summary>デモ展示モードへ切り替える（HUD のボタンと、両スティック長押しから）。</summary>
    public void SwitchToDemo()
    {
        string why = DemoSwitchBlockReason;
        if (!string.IsNullOrEmpty(why)) { Debug.LogWarning("[ExperimentController] デモへ切り替えられません: " + why); return; }
        Debug.Log("[ExperimentController] デモ展示モードへ切り替えます（CP・先端校正なし）");
        AppMode.LoadDemo();
    }

    private void Update()
    {
        // 両スティック長押し → デモ。experimentInputEnabled に依らず効かせる（最初の画面の入口なので）
        if (_demoHold.Update(demoSwitchHoldS)) SwitchToDemo();

        ApplyInputMode();          // 測量中だけスティックを通す。局面は毎フレーム変わる
        HandleExperimentInput();
        UpdateSplatVisibilityForPhase();

        // 試行の実行中は左グリップの手動送信を受け付けない。誤って押すと自動送信が止まったり
        // 余計なキーフレームが混ざって、その試行が壊れるため。状態遷移を取りこぼさないよう毎フレーム同期する。
        if (uploader != null)
            uploader.ManualGripEnabled = !(experimentInputEnabled && _state == TrialState.Running);

        if (_state != TrialState.Running) return;

        // 収束時間の起点: 最初のキーフレームが実際に送信された時刻
        int sent = Sent;
        if (sent != _lastSeenSent)
        {
            if (_firstKfT < 0f && sent > 0) _firstKfT = Time.realtimeSinceStartup;
            _lastSeenSent = sent;
        }

        // 所定時間に達したら収集完了。ロックできたかどうかは終了条件ではない
        // （曲線は保存済みキーフレームから後で引くので、確定の有無に関係なくデータは使える）。
        if (Time.realtimeSinceStartup - _trialStartT >= collectSeconds) FinishCollection();
    }

    /// <summary>実験用のコントローラ割当。両手が塞がる場面があるので、主要操作は
    /// HUD の Poke だけでなく物理ボタンからも打てるようにする。
    /// experimentInputEnabled の間は AlignmentManager 側の同ボタンが封じられているので二重発火しない。</summary>
    private void HandleExperimentInput()
    {
        if (!experimentInputEnabled) return;

        // 測量中・編集中は右トリガーが記録／配置に使われている。試行操作と混ぜない。
        bool surveying = cpMeasure != null && cpMeasure.IsRunning;
        bool editing   = EditingCPs;

        HandleBButton(surveying, editing);
        HandleAButton(surveying, editing);

        // Y(左) は文脈で意味が変わる。試行中は「中断」、それ以外は「先端校正」。
        // 校正は試行の実行中にも測量中にも行わないので、時間的に競合しない。
        if (OVRInput.GetDown(OVRInput.Button.Four))
        {
            if (_state == TrialState.Running)  AbortTrial();
            else if (surveying)                cpMeasure.CancelSurvey();
            else if (editing)                  cpEdit.Finish();
            else if (tipCalib != null)         tipCalib.ToggleCalibration();
        }
    }

    /// <summary>B(右)。CP 編集中だけ意味が変わる。
    ///   通常   … 試行の開始／次へ（押した瞬間に発火。長押し判定で遅らせない）
    ///   編集中 … 短押し=この点をビルド値へ戻す / 長押し=動かした点をすべて戻す
    ///
    /// 編集中の B は元々「何も起きない」ボタンだった。そこへ Revert を載せたのは、
    /// **HUD のボタンを Poke でしか押せない状態を無くす**ため（パネルを手で突く操作は
    /// 空中の 40px の的を狙うことになり、実機で「何を突くのか分からない・当たらない」と
    /// なった）。試行操作と衝突しないのは、編集中は StartTrial を通さないから。</summary>
    private void HandleBButton(bool surveying, bool editing)
    {
        if (!editing)
        {
            _bDownT = -1f; _bLongFired = false;
            if (OVRInput.GetDown(OVRInput.Button.Two) && _state != TrialState.Running && !surveying)
                StartTrial();                                    // B(右): 開始／次へ
            return;
        }
        if (cpEdit == null) return;

        if (OVRInput.GetDown(OVRInput.Button.Two)) { _bDownT = Time.realtimeSinceStartup; _bLongFired = false; }

        // 長押しは離す前に発火（A と同じ理由: 押し続ける長さが分からないと不安になる）
        if (!_bLongFired && _bDownT >= 0f && OVRInput.Get(OVRInput.Button.Two)
            && Time.realtimeSinceStartup - _bDownT >= aLongPressS)
        {
            _bLongFired = true;
            cpEdit.RevertAll();
        }
        if (OVRInput.GetUp(OVRInput.Button.Two))
        {
            bool wasLong = _bLongFired;
            _bDownT = -1f; _bLongFired = false;
            if (!wasLong) cpEdit.RevertCurrent();
        }
    }

    /// <summary>A(右) の短押し／長押し。
    ///   短押し … **「この点を確定して次へ」**（測量中・編集中）／どちらでもなければ測量の開始
    ///   長押し … model 側 CP の VR 編集の開始／終了
    /// 2 つの準備ステップに 1 つのボタンを割り当てるのは、実験中に使うボタンを増やしたくないため
    /// （[[KeyframeUploader]] の左グリップと同じ流儀）。AlignmentManager 側の A（Lock）は
    /// 実験中は封じられているので二重発火しない。
    ///
    /// **短押しの意味を「確定して次へ」に統一した理由。** 右トリガーは仮置きだけを行い、
    /// 進行はここで起こす（→ [[ControlPointMeasure.ConfirmPoint]] / [[ControlPointEdit.AcceptCurrent]]）。
    /// 編集中は仮置きが無ければ Skip（置かずに次へ）になるので、直したいのが CP_04 だけのときに
    /// CP_01..03 を置き直させずに送れる、という従来の役割もそのまま残る。
    ///
    /// **測量中の A は測量の中断に使わない。** 以前は「測量中の A = ToggleSurvey = 中断」だったが、
    /// 確定を載せた以上、A を余分に 1 回押しただけで 6 点の測量が消えるのは危険すぎる。
    /// 中断は Y に一本化する（元々 Y でも中断でき、HUD もそう出す）。</summary>
    private void HandleAButton(bool surveying, bool editing)
    {
        if (_state == TrialState.Running) { _aDownT = -1f; return; }

        if (OVRInput.GetDown(OVRInput.Button.One)) { _aDownT = Time.realtimeSinceStartup; _aLongFired = false; }

        // 長押しは「離す前」に発火させる。押している間ずっと反応が無いと、
        // どれだけ押せばよいのか分からない（触覚が出た時点で指を離せる）。
        if (!_aLongFired && _aDownT >= 0f && OVRInput.Get(OVRInput.Button.One)
            && Time.realtimeSinceStartup - _aDownT >= aLongPressS)
        {
            _aLongFired = true;
            if (cpEdit != null && !surveying) cpEdit.ToggleEdit();
        }

        if (OVRInput.GetUp(OVRInput.Button.One))
        {
            bool wasLong = _aLongFired;
            _aDownT = -1f; _aLongFired = false;
            if (wasLong) return;
            if (editing)                 cpEdit.AcceptCurrent();   // 仮置きを確定／無ければ次へ
            else if (surveying)          cpMeasure.ConfirmPoint(); // 仮置きを確定して次の点へ
            else if (cpMeasure != null)  cpMeasure.ToggleSurvey(); // 測量の開始
        }
    }

    // ---------------- 試行の開始・終了 ----------------

    /// <summary>新しい試行を開始する（HUD ボタン / 外部から呼ぶ）。</summary>
    public void StartTrial()
    {
        if (_busy) { Debug.LogWarning("[ExperimentController] リセット処理中です"); return; }

        // 計画分を消化したら止める。余分に回すとログに使わない試行が混ざり、
        // どれを解析対象にするかが後から分からなくなる。
        if (AllTrialsDone)
        {
            Debug.LogWarning($"[ExperimentController] 計画した {plannedTrials} 試行は完了済みです。" +
                             "続けるなら Inspector の plannedTrials を増やしてください。");
            return;
        }
        // 成立し得ない試行を始めると、試行番号だけ消費してログが濁る。理由を明示して止める。
        // CP の測量は試行ごとではなく実験前に 1 回なので、ここでは SurveyReady（下）だけを見る。
        if (!ServerReady)
        {
            Debug.LogError("[ExperimentController] PC サーバが応答しません（PING に PONG が返らない）。" +
                           "run.py の起動と、ネットワーク到達性（ビーコンまたは UDPManager の Manual IP）を確認してください。");
            return;
        }
        if (!CvReady)
        {
            Debug.LogError("[ExperimentController] WSL2 の CV ワーカが検出できません。" +
                           "このまま始めても Sim(3) が返らず必ずタイムアウトします。" +
                           "PC の run.py のウインドウで [cv] の行を確認してください" +
                           "（run.py が localize を自動で起動します。--no-cv で起動した場合は WSL2 で手動起動）。");
            return;
        }
        if (!PointsReady)
        {
            Debug.LogError("[ExperimentController] Virtual CP（モデル上の CP）がありません。" +
                           "Editor の Tools > VPTAligned3DGS > Control Points で配置してください。");
            return;
        }
        if (!SurveyReady)
        {
            Debug.LogError("[ExperimentController] Real CP（実物側の位置）が未設定です。"
                           + (cpMeasure != null && !string.IsNullOrEmpty(cpMeasure.InvalidReason)
                              ? cpMeasure.InvalidReason + " "
                              : "")
                           + "実験の前に 1 回、各 CP の実物を先端で触って記録してください（Set Real CP）。");
            return;
        }
        if (!TipReady)
        {
            Debug.LogError("[ExperimentController] コントローラ先端が未校正です。" +
                           "追跡原点と物理的先端の差がそのまま誤差に乗るため、先に先端校正を行ってください。");
            return;
        }
        if (!TrackingSteady)
        {
            // 原点が動いた直後は座標系がまだ落ち着いていない。数秒待ってから始める。
            Debug.LogWarning("[ExperimentController] トラッキング原点が動いた直後です。" +
                             "数秒待ってから開始してください。");
            return;
        }
        StartCoroutine(StartTrialRoutine());
    }

    /// <summary>実行中の試行を中断する（記録は aborted として残す）。</summary>
    public void AbortTrial()
    {
        if (_state != TrialState.Running) return;
        _state = TrialState.Idle;
        if (uploader != null) uploader.SetAuto(false);
        logger?.EndTrial("aborted", TrialMetricsJson());   // 実験者都合の中断。解析からは除外される
        Debug.Log("[ExperimentController] 試行を中断しました");
    }

    private IEnumerator StartTrialRoutine()
    {
        _busy = true;

        // 実行中の試行があれば先に閉じる（記録を落とさない）
        if (_state == TrialState.Running) AbortTrial();

        // 1) 送信を止める。前試行の在庫パケットが新セッションへ混ざるのを防ぐ。
        if (uploader != null) uploader.SetAuto(false);
        yield return new WaitForSeconds(0.2f);

        // 2) PC のセッションを切り替える（蓄積 Cc/Cw の破棄はここで起きる）
        // ラベルは通し番号。破棄した試行にも固有の番号を与えないと、PC の session_*/trial.json と
        // 1 対 1 に対応せず、後から破棄分だけを除外できなくなる。
        _attempt++;
        string label = $"trial-{_attempt:D2}";
        if (udp != null)
            udp.SendReset("{\"trial\":" + _attempt + ",\"label\":\"" + label + "\"}");
        else
            Debug.LogWarning("[ExperimentController] UDPManager 無し → PC 側のリセットは手動で行ってください");

        // 3) Quest 側の状態を初期化
        if (align != null)
        {
            align.ResetForNewTrial();
            // 収集中に見るべきは実環境だけ。スプラットは初期姿勢へ戻された直後で
            // 実世界と無関係な場所に浮いており、視界を塞ぐうえ「もう合っている」と誤解させる。
            align.SetSplatVisible(false);
        }
        _shownForMeasure = false;
        if (anchors != null) { anchors.ExperimentMode = true; anchors.ClearSaved(); }
        if (uploader != null) uploader.ResetForNewTrial(false);   // 自動 ON は settle 後
        // CP の測量は試行をまたいで使い回すので、ここでは触らない。
        // model 側（Editor で配置）も実物側（部屋）も試行では変わらないため。

        // PC が新しい session_* を作り終えるのを待つ
        yield return new WaitForSeconds(resetSettleS);

        // 4) 計測開始
        _state = TrialState.Running;
        _trialStartT = Time.realtimeSinceStartup;
        _firstKfT = -1f; _lockT = -1f; _kfAtLock = 0; _lastSeenSent = 0;
        VoidNotice = "";                       // 前回の破棄警告を消す
        if (tracking != null) tracking.Mark();  // この試行での原点変化を数え直す
        // どの測量（実物側の CP 位置）を使った試行かを残す。原点が動いて測り直すと
        // 番号が変わるので、これが無いと解析側で試行と測量を対応づけられない。
        logger?.BeginTrial("collect", label,
                           "\"survey\":" + (cpMeasure != null ? cpMeasure.SurveyId : 0));
        if (uploader != null && autoCaptureOnStart) uploader.SetAuto(true);

        Debug.Log($"[ExperimentController] === 試行 {TrialIndex} ({label}) 開始。部屋を見回してください ===");
        _busy = false;
    }

    /// <summary>自動位置合わせが確定した時刻を記録する（参考情報）。
    /// **試行はここでは終わらせない。** 収集は所定時間まで続けないと曲線の後半が埋まらないし、
    /// 確定できたかどうかは評価指標ではない（曲線は保存済みキーフレームから後で引く）。</summary>
    private void OnAutoAligned(Sim3Result r)
    {
        if (_state != TrialState.Running || _lockT >= 0f) return;
        _lockT = Time.realtimeSinceStartup;
        _kfAtLock = Sent;
        Debug.Log($"[ExperimentController] 自動位置合わせ確定: {TimeToLock:F1}s / KF {_kfAtLock}"
                  + "（収集はこのまま継続）");
    }

    /// <summary>所定時間に達したので収集を終える。成否の概念は無い。</summary>
    private void FinishCollection()
    {
        _state = TrialState.Collected;
        _validTrials++;              // 成立した試行として数える（破棄されたらここで戻す）
        if (uploader != null) uploader.SetAuto(false);
        logger?.EndTrial("completed", TrialMetricsJson());
        string lockInfo = _lockT >= 0f
            ? $"（確定 {TimeToLock:F1}s / KF {_kfAtLock}）"
            : "（この試行では確定に至らず。収集データは解析に使えます）";
        Debug.Log($"[ExperimentController] 試行 {_validTrials} 収集完了: "
                  + $"{Sent} 枚 / {collectSeconds:F0}s {lockInfo}");
    }

    private string TrialMetricsJson()
    {
        return "\"time_to_lock_s\":" + TimeToLock.ToString("F3", Inv) +
               ",\"kf_at_lock\":" + _kfAtLock +
               ",\"kf_total\":" + Sent +
               ",\"elapsed_s\":" + (Time.realtimeSinceStartup - _trialStartT).ToString("F3", Inv) +
               ",\"collect_s\":" + collectSeconds.ToString("F1", Inv) +
               ",\"tracking_events\":" + TrackingEventsThisTrial;
    }
}
