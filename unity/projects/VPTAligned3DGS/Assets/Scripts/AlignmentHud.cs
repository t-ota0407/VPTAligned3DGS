using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// ヘッドセット内 HUD。実験者が試行を進めるための画面。**表示文字はすべて英語**。
///
/// 設計方針:
/// - **局面で表示を切り替える**。知りたいのは常に「今なにをすべきか」なので、
///   指示文を最上段に大きく置き、数値は下へ落とす。
/// - **文字が重ならないこと**。各行に十分な高さを確保し、1 行想定の欄は Truncate にして
///   はみ出させない（以前は Overflow で長文が隣の行に被っていた）。
/// - **頭に緩く追従**（lazy follow）。部屋を歩き回るため世界固定だと見えなくなる。
///   視線から一定角度外れたときだけ滑らかに追いつき、正面を避けて少し下に置く。
///   Poke 中は動かない（触ろうとしたボタンが逃げるのを防ぐ）。
/// - **すべてのボタンに物理割当がある**（割当は ExperimentController）。Poke は補助で、
///   Poke でしか押せないボタンは置かない——空中に浮いた 40px の板を 3cm 以内まで突く操作は
///   実機で狙えず、実験者に「その機能が無い」と受け取られた。ラベルには割当を角括弧で添える。
/// - **押せない操作は淡色にする**。とくに Real CP が揃うまでは「試行開始」を塞ぐ
///   （Real CP が無いまま集めたデータは、あとから誤差を計算できない）。割当が文脈で変わる
///   ボタン（Y は 4 役、A/B は短長押しで 2 役）は、**その割当が効かない局面ごと無効にする**。
///   ラベル側で割当タグだけ消していた頃は、編集中に `Set Real CP` を Poke できてしまい、
///   測定と編集が同時に走って右トリガーが両方に効いた。
/// - UI は実行時に自前生成（World Space Canvas＋UI.Text/Image）。シーンファイルを汚さない。
///
/// **CP の呼び分け（HUD の文言とワールドのラベルのみ。クラス名・ログ名・JSON の鍵は従来のまま）**:
///   Virtual CP … 3DGS モデル上の座標（[[ControlPointEdit]] / Editor。ログ名 cp_model_edit）
///   Real CP    … 同じ特徴の実物の位置（[[ControlPointMeasure]] で実測。ログ名 cp_survey）
/// **個々の点を指すときは表示名（`VCP_01` / `RCP_01`）で書く**（内部名 `CP_01` をそのまま出さない）。
/// ワールドの球・点に出るラベルと同じ文字にしておかないと、HUD が指した点を視界の中で
/// 探せない（→ [[CpGizmos.VirtualLabel]] / [[CpGizmos.RealLabel]]、`CurrentLabel`）。
///
/// 表示トグルは右グリップ（`RawAxis1D.RHandTrigger`。左グリップは KeyframeUploader が使用）。
/// 実験コンポーネントが無いシーンでは、位置合わせ用の簡易 HUD として動く。
///
/// **デモ展示モード**（[[DemoController]] があるシーン）では 2 枚に分かれる（→ ImplementationSpec.md §14）:
///   - 来場者向けの案内（小さな 1 枚。`Look around the room` など）。頭に緩く付いてくる。
///     スタッフが要る場面では 3 行目にスタッフパネルの開き方（Y＋B 長押し）を出す。
///   - スタッフパネル（この主パネル）。Y＋B 長押しで開閉し、右グリップのトグルは使わない
///     （来場者はグリップを握ったまま操作するので、偶然開いてしまう）。
/// </summary>
public class AlignmentHud : MonoBehaviour
{
    [Header("References (auto-find if empty)")]
    [SerializeField] private UDPManager udp;
    [SerializeField] private KeyframeUploader uploader;
    [SerializeField] private AlignmentManager align;

    [Header("Experiment (optional; 無ければ実験表示は出ない)")]
    [SerializeField] private ExperimentController exp;
    [SerializeField] private ControllerTipCalibration tipCalib;
    [SerializeField] private ControlPointMeasure cpMeasure;
    [SerializeField] private ControlPointEdit cpEdit;

    [Header("Demo (optional; あればデモ展示用の表示になる)")]
    [SerializeField] private DemoController demo;

    [Header("Display")]
    [Tooltip("製品ビルドではこれを false にして丸ごと無効化できる。")]
    [SerializeField] private bool hudEnabled = true;
    [Tooltip("起動時に表示するか（右グリップでいつでもトグル）。")]
    [SerializeField] private bool startVisible = true;
    [Tooltip("日本語フォント（未設定なら組み込みフォント。実機で文字化けする場合はここに NotoSansJP を割り当てる）。")]
    [SerializeField] private Font uiFont;

    [Header("Follow")]
    [Tooltip("頭からこの距離[m]にパネルを置く。")]
    [SerializeField] private float followDistance = 0.75f;
    [Tooltip("視線中心からこの高さ[m]だけ下げる（正面を塞がないため）。")]
    [SerializeField] private float followDownOffset = 0.30f;
    [Tooltip("視線からこの角度[deg]以上外れたら追従を始める（小さいと常に動いて鬱陶しい）。")]
    [SerializeField] private float followDeadzoneDeg = 24f;
    [Tooltip("追従の速さ（大きいほど速く追いつく）。")]
    [SerializeField] private float followSpeed = 3.5f;
    [Tooltip("手がこの距離[m]以内にあるときは追従を止める（Poke 中にボタンが逃げないように）。")]
    [SerializeField] private float followFreezeHandDistM = 0.35f;

    [Tooltip("起動直後この秒数は毎フレーム前方へ置き直す（トラッキング起動前の原点張り付き防止）。")]
    [SerializeField] private float placeSettleS = 1.5f;

    [Header("Poke")]
    [Tooltip("ボタン面からこの距離[m]以内に指先が入ったら押下扱い。")]
    [SerializeField] private float pokeDepthM = 0.03f;

    // ---- 内部 ----
    private Transform _head, _leftTip, _rightTip;
    private Canvas _canvas;
    private bool _visible;
    private bool _rgPressed;
    private bool _following;
    private bool _loggedPlace;

    // 画面要素
    private Text  _headerText, _headerRight, _titleText, _subText,
                  _metricsText, _checkText, _diagText, _progressText;
    private Image _barBg, _progressFill;
    private RectTransform _progressFillRect;
    private float _progressBarWidth;

    private class HudButton
    {
        public RectTransform rect;
        public Image image;
        public Text label;
        public Action action;
        public Func<string> dynamicLabel;   // 局面でラベルが変わるボタン
        public Func<bool> isEnabled;        // false のときは淡色＋押しても無反応
        public Color baseColor;
        public bool leftInside, rightInside;
        public float flashUntil;
    }
    private readonly List<HudButton> _buttons = new List<HudButton>();

    // --- 配色 ---
    // 大きな色面はやめ、局面はアクセントバー（細い縦線）1 本だけで示す。
    // 面で塗ると視界の情報量が増えて、パススルー越しの実験の邪魔になる。
    private static readonly Color ColBg      = new Color(0.055f, 0.06f, 0.075f, 0.90f);
    private static readonly Color ColRule    = new Color(1f, 1f, 1f, 0.10f);   // 罫線
    private static readonly Color ColText    = new Color(0.96f, 0.97f, 0.98f);
    private static readonly Color ColMuted   = new Color(0.62f, 0.66f, 0.72f);
    private static readonly Color ColFaint   = new Color(0.45f, 0.49f, 0.55f);
    // 物理ボタンの割当。**淡くしない。** ボタンを探すために読む文字なので、
    // パススルーの明るい背景に重なると ColFaint では沈んで読めない（実機で判明）。
    private static readonly Color ColKey     = new Color(0.99f, 0.85f, 0.45f);  // 淡い山吹

    private static readonly Color ColSetup   = new Color(0.98f, 0.72f, 0.22f);  // 琥珀: 準備待ち
    private static readonly Color ColReady   = new Color(0.38f, 0.66f, 1.00f);  // 青:   開始できる
    private static readonly Color ColRunning = new Color(0.30f, 0.82f, 0.85f);  // 青緑: 収集中
    private static readonly Color ColMeasure = new Color(0.72f, 0.55f, 1.00f);  // 紫:   測量中
    private static readonly Color ColEdit    = new Color(0.98f, 0.45f, 0.72f);  // 桃:   model 側 CP の編集中
    private static readonly Color ColOk      = new Color(0.42f, 0.86f, 0.55f);  // 緑:   完了
    private static readonly Color ColGood    = new Color(0.42f, 0.86f, 0.55f);
    private static readonly Color ColBad     = new Color(1.00f, 0.48f, 0.42f);
    private static readonly Color ColWarn    = new Color(0.98f, 0.72f, 0.22f);

    // アクセントバー（帯の左の縦線）
    private Image _accent;

    private bool HasExp => exp != null || tipCalib != null || cpMeasure != null;

    private void Awake()
    {
        if (udp == null)       udp = FindFirstObjectByType<UDPManager>();
        if (uploader == null)  uploader = FindFirstObjectByType<KeyframeUploader>();
        if (align == null)     align = FindFirstObjectByType<AlignmentManager>();
        if (exp == null)       exp = FindFirstObjectByType<ExperimentController>();
        if (tipCalib == null)  tipCalib = FindFirstObjectByType<ControllerTipCalibration>();
        if (cpMeasure == null) cpMeasure = FindFirstObjectByType<ControlPointMeasure>();
        if (cpEdit == null)    cpEdit = FindFirstObjectByType<ControlPointEdit>();
        if (demo == null)      demo = FindFirstObjectByType<DemoController>();

        var rig = FindFirstObjectByType<OVRCameraRig>();
        if (rig != null) { _head = rig.centerEyeAnchor; _leftTip = rig.leftHandAnchor; _rightTip = rig.rightHandAnchor; }
        if (_head == null && Camera.main != null) _head = Camera.main.transform;
    }

    private void Start()
    {
        if (!hudEnabled) { enabled = false; Debug.Log("[AlignmentHud] hudEnabled=false → HUD 無効"); return; }
        BuildUi();
        SetVisible(demo != null ? demo.StaffMode : startVisible);
        Debug.Log($"[AlignmentHud] built. exp={HasExp} demo={(demo != null)} font={(uiFont != null ? uiFont.name : "builtin")} head={(_head != null)}");
    }

    private void Update()
    {
        if (!hudEnabled) return;

        if (demo != null)
        {
            // デモ: 主パネル（スタッフ用）はスタッフパネルを開いている間だけ出す。
            // 右グリップのトグルは使わない（来場者は握ったまま操作するので、偶然開いてしまう）。
            if (_visible != demo.StaffMode) { SetVisible(demo.StaffMode); if (_visible) PlaceInFront(); }
            UpdateVisitorHint();
        }
        else
        {
            // 右グリップ押下エッジで表示トグル（利き手で入れ替わる Primary/Secondary は使わない）
            float rg = OVRInput.Get(OVRInput.RawAxis1D.RHandTrigger);
            if (!_rgPressed && rg >= 0.8f) { _rgPressed = true; ToggleVisible(); }
            else if (_rgPressed && rg < 0.5f) { _rgPressed = false; }
        }

        if (!_visible) return;
        if (Time.timeSinceLevelLoad < placeSettleS) { PlaceInFront(); LogPlaceOnce(); }
        else UpdateFollow();

        // 表示内容の更新は毎フレームやると文字列生成の GC で 72fps を削る。
        // 人が読む情報なので 10Hz で十分（追従と Poke は毎フレーム）。
        if (Time.unscaledTime >= _nextRefresh)
        {
            _nextRefresh = Time.unscaledTime + RefreshIntervalS;
            RefreshContent();
        }
        UpdatePoke();
    }

    private const float RefreshIntervalS = 0.1f;
    private float _nextRefresh;

    /// <summary>校正中の暫定残差がこれ[mm]を超えたら「先端がずれている」と警告する。</summary>
    private const float SlipWarnMm = 8f;

    private void LogPlaceOnce()
    {
        if (_loggedPlace || _canvas == null) return;
        _loggedPlace = true;
        Debug.Log("[AlignmentHud] placed canvas at " + _canvas.transform.position);
    }

    // ---------------- 表示・追従 ----------------

    public void ToggleVisible()
    {
        SetVisible(!_visible);
        if (_visible) PlaceInFront();
    }

    private void SetVisible(bool v)
    {
        _visible = v;
        if (_canvas != null) _canvas.gameObject.SetActive(v);
    }

    private Vector3 FollowTarget(out Vector3 fwd)
    {
        fwd = Vector3.ProjectOnPlane(_head.forward, Vector3.up).normalized;
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
        return _head.position + fwd * followDistance - Vector3.up * followDownOffset;
    }

    private void PlaceInFront()
    {
        if (_head == null || _canvas == null) return;
        _canvas.transform.position = FollowTarget(out Vector3 fwd);
        _canvas.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);
        _following = false;
    }

    /// <summary>視線から一定角度外れたときだけ滑らかに追いつく（lazy follow）。
    /// 常時追従だと画面に張り付いて酔うので、デッドゾーンを設けて「置いてある」感を保つ。</summary>
    private void UpdateFollow()
    {
        if (_head == null || _canvas == null) return;

        Vector3 target = FollowTarget(out Vector3 fwd);
        Vector3 cur = _canvas.transform.position;

        // Poke しようとしている間は動かさない（触るボタンが逃げると操作できない）
        bool handNear =
            (_leftTip  != null && Vector3.Distance(_leftTip.position,  cur) < followFreezeHandDistM) ||
            (_rightTip != null && Vector3.Distance(_rightTip.position, cur) < followFreezeHandDistM);

        if (!handNear)
        {
            Vector3 toCur = cur - _head.position; toCur.y = 0f;
            float ang = toCur.sqrMagnitude > 1e-6f ? Vector3.Angle(fwd, toCur.normalized) : 999f;
            if (ang > followDeadzoneDeg) _following = true;

            if (_following)
            {
                float k = 1f - Mathf.Exp(-followSpeed * Time.deltaTime);   // フレームレート非依存の減衰
                _canvas.transform.position = Vector3.Lerp(cur, target, k);
                if (Vector3.Distance(_canvas.transform.position, target) < 0.02f) _following = false;
            }
        }

        // 向きは常に頭の方へ（滑らかに）
        Vector3 look = _canvas.transform.position - _head.position; look.y = 0f;
        if (look.sqrMagnitude > 1e-6f)
            _canvas.transform.rotation = Quaternion.Slerp(
                _canvas.transform.rotation, Quaternion.LookRotation(look.normalized, Vector3.up),
                1f - Mathf.Exp(-8f * Time.deltaTime));
    }

    // ---------------- 内容の更新 ----------------

    private void RefreshContent()
    {
        // 校正中は他のすべてに優先（この間ずっと手を固定しているので、他の情報は邪魔）
        if (tipCalib != null && tipCalib.IsRunning) { RefreshCalibrating(); return; }

        if (demo != null) { RefreshDemo(); return; }
        if (exp == null) { RefreshWithoutExperiment(); return; }

        SetHeader("TRIAL", $"{exp.TrialIndex} / {exp.PlannedTrials}");

        // 破棄はまず伝える。原因（部屋の座標系が動いた）を知らないと、実験者は
        // 「位置合わせが失敗した」と受け取って同じ条件でやり直してしまう。
        if (!string.IsNullOrEmpty(exp.VoidNotice) && exp.VoidNoticeAgeS < 25f
            && exp.State == ExperimentController.TrialState.Idle)
        {
            SetBanner(ColBad, "Trial discarded",
                      "Tracking moved — keep the headset on, stay in this space, press "
                      + Btn("B") + " to redo");
            SetProgress(-1f, "");
            SetMetrics(Amber("The room coordinates shifted (" + exp.VoidNotice
                             + "), so this trial cannot be used."));
            SetCheck();
            SetDiag();
            return;
        }

        switch (exp.Phase)
        {
            case ExperimentController.UiPhase.NeedsSetup:
                SetBanner(ColSetup, "Setup required", FirstUnmetReason());
                SetProgress(-1f, "");
                // 実験の準備（CP・先端校正）をせずにデモを見せたいだけの人向けに、入口をここで案内する
                SetMetrics(exp.DemoSwitchProgress > 0f
                           ? $"Switching to the demo… {exp.DemoSwitchProgress * exp.DemoSwitchHoldS:F1} / {exp.DemoSwitchHoldS:F0} s"
                           : "Complete the checks below to start\nDemo without CPs: hold " + Btn("both sticks")
                             + $" for {exp.DemoSwitchHoldS:F0} s");
                break;

            case ExperimentController.UiPhase.Ready:
                SetBanner(ColReady, "Ready to start",
                          "Press " + Btn("B") + ", then walk a curved path while looking around");
                SetProgress(-1f, "");
                // 未配置の CP を抱えたまま測ると誤差が数メートルになり、位置合わせが悪いのか
                // CP が置かれていないのか区別がつかなくなる。始める前に気づけるようにする。
                SetMetrics(cpMeasure != null && !string.IsNullOrEmpty(cpMeasure.SetupWarning)
                           ? Amber(cpMeasure.SetupWarning) : LastTrialSummary());
                break;

            case ExperimentController.UiPhase.Running:
                SetBanner(ColRunning, "Walk around the room",
                          "Cover the space. Do not stand still or walk in a straight line.");
                SetProgress(exp.CollectProgress, $"{exp.Elapsed:F0} / {exp.CollectSeconds:F0} s");
                SetMetrics(RunningMetrics);
                break;

            case ExperimentController.UiPhase.Surveying:
                RefreshSurveying();
                break;

            case ExperimentController.UiPhase.EditingCPs:
                RefreshEditingCPs();
                break;

            case ExperimentController.UiPhase.Review:
                SetBanner(ColOk, "Collection complete",
                          exp.AllTrialsDone ? "All trials done. Run experiment_report.py on the PC."
                                            : "Check the overlay, then press " + Btn("B")
                                              + " for the next trial");
                SetProgress(1f, "");
                SetMetrics(UploadSummary());
                break;

            default:  // AllDone
                // **データが PC に揃ったかをここで断言する。** 実験者がヘッドセットを外して
                // よいかどうかの判断がこれで決まる。未送達が残っているのに終わったと思われると、
                // 測り直しの効かない CP 実測値が端末の中に取り残される。
                bool delivered = udp != null && udp.PendingEventCount == 0;
                SetBanner(delivered ? ColOk : ColWarn, "All trials complete",
                          delivered ? "Data is on the PC — run experiment_report.py"
                                    : "Waiting for the last records to reach the PC…");
                SetProgress(1f, "");
                SetMetrics(UploadSummary());
                break;
        }

        SetCheck();
        SetDiag();
    }

    private void RefreshCalibrating()
    {
        float spread = tipCalib.CurrentSpreadDeg, need = tipCalib.RequiredSpreadDeg;
        bool ok = spread >= need && tipCalib.SampleCount >= tipCalib.RequiredSamples;

        SetHeader("TIP CALIBRATION", "");
        // 割当は HUD 側で添える（ColKey で目立たせるため。→ ControllerTipCalibration.GuidanceSub）。
        // 校正中の Y は「終了」に割り当たっている（ExperimentController の Y の分岐）。
        SetBanner(ok ? ColOk : ColMeasure, tipCalib.GuidanceTitle,
                  tipCalib.GuidanceSub + "  ·  " + Btn("Y") + ": finish");
        SetProgress(tipCalib.Progress,
                    $"{tipCalib.SampleCount}/{tipCalib.RequiredSamples} samples · "
                    + $"{spread:F0}°/{need:F0}° swing");

        // 暫定残差。先端が点から滑ると跳ね上がるので、その場で当て直せる。
        // 8mm は接点が約 5mm ずれた状態に相当し、そこからオフセット誤差が 2mm を超え始める。
        float lr = tipCalib.LiveResidualMm;
        SetMetrics(lr < 0f ? "Measuring residual…"
                   : lr > SlipWarnMm ? Amber($"Residual {lr:F1} mm — tip is slipping, reseat it")
                                     : $"Residual {lr:F1} mm");
        SetCheck();
        SetDiag();
    }

    /// <summary>Real CP の測定中の表示。実験の前に 1 回だけ通る局面。</summary>
    private void RefreshSurveying()
    {
        SetHeader("SET REAL CONTROL POINTS", $"{cpMeasure.DoneCount} / {cpMeasure.MarkerCount}");

        // **受け付けられない状態で「触ってください」と出さない。**
        // 指示どおり押しても無反応だと、実験者は故障を疑うしかなくなる。
        string block = cpMeasure.BlockReason;
        if (!string.IsNullOrEmpty(block))
        {
            SetBanner(ColSetup, "Cannot set Real CPs yet", block);
            SetProgress(-1f, ""); SetMetrics(""); SetCheck(); SetDiag();
            return;
        }

        // スティックは残す。位置合わせ前の球は壁の向こうや 20m 先に出ることがあり、
        // シーンごと引き寄せられないと「次はどの点か」の目印すら見えない。
        // ただし用途は**見えるところへ持ってくる**ことだけで、実物に重ねる作業ではない
        // （重ねると球に釣られて先端が実物からずれる）。X の案内はメトリクス行に置く。
        // 指示に出す名前は**ワールドのラベルと同じ文字**（`RCP_01`）にする。HUD が `CP_01`、
        // 視界の球が `VCP_01`、記録した点が `RCP_01` では、どれを探せばよいのか対応が取れない。
        // R が「実物側」を表すので "the real" は重ねない（意味は下の 2 行で押さえる）。
        //
        // **記録は 2 段階**なので、指示も 2 段階で出す。「置いた」と「記録した」を同じ文言に
        // すると、確定し忘れた点をそのまま置き去りにしてしまう（測量は 1 回きりで測り直しが
        // 効かない）。仮置き中はタイトルから変えて、確定待ちであることを最初に読ませる。
        if (cpMeasure.HasPending)
            SetBanner(ColSetup, $"Confirm {cpMeasure.CurrentLabel}?",
                      "Look at the white dot from another angle. It is <b>not recorded yet</b>."
                      + "  ·  " + Btn("A") + ": confirm & next"
                      + "  ·  " + Btn("right trigger") + ": place again"
                      + "  ·  " + Btn("left trigger") + ": discard");
        else
            SetBanner(ColMeasure, $"Walk to {cpMeasure.CurrentLabel}",
                      "Touch the real feature with the tip, then pull the " + Btn("right trigger")
                      + " to place it  ·  " + Btn("left trigger") + ": redo previous"
                      + "  ·  " + Btn("sticks") + ": bring the markers into view");
        SetProgress(cpMeasure.Progress, $"{cpMeasure.DoneCount} / {cpMeasure.MarkerCount} points");

        if (cpMeasure.RecentlyRejected)
        {
            // 理由が分かるものは理由を出す。「押したのに何も起きない」が最も困る。
            string why = cpMeasure.RejectReason;
            SetMetrics(Amber(string.IsNullOrEmpty(why) ? "Not recorded — see the reason above" : why));
        }
        else if (cpMeasure.HasPending)
        {
            // 確定するまでは何も記録されていない、を数字の側からも押さえる。
            SetMetrics(Amber("White RCP dot = placed but NOT recorded  ·  " + Btn("A") + " records it"
                             + (cpMeasure.PendingRetries > 0
                                ? $"  ·  placed {cpMeasure.PendingRetries + 1}×" : "")));
        }
        else if (!cpMeasure.CanGuideVisually)
        {
            // 球は出ているが、位置合わせ前なので実物の上には載っていない。
            // **「球のところへ行けばいい」と読ませない。** 球へ寄せると先端が実物からずれ、
            // それがそのまま Real CP の誤差になる（グラウンドトゥルースが汚れる）。
            SetMetrics(Amber("The yellow VCP sphere only says which point — it is NOT on the real object.\n"
                             + "Touch the real thing  ·  " + Btn("X") + ": peek at the splats"));
        }
        else
        {
            // V/R の意味はここで一度だけ教える（ワールドのラベルが VCP_nn / RCP_nn）。
            SetMetrics("Yellow VCP sphere = which point now  ·  orange RCP dot = recorded"
                       + "  ·  " + Btn("X") + ": peek at the splats");
        }
        SetCheck();
        SetDiag();
    }

    /// <summary>Virtual CP の VR 編集中の表示。位置合わせ前の準備でしか通らない局面。</summary>
    private void RefreshEditingCPs()
    {
        SetHeader("EDIT VIRTUAL CONTROL POINTS", $"{cpEdit.EditedCount} / {cpEdit.MarkerCount} moved");

        // 名前はワールドの球のラベルと同じ文字（`VCP_01`）で出す（→ RefreshSurveying の注記）。
        // 置き直しも 2 段階（右トリガー＝仮置き / A＝確定して次へ）。仮置き中はタイトルを変える。
        if (cpEdit.HasPending)
            SetBanner(ColSetup, $"Confirm {cpEdit.CurrentLabel}?",
                      "Move your head and check the white sphere sits on the feature."
                      + "  ·  " + Btn("A") + ": confirm & next"
                      + "  ·  " + Btn("right trigger") + ": place again"
                      + "  ·  " + Btn("left trigger") + ": discard");
        else
            SetBanner(ColEdit, $"Place {cpEdit.CurrentLabel} on the splats",
                      // **3 行に収まる長さで書く**（副見出しの枠 = SubH。溢れた行は縦 Truncate で
                      // 黙って切り落とされる。実際にここは枠が 2 行だった頃に「sticks: move the scene」が
                      // まるごと消えていた）。`Skip`（A）は下のメトリクス行で案内する。
                      "Touch the matching feature <b>on the splats</b> with the tip, then "
                      + Btn("right trigger")
                      + "  ·  " + Btn("left trigger") + ": previous"
                      + "  ·  " + Btn("sticks") + ": move the scene");
        // **列のどこにいるかを必ず出す。** 右トリガーで置くと自動で次の点へ進むが、以前ここが
        // 空（SetProgress(-1f)）で、進んだことが画面から読み取れなかった。球の色（黄→緑）だけが
        // 手掛かりで、モデルが視界の外にあると何も変わらないように見える。実機で「CP_01 の次へ
        // 進めない」と報告されたのはこれが原因で、動作そのものは正しかった。
        SetProgress(cpEdit.Progress,
                    $"{cpEdit.CurrentLabel}  ·  {cpEdit.CurrentIndex + 1} / {cpEdit.MarkerCount}"
                    + $"  ·  {cpEdit.EditedCount} moved");

        if (cpEdit.RecentlyRejected)
        {
            SetMetrics(Amber(cpEdit.RejectReason));
        }
        else if (cpEdit.HasPending)
        {
            SetMetrics(Amber("White VCP sphere = placed but NOT committed  ·  " + Btn("A") + " commits it"
                             + (cpEdit.PendingRetries > 0
                                ? $"  ·  placed {cpEdit.PendingRetries + 1}×" : "")
                             + $"\nGrey dot = original  ·  moved {cpEdit.CurrentMoveM * 100f:F0} cm"));
        }
        else
        {
            // 実物ではなくモデルに触る、という取り違えが最も起きやすい。毎回念を押す。
            string warn = cpEdit.MoveWarning;
            string moved = cpEdit.CurrentMoveM > 0.005f
                           ? $"moved {cpEdit.CurrentMoveM * 100f:F0} cm  ·  " : "";
            // 2 行目は「次に進む手」を常に出す。全点を触る作業ではないので、直す気の無い点を
            // 飛ばせないと詰まる（末尾では飛ばす先が無いので終わり方を出す）。
            // **枠は 2 行ぶん（42px）しかなく、溢れた行は縦 Truncate で黙って消える。**
            // 各行を折り返さない長さ（幅 476 / fontSize 16）に収めること。
            string next = cpEdit.AtLastPoint
                          ? "last point  ·  " + Btn("Y") + ": finish"
                          : Btn("A") + ": skip this point";
            SetMetrics(!string.IsNullOrEmpty(warn) ? Amber(warn)
                       : "Touch the virtual feature on the splats, not the real object"
                         + "\nGrey dot = original  ·  " + moved + next);
        }
        SetCheck();
        SetDiag();
    }

    /// <summary>実験コンポーネントが無いシーン用（位置合わせだけの簡易表示）。</summary>
    private void RefreshWithoutExperiment()
    {
        bool locked = align != null && align.State == AlignmentManager.AlignState.Locked;
        SetHeader("ALIGNMENT", "");
        SetBanner(locked ? ColOk : ColReady,
                  locked ? "Aligned" : "Aligning",
                  uploader != null && uploader.AutoCaptureOn
                      ? "Auto capture on" : Btn("Left grip") + " to send a keyframe");
        SetProgress(-1f, "");
        SetMetrics($"Sent {(uploader != null ? uploader.SentCount : 0)} · "
                   + $"accepted {Accepted} · rejected {Rejected}");
        SetCheck();
        SetDiag();
    }

    // ---------------- デモ展示モード（スタッフパネル） ----------------

    /// <summary>デモのスタッフパネル。**いま何が起きていて、次に何を押せばよいか**を 1 枚で出す。
    /// 来場者向けの案内は別の小さな板（<see cref="UpdateVisitorHint"/>）。</summary>
    private void RefreshDemo()
    {
        var an = demo.Anchors;
        string problem = an != null ? an.ProblemText : "AnchorPersistence is missing";
        string next = (demo.PcReady ? Btn("A") + ": re-align with the PC" : demo.RealignBlockReason)
                      + "  ·  " + Btn("Y") + ": manual";
        SetHeader("DEMO · STAFF", demo.State.ToString());
        SetProgress(-1f, "");

        switch (demo.State)
        {
            case DemoController.DemoState.Locating:
                SetBanner(ColSetup, "Finding saved alignment",
                          (string.IsNullOrEmpty(problem) ? "Look around the room" : problem)
                          + $"  ·  {demo.StateElapsedS:F0} s");
                break;
            case DemoController.DemoState.Relocating:
                SetBanner(ColWarn, "Re-checking position",
                          "Tracking moved or the headset slept. Look around the room."
                          + (demo.LastSaveFailed ? "\nThe last save failed — re-align." : "")
                          + (demo.StateElapsedS > 30f ? "\n" + next : ""));
                break;
            case DemoController.DemoState.NeedsAlignment:
                // 直前の再位置合わせを諦めたなら、その理由を先に出す（次に何を変えればよいかが分かる）
                SetBanner(ColBad, "Alignment needed",
                          (!string.IsNullOrEmpty(demo.LastAlignFailure) ? "Could not align: " + demo.LastAlignFailure
                           : string.IsNullOrEmpty(problem) ? "No usable saved alignment" : problem)
                          + "\n" + next);
                break;
            case DemoController.DemoState.Aligning:
                // 集める → 待つ → 足す（Specification.md §15.6b）。段階ごとに「いま何をすればよいか」を出す
                if (!demo.AlignActive)
                {
                    SetBanner(ColRunning, "Starting re-alignment", demo.AlignGuidance + "  ·  " + Btn("A") + ": cancel");
                }
                else if (demo.CurrentAlignPhase == DemoController.AlignPhase.Collect)
                {
                    SetBanner(ColRunning,
                              demo.TopUpCount == 0 ? "Walk around the room"
                                                   : $"Walk a bit more ({demo.TopUpCount}/{demo.MaxTopUps})",
                              demo.AlignGuidance + "  ·  " + Btn("A") + ": cancel");
                    // 時間は「実際に送信していた時間」（PC 待ちの一時停止は数えない）。一時停止中はそう書く
                    SetProgress(demo.CollectProgress,
                                $"Keyframes {demo.CollectAccepted} / {demo.CollectTarget}"
                                + $"  ·  {demo.CollectActiveS:F0} / {demo.PhaseLimitS:F0} s"
                                + (demo.CapturePaused ? $"  ·  paused, PC queue {demo.PcBacklog}"
                                   : demo.PcBacklog > 0 ? $"  ·  PC queue {demo.PcBacklog}" : ""));
                }
                else
                {
                    SetBanner(ColRunning, "Computing the alignment",
                              demo.AlignGuidance + "  ·  you can stop walking  ·  " + Btn("A") + ": cancel");
                    // 待機に上限時間は無い（PC が遅くても処理し終えるまで待つ）ので、進み具合は
                    // 「PC が受け取った分のうち何枚を処理し終えたか」で出す。経過秒も添える
                    int kf = udp != null ? udp.ServerKeyframeCount : 0;
                    int done = udp != null ? Mathf.Min(udp.CvDoneCount, kf) : 0;
                    string elapsed = $"  ·  {demo.PhaseElapsedS:F0} s";
                    SetProgress(demo.WaitingForCv || kf <= 0 ? -1f : done / (float)kf,
                                demo.WaitingForCv ? "Waiting for the PC to finish earlier work…" + elapsed
                                : udp != null && udp.CvBusy ? $"PC processed {done} / {kf} (processing {udp.CvBusyCount})…" + elapsed
                                : demo.PcBacklog > 0 ? $"PC processed {done} / {kf}…" + elapsed
                                : "Waiting for the result…" + elapsed);
                }
                break;
            case DemoController.DemoState.Saving:
                SetBanner(ColRunning, "Saving alignment", "Creating anchors — keep looking around");
                break;
            case DemoController.DemoState.Showing:
                bool forced = an != null && an.IsForced;
                SetBanner(forced || demo.LastSaveFailed ? ColWarn : ColOk,
                          forced ? "Showing (unverified)" : "Showing",
                          (demo.LastSaveFailed ? "Not saved — will be lost after a restart"
                           : forced ? "Anchors did not cross-check — check the overlay by eye"
                           : SavedSummary(an))
                          + "  ·  " + Btn("Y+B") + " hold: close");
                break;
            case DemoController.DemoState.Manual:
                SetBanner(ColEdit, "Manual adjust",
                          Btn("sticks") + ": move  ·  " + Btn("triggers") + ": scale  ·  "
                          + Btn("A") + ": lock & save  ·  " + Btn("Y") + ": cancel");
                break;
        }

        string notice = demo.NoticeAgeS < 20f ? demo.Notice : "";
        string metrics = demo.State == DemoController.DemoState.Aligning
                         ? AlignProgressMetrics(uploader != null ? uploader.SentCount : 0)
                         : AnchorMetrics(an);
        SetMetrics(string.IsNullOrEmpty(notice) ? metrics : Amber(notice));
        SetDemoCheck(an);
        SetDiag();
    }

    /// <summary>アンカーの見つかり具合。どこで詰まっているか（端末に無い／空間で見つからない／食い違う）を分けて出す。</summary>
    private static string AnchorMetrics(AnchorPersistence an)
    {
        if (an == null || !an.HasSave) return "No saved anchors";
        string s = $"Anchors: found {an.LocalizedCount}/{an.SavedAnchorCount} · tracked {an.TrackedCount}"
                   + $" · agree {an.AgreeingCount}/{an.RequiredAgreeing}";
        if (an.WorstDisagreementM >= 0f && an.TrackedCount >= 2)
            s += $" · spread {an.WorstDisagreementM * 100f:F0} cm";
        return s;
    }

    private static string SavedSummary(AnchorPersistence an)
    {
        if (an == null || !an.HasSave) return "Not saved";
        string src = string.IsNullOrEmpty(an.SavedSource) ? "" : $" · {an.SavedSource}";
        return $"Saved {Age(an.SavedUtc)} ago{src} · {an.SavedAnchorCount} anchors";
    }

    /// <summary>ISO 8601 の保存時刻から経過時間を短く（"12 min" / "3 h" / "2 d"）。</summary>
    private static string Age(string isoUtc)
    {
        if (!DateTime.TryParse(isoUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)) return "?";
        TimeSpan d = DateTime.UtcNow - t.ToUniversalTime();
        if (d.TotalMinutes < 1) return "<1 min";
        if (d.TotalHours < 1) return $"{d.TotalMinutes:F0} min";
        if (d.TotalDays < 1) return $"{d.TotalHours:F0} h";
        return $"{d.TotalDays:F0} d";
    }

    /// <summary>デモの準備チェック。PC はデモでは必須ではない（保存があれば要らない）ので、止まっていても琥珀にする。</summary>
    private void SetDemoCheck(AnchorPersistence an)
    {
        if (_checkText == null) return;
        string anc = an == null || !an.HasSave ? Dot(0, "No anchors")
                   : an.HasConsensus ? Dot(an.IsForced ? 1 : 2, $"Anchors {an.AgreeingCount}/{an.SavedAnchorCount}")
                   : an.TrackedCount > 0 ? Dot(1, $"Anchors {an.TrackedCount}/{an.SavedAnchorCount}")
                   : Dot(0, $"Anchors 0/{an.SavedAnchorCount}");
        string saved = an != null && an.HasSave ? Dot(demo.LastSaveFailed ? 1 : 2, "Saved " + Age(an.SavedUtc))
                                                : Dot(0, "Not saved");
        string srv = udp != null && udp.ServerAlive ? Dot(2, $"PC {udp.RttMs:F0}ms") : Dot(1, "PC off");
        string cv  = udp != null && udp.CvAlive ? Dot(2, "CV") : Dot(1, "CV off");
        _checkText.text = string.Join("   ", anc, saved, srv, cv);
    }

    // ---------------- 来場者向けの案内（デモ） ----------------

    private Canvas _hintCanvas;
    private Text   _hintTitle, _hintSub, _hintStaff;
    private bool   _hintShow;
    private float  _hintNextRefresh;
    private const float HintW = 560f, HintH = 124f;
    [Tooltip("（デモ）来場者向けの案内を置く距離[m]。")]
    [SerializeField] private float hintDistanceM = 1.0f;
    [Tooltip("（デモ）来場者向けの案内を視線からこの高さ[m]だけ下げる。")]
    [SerializeField] private float hintDownOffsetM = 0.12f;

    /// <summary>来場者向けの案内（`Look around the room` など）。見回してもらうのが目的なので、
    /// 主パネルのような「置いてある」追従ではなく、視線に緩く付いてきて常に視界に入るようにする。
    ///
    /// **3 行目にスタッフパネルの開き方（Y＋B 長押し）を出す**（スタッフも忘れるので UI に書く）。
    /// 出すのはスタッフが要る場面だけ（<see cref="DemoController.ShowStaffHint"/>）。
    /// Y＋B を押している間は案内を差し替えて残り時間を出す（3 秒は無反応だと効いているか分からない）。
    /// 文字列の組み立ては 10Hz（主パネルと同じ理由: 毎フレームだと GC で 72fps を削る）、追従は毎フレーム。</summary>
    private void UpdateVisitorHint()
    {
        if (_hintCanvas == null || _head == null) return;
        if (Time.unscaledTime >= _hintNextRefresh)
        {
            _hintNextRefresh = Time.unscaledTime + RefreshIntervalS;
            RefreshVisitorHint();
        }
        if (_hintCanvas.gameObject.activeSelf != _hintShow)
        {
            _hintCanvas.gameObject.SetActive(_hintShow);
            if (_hintShow) PlaceHint(1f);
        }
        if (_hintShow) PlaceHint(1f - Mathf.Exp(-5f * Time.deltaTime));   // フレームレート非依存の減衰
    }

    private void RefreshVisitorHint()
    {
        if (demo.StaffMode) { _hintShow = false; return; }

        string title, sub, staff;
        float p = demo.StaffComboProgress;
        if (p > 0f)
        {
            // Y＋B を押している最中。離すと 0 からやり直しになることも伝える
            title = "Opening the staff panel…";
            sub   = $"Keep holding {Btn("Y")} + {Btn("B")}  ·  {p * demo.StaffComboHoldS:F1} / {demo.StaffComboHoldS:F0} s";
            staff = "";
        }
        else
        {
            title = demo.VisitorHint;
            sub   = demo.VisitorHintSub;
            staff = demo.ShowStaffHint
                    ? $"Staff: hold {Btn("Y")} + {Btn("B")} together for {demo.StaffComboHoldS:F0} s to open the staff panel"
                    : "";
            // 来場者向けの文言が無い局面（デモに入った直後・表示中）でもスタッフ向けの行は出す
            if (string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(staff)) title = "Demo mode";
        }
        _hintShow = !string.IsNullOrEmpty(title);
        if (!_hintShow) return;
        if (_hintTitle.text != title) _hintTitle.text = title;
        if (_hintSub.text != sub)     _hintSub.text = sub;
        if (_hintStaff.text != staff) _hintStaff.text = staff;
    }

    private void PlaceHint(float k)
    {
        Vector3 target = _head.position + _head.forward * hintDistanceM - _head.up * hintDownOffsetM;
        Transform t = _hintCanvas.transform;
        t.position = Vector3.Lerp(t.position, target, k);
        Vector3 look = t.position - _head.position;
        if (look.sqrMagnitude > 1e-6f)
            t.rotation = Quaternion.Slerp(t.rotation, Quaternion.LookRotation(look.normalized, Vector3.up), k);
    }

    private void BuildVisitorHint()
    {
        var go = new GameObject("VisitorHintCanvas");
        go.transform.SetParent(transform, false);
        _hintCanvas = go.AddComponent<Canvas>();
        _hintCanvas.renderMode = RenderMode.WorldSpace;
        var rt = _hintCanvas.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(HintW, HintH);
        go.transform.localScale = Vector3.one * 0.001f;   // 1px = 1mm
        NewUi("HintBg", rt, Vector2.zero, new Vector2(HintW, HintH)).AddComponent<Image>().color = ColBg;
        // 上から 案内（28px）／補足（17px）／スタッフ向け（14px・淡色。割当の文字だけ山吹色）
        _hintTitle = AddText(NewUi("HintTitle", rt, new Vector2(0f, 30f), new Vector2(HintW - 40f, 44f)),
                             28, TextAnchor.MiddleCenter, ColText, false);
        _hintSub = AddText(NewUi("HintSub", rt, new Vector2(0f, -4f), new Vector2(HintW - 40f, 28f)),
                           17, TextAnchor.MiddleCenter, ColMuted, false);
        _hintStaff = AddText(NewUi("HintStaff", rt, new Vector2(0f, -38f), new Vector2(HintW - 40f, 22f)),
                             14, TextAnchor.MiddleCenter, ColFaint, false);
        go.SetActive(false);
    }

    /// <summary>デモのスタッフ用ボタン。**2 列 3 行＋全幅 1 行**（割当タグを添えるので 3 列だと文字が隣へはみ出す）。
    /// 手動調整中は A が AlignmentManager の Lock（保存）に取られるので、ラベルもそれに合わせて変える。</summary>
    private void BuildDemoButtons(RectTransform crt, float y, float inner)
    {
        const float H = 40f, G = 8f;
        float w = (inner - G) / 2f;
        float xl = -inner / 2f + w / 2f, xr = inner / 2f - w / 2f;
        Color c = new Color(1f, 1f, 1f, 0.10f);
        Color alBtn = new Color(1f, 1f, 1f, 0.06f);
        y -= H / 2f;
        AddButtonAt(crt, new Vector2(xl, y), new Vector2(w, H), null,
                    () => { if (demo.InManual) align?.Lock();
                            else if (demo.CanCancelAlign) demo.CancelAlign();
                            else demo.Realign(); }, 15,
                    () => (demo.InManual ? "Lock & save" : demo.CanCancelAlign ? "Cancel re-align" : "Re-align") + Key("A"),
                    () => demo.InManual || demo.CanCancelAlign || demo.CanRealign, c);
        // 手動調整中の B は無効（AlignmentManager.ResetButtonEnabled=false）なので、ここも押せなくなる
        AddButtonAt(crt, new Vector2(xr, y), new Vector2(w, H), null,
                    () => demo.ShowAnyway(), 15,
                    () => "Show anyway" + Key("B"),
                    () => demo.CanShowAnyway, c);
        y -= H + G;
        AddButtonAt(crt, new Vector2(xl, y), new Vector2(w, H), null,
                    () => demo.ToggleManual(), 15,
                    () => (demo.InManual ? "Cancel manual" : "Manual adjust") + Key("Y"),
                    () => demo.InManual || demo.CanManual, c);
        // 消去は元に戻せないので、Poke では消さずに長押しを案内するだけにする（実行は A 長押しのみ）
        AddButtonAt(crt, new Vector2(xr, y), new Vector2(w, H), null,
                    () => demo.HintClearHold(), 15,
                    () => "Clear saved" + Key("A hold"),
                    () => demo.CanClearSaved, c);
        y -= H + G;
        AddButtonAt(crt, new Vector2(xl, y), new Vector2(w, H), null,
                    () => align?.ToggleSplatVisible(), 15,
                    () => (align != null && align.SplatVisible ? "Hide splats" : "Show splats") + Key("X"),
                    null, alBtn);
        AddButtonAt(crt, new Vector2(xr, y), new Vector2(w, H), null,
                    () => demo.SetStaffMode(false), 15,
                    () => "Close" + Key("Y+B hold"),
                    () => !demo.InManual, alBtn);
        y -= H + G;
        // 実験へ戻る（実験の最初の画面からデモへ入る操作と同じ割当。保存した位置合わせは残る）
        AddButtonAt(crt, new Vector2(0f, y), new Vector2(inner, H), null,
                    () => demo.SwitchToExperiment(), 15,
                    () => "Back to experiment mode" + Key("both sticks hold"),
                    () => demo.CanSwitchToExperiment, alBtn);
    }

    private int CpTotal => cpMeasure != null ? cpMeasure.MarkerCount : 0;

    private string FirstUnmetReason()
    {
        if (exp == null) return "";
        if (!exp.TrackingSteady)
            return "Tracking just moved — wait a few seconds for it to settle";
        if (!exp.ServerReady)
            return udp != null && udp.HasTarget
                ? "Server not responding — is run.py running?"
                : "Server not found — check run.py and the network";
        if (udp != null && udp.CvBusy)
            return $"CV is processing {udp.CvBusyCount} keyframes — wait for it to finish";
        if (udp != null && udp.CvUnknown)
            return "Waiting for CV status — if this persists, check localize";
        if (!exp.CvReady)     return "CV worker not running — start vptserver.cv.localize";
        if (!exp.PointsReady) return "No Virtual CP — place them in the Editor";
        if (!exp.SurveyReady)
        {
            // A を押したのに始まらなかったなら、まずその理由。押して無反応だと、
            // ボタンが効いていないのか条件が足りないのかを実機で区別できない。
            if (cpMeasure != null && cpMeasure.RecentlyRejected && !cpMeasure.IsRunning
                && !string.IsNullOrEmpty(cpMeasure.RejectReason))
                return cpMeasure.RejectReason;

            // Real CP の測定は実験の前に 1 回。無効化されたなら、その理由をそのまま出す。
            string why = cpMeasure != null ? cpMeasure.InvalidReason : "";
            return string.IsNullOrEmpty(why)
                   ? "Set the Real CPs once — " + Btn("A")
                   : why + " — set them again with " + Btn("A");
        }
        if (tipCalib != null && tipCalib.HasStaleCalibration)
            return $"Tip offset is from a previous session ({tipCalib.StoredAgeHours:F0} h ago)"
                   + " — recalibrate with " + Btn("Y");
        if (!exp.TipReady)    return "Calibrate the controller tip first — " + Btn("Y");
        return "";
    }

    /// <summary>PC 側にデータが届いているか。CP 実測値は測り直しが効かないので、
    /// 「端末に残っているが PC には無い」状態をはっきり見せる。</summary>
    private string UploadSummary()
    {
        if (udp == null) return "";
        int pending = udp.PendingEventCount;
        if (pending == 0) return $"Saved on PC: {udp.ServerEventCount} records";
        return Amber($"{pending} records not yet on the PC — keep the app running");
    }

    private string LastTrialSummary()
    {
        if (exp == null || exp.TrialIndex == 0) return "No trials recorded yet";
        float t = exp.TimeToLock;
        return t >= 0f ? $"Last trial: aligned after {t:F1} s, {exp.KfAtLock} keyframes"
                       : "Last trial: no confirmed alignment";
    }

    private int Accepted => align != null ? align.AcceptedCount : 0;
    private int Rejected => align != null ? align.RejectedCount : 0;

    /// <summary>収集中の状況。詰まっている場所が一目で分かるようにする。
    ///
    /// ロックしないときの原因は 3 段階のどこかにある:
    ///   (1) キーフレームが PC に届いていない  → 送信数と PC 採用数を比べる
    ///   (2) PC で解が棄却されている            → 棄却理由をそのまま出す
    ///   (3) 解は来ているが一致しない            → 一致回数とずれを出す
    /// 分けて表示しないと、実機側からは全部「進まない」に見えてしまう。</summary>
    private string RunningMetrics => AlignProgressMetrics(exp != null ? exp.KfSent : 0);

    /// <summary><see cref="RunningMetrics"/> の本体（デモの再位置合わせ中も同じ表示を使う）。</summary>
    private string AlignProgressMetrics(int sent)
    {
        if (udp == null || !udp.ServerAlive) return $"Sent {sent} · server not responding";

        string pc = $"server {udp.CvAcceptedCount}";
        if (udp.CvBusy) pc += $" (+{udp.CvBusyCount})";

        string why = udp.CvReason;
        if (!string.IsNullOrEmpty(why) && !udp.CvPublished)
            return $"Sent {sent} · {pc}\n{Amber("Rejected: " + why)}";
        if (Accepted == 0 && string.IsNullOrEmpty(why))
            return $"Sent {sent} · {pc} · solving…";
        return $"Sent {sent} · {pc} · accepted {Accepted} · {AgreeText}";
    }

    /// <summary>連続一致の進捗。独立に解かれた解が同じ答えに落ち着いたかが確定条件。</summary>
    private string AgreeText
    {
        get
        {
            if (align == null) return "";
            string shift = align.LastSceneShiftM >= 0f ? $" ({align.LastSceneShiftM * 100f:F0} cm apart)" : "";
            return $"match {align.AgreementCount}/{align.RequiredAgreements}{shift}";
        }
    }

    private static string Cm(float m) => m >= 0f ? $"{m * 100f:F1} cm" : "–";

    /// <summary>説明文の中に出てくる物理ボタン名。ボタンラベルの割当タグ（<see cref="Key"/>）と
    /// 同じ色・太字にして、地の文のどれが「押すもの」なのかを拾えるようにする。
    /// <see cref="Amber"/> で包む文字列の中では使わない（色タグの入れ子になるため）。</summary>
    private static string Btn(string binding) =>
        $"<b><color=#{ColorUtility.ToHtmlStringRGB(ColKey)}>{binding}</color></b>";

    private static string Amber(string s) =>
        $"<color=#{ColorUtility.ToHtmlStringRGB(ColWarn)}>{s}</color>";

    // ---- 個々の要素へ流し込む ----

    private void SetHeader(string left, string right)
    {
        if (_headerText != null) _headerText.text = left;
        if (_headerRight != null) _headerRight.text = right;
    }

    private void SetBanner(Color accent, string title, string sub)
    {
        if (_accent != null)    _accent.color = accent;
        if (_titleText != null) _titleText.text = title;
        if (_subText != null)   _subText.text = sub;
    }

    /// <summary>進捗バー。fraction が負ならバーごと隠す（局面によっては進捗の概念が無い）。</summary>
    private void SetProgress(float fraction, string label)
    {
        bool show = fraction >= 0f;
        if (_barBg != null) _barBg.enabled = show;
        if (_progressFill != null) _progressFill.enabled = show;
        if (_progressFillRect != null && show)
        {
            var sd = _progressFillRect.sizeDelta;
            sd.x = Mathf.Clamp01(fraction) * _progressBarWidth;
            _progressFillRect.sizeDelta = sd;
        }
        if (_progressText != null) _progressText.text = label;
    }

    private void SetMetrics(string s) { if (_metricsText != null) _metricsText.text = s; }

    /// <summary>準備チェック。●=OK / ▲=未検証 / ✕=未達 を 1 行に並べる。</summary>
    private void SetCheck()
    {
        if (_checkText == null) return;
        if (exp == null) { _checkText.text = ""; return; }

        string srv = exp.ServerReady && udp != null && udp.RttMs >= 0f
                     ? $"Server {udp.RttMs:F0}ms" : "Server";

        // CV は 3 状態。hloc の 1 バッチは数秒かかりその間ハートビートが止まるので、
        // 「処理中」を「停止」と同じ ✕ で出すと誤解を招く。
        string cv;
        if (udp != null && udp.CvBusy)          cv = Dot(1, $"CV {udp.CvBusyCount}");
        else if (udp != null && udp.CvUnknown)  cv = Dot(1, "CV ?");
        else                                     cv = Dot(exp.CvReady ? 2 : 0, "CV");

        // 先端も 3 状態。前回セッションの値が残っているだけの状態を緑にすると、
        // 別の物理構成で取った値をそのまま使ってしまう。
        string tip = tipCalib != null && tipCalib.HasStaleCalibration
                     ? Dot(1, "Tip old")
                     : Dot(exp.TipReady ? 2 : 0,
                           tipCalib != null && tipCalib.IsCalibrated
                           ? $"Tip {tipCalib.ResidualMm:F1}mm" : "Tip");

        // トラッキングは正常なのが当たり前なので、**異常なときだけ**出す。
        // 常に並べると 1 行に収まらず、他の項目が Truncate で消えてしまう。
        string trk = exp.TrackingSteady ? "" : "   " + Dot(0, "Tracking");
        // 未送達がある間だけ出す。0 が常態なので、出ていること自体が注意喚起になる。
        int pending = udp != null ? udp.PendingEventCount : 0;
        string up = pending > 0 ? "   " + Dot(1, $"Upload {pending}") : "";

        // CP は 2 段構え。Virtual CP（Editor か VR で配置）と Real CP（実測）の両方が要る。
        // 片方だけ緑にすると、実測を忘れたまま試行を始めてしまう。
        string cp = exp.PointsReady
                    ? Dot(exp.SurveyReady ? 2 : 0,
                          exp.SurveyReady ? $"Real CP {CpTotal}" : "Real CP not set")
                    : Dot(0, "No Virtual CP");

        // Virtual CP を VR で動かしたセッションであることを残す。シーンに焼かれていない
        // 変更を抱えたまま実験を終えると、PC の JSON を取り込み忘れて次回に消える。
        int edited = cpEdit != null ? cpEdit.EditedCount : 0;
        string ed = edited > 0 ? "   " + Dot(1, $"Virtual CP edited {edited}") : "";

        _checkText.text = string.Join("   ",
            Dot(exp.ServerReady ? 2 : 0, srv), cv, tip, cp) + trk + up + ed;
    }

    /// <summary>状態ドット。level: 0=未達(赤) / 1=未検証(琥珀) / 2=OK(緑)。</summary>
    private static string Dot(int level, string label)
    {
        Color c = level >= 2 ? ColGood : level == 1 ? ColWarn : ColBad;
        string h = ColorUtility.ToHtmlStringRGB(c);
        string glyph = level >= 2 ? "●" : level == 1 ? "▲" : "✕";
        return $"<color=#{h}>{glyph}</color> {label}";
    }

    /// <summary>開発用の診断（小さく常時表示）。実験が止まったとき、その場で切り分けるため。</summary>
    private void SetDiag()
    {
        if (_diagText == null) return;
        string server = udp != null ? udp.TargetDescription : "(no UDPManager)";
        string state  = align != null ? align.State.ToString() : "-";
        string auto   = uploader != null ? (uploader.AutoCaptureOn ? "on" : "off") : "-";
        string inputs = demo != null ? (demo.InManual ? "manual" : "locked")
                      : exp != null && exp.ExperimentInputEnabled ? "experiment" : "manual";

        string cv = "cv –";
        if (udp != null && udp.ServerAlive)
            cv = udp.CvBusy ? $"cv busy({udp.CvBusyCount})"
               : udp.CvAlive ? "cv ok"
               : (udp.CvAgeS < 0f ? "cv down" : $"cv stale({udp.CvAgeS:F0}s)");
        string sess = udp != null && udp.ServerAlive ? udp.ServerSession : "";

        _diagText.text = $"{server} · {cv} · auto {auto} · {state} · input {inputs}\n{sess}";
    }

    // ---------------- Poke ----------------

    private void UpdatePoke()
    {
        for (int i = 0; i < _buttons.Count; i++)
        {
            var b = _buttons[i];
            if (b.dynamicLabel != null && b.label != null) b.label.text = b.dynamicLabel();
            bool on = b.isEnabled == null || b.isEnabled();

            bool nowLeft  = _leftTip  != null && Inside(b.rect, _leftTip.position);
            bool nowRight = _rightTip != null && Inside(b.rect, _rightTip.position);
            if (on && ((nowLeft && !b.leftInside) || (nowRight && !b.rightInside))) Fire(b);
            b.leftInside = nowLeft; b.rightInside = nowRight;

            if (b.image != null)
            {
                Color c = Time.unscaledTime < b.flashUntil ? new Color(0.38f, 0.66f, 1f, 0.85f) : b.baseColor;
                if (!on) c = new Color(c.r, c.g, c.b, c.a * 0.35f);   // 押せないボタンは淡く
                b.image.color = c;
            }
            if (b.label != null)
            {
                b.label.color = on ? ColText : ColFaint;
                // ラベル中の割当タグ（[X] など）は rich text で色を持つので b.label.color では暗くならない。
                // CanvasRenderer のアルファは文字ごとの色にも掛かるので、押せないボタンの割当だけが
                // 明るく浮いて「押せそう」に見えるのを防げる。
                b.label.canvasRenderer.SetAlpha(on ? 1f : 0.6f);
            }
        }
    }

    // 指先がボタン面の矩形内かつ面から pokeDepthM 以内か（Canvas スケールに依らず world 換算）
    private bool Inside(RectTransform rect, Vector3 worldTip)
    {
        Vector3 lp = rect.InverseTransformPoint(worldTip);
        float worldZ = Mathf.Abs(lp.z) * Mathf.Abs(rect.lossyScale.z);
        return worldZ < pokeDepthM && rect.rect.Contains(new Vector2(lp.x, lp.y));
    }

    private void Fire(HudButton b)
    {
        b.flashUntil = Time.unscaledTime + 0.15f;
        try { b.action?.Invoke(); }
        catch (Exception e) { Debug.LogWarning("[AlignmentHud] button action error: " + e.Message); }
    }

    // ---------------- UI 構築（実行時） ----------------
    //
    // レイアウトは上から順に y カーソルを下ろしながら積む。各要素の高さは
    // 「想定される最大行数」で確保し、1 行想定の欄は Truncate にして隣へはみ出させない。

    private const float PanelW = 520f;
    private const float Pad    = 22f;      // 左右余白
    private const float RowGap = 14f;      // ブロック間の余白

    private void BuildUi()
    {
        bool hasExp = HasExp;
        // 副ボタンは 4 行（校正＋測量 / CP 編集＋Skip / Revert 2 種 / Splats）。
        // 空きを残すと視界を無駄に塞ぐし、余白の意味が読めなくなるので、行数ぶんだけ確保する。
        // 実験なしは 2 列 3 行（→ BuildAlignmentOnlyButtons）。400 のままだと最下段が
        // 下の罫線と診断行に重なる（従来の 2 行構成でも既に 28px 食い込んでいた）。
        // **中身を足したら必ずこの値を直す。** 行の積み上げは y カーソルで、はみ出しても
        // エラーにならず下の診断行に重なるだけなので、目視では気づきにくい。
        // 670 → 688: 準備チェックを 2 行ぶん（22 → 40px）にしたぶん背を高くした。
        // デモ（628）: 指標行の下に 罫線＋チェック行（40）＋ボタン 2 列 3 行＋全幅 1 行（実験へ戻る）。
        // 最下段の下端が上から 546px、診断行の罫線が下から 60px（= 上から 568px）なので 22px 空く。
        float h = demo != null ? 628f : hasExp ? 688f : 510f;

        var canvasGo = new GameObject("HudCanvas");
        canvasGo.transform.SetParent(transform, false);
        _canvas = canvasGo.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.WorldSpace;
        var crt = _canvas.GetComponent<RectTransform>();
        crt.sizeDelta = new Vector2(PanelW, h);
        canvasGo.transform.localScale = Vector3.one * 0.001f;   // 1px = 1mm

        var panel = NewUi("Panel", crt, Vector2.zero, new Vector2(PanelW, h));
        panel.AddComponent<Image>().color = ColBg;

        float inner = PanelW - Pad * 2f;
        float y = h / 2f - Pad;          // 上端から下へ積む y カーソル

        // --- ヘッダ（小さく・大文字・左右振り分け）---
        y -= 16f;
        _headerText = AddText(NewUi("Header", crt, new Vector2(-inner / 4f, y), new Vector2(inner / 2f, 22f)),
                              15, TextAnchor.MiddleLeft, ColFaint, false);
        _headerRight = AddText(NewUi("HeaderR", crt, new Vector2(inner / 4f, y), new Vector2(inner / 2f, 22f)),
                               15, TextAnchor.MiddleRight, ColFaint, false);
        y -= 16f;

        y -= 8f;
        AddRule(crt, y, inner);
        y -= RowGap;

        // --- 指示（アクセントバー＋タイトル＋補足）---
        // タイトル 1 行 + 補足 **3 行**を想定して高さを確保する。補足は縦 Truncate なので、
        // 溢れた行は警告も無く消える——CP 編集の指示（`… · sticks: move the scene`）が実際に
        // 1 行まるごと落ちていた。**HUD で最も重要な文字列**なので、行数のほうを合わせる。
        // 実測（LegacyRuntime.ttf / fontSize 17 / 幅 458）: 1 行 19px・2 行 39px・3 行 59px。
        // SubH を増やさずに 4 行目を書くと消える。**補足を長くするときはここも直す。**
        const float TitleH = 40f, SubH = 60f;
        float bannerH = TitleH + SubH;
        var accentGo = NewUi("Accent", crt, new Vector2(-inner / 2f + 2f, y - bannerH / 2f), new Vector2(4f, bannerH));
        _accent = accentGo.AddComponent<Image>();
        _accent.color = ColReady;

        float textX = -inner / 2f + 18f + (inner - 18f) / 2f;
        float textW = inner - 18f;
        y -= TitleH / 2f;
        _titleText = AddText(NewUi("Title", crt, new Vector2(textX, y), new Vector2(textW, TitleH)),
                             30, TextAnchor.MiddleLeft, ColText, false);
        y -= TitleH / 2f;
        y -= SubH / 2f;
        _subText = AddText(NewUi("Sub", crt, new Vector2(textX, y), new Vector2(textW, SubH)),
                           17, TextAnchor.UpperLeft, ColMuted, true);
        y -= SubH / 2f;
        y -= RowGap;

        // --- 進捗バー（細い線）＋ ラベル ---
        const float BarH = 6f;
        _progressBarWidth = inner;
        y -= BarH / 2f;
        var barBg = NewUi("BarBg", crt, new Vector2(0, y), new Vector2(inner, BarH));
        _barBg = barBg.AddComponent<Image>();
        _barBg.color = new Color(1f, 1f, 1f, 0.10f);
        var fill = new GameObject("BarFill", typeof(RectTransform));
        _progressFillRect = fill.GetComponent<RectTransform>();
        _progressFillRect.SetParent(barBg.GetComponent<RectTransform>(), false);
        _progressFillRect.anchorMin = _progressFillRect.anchorMax = new Vector2(0f, 0.5f);
        _progressFillRect.pivot = new Vector2(0f, 0.5f);
        _progressFillRect.anchoredPosition = Vector2.zero;
        _progressFillRect.sizeDelta = new Vector2(0f, BarH);
        _progressFill = fill.AddComponent<Image>();
        _progressFill.color = ColReady;
        y -= BarH / 2f + 6f;

        y -= 11f;
        _progressText = AddText(NewUi("ProgressLabel", crt, new Vector2(0, y), new Vector2(inner, 22f)),
                                15, TextAnchor.MiddleLeft, ColMuted, false);
        y -= 11f;
        y -= RowGap;

        // --- ライブ数値（2 行想定）---
        const float MetricsH = 42f;
        y -= MetricsH / 2f;
        _metricsText = AddText(NewUi("Metrics", crt, new Vector2(0, y), new Vector2(inner, MetricsH)),
                               16, TextAnchor.UpperLeft, ColText, true);
        y -= MetricsH / 2f;

        if (demo != null)
        {
            y -= RowGap;
            AddRule(crt, y, inner);
            y -= RowGap;
            const float DemoCheckH = 40f;
            y -= DemoCheckH / 2f;
            _checkText = AddText(NewUi("Check", crt, new Vector2(0, y), new Vector2(inner, DemoCheckH)),
                                 15, TextAnchor.MiddleLeft, ColText, true);
            y -= DemoCheckH / 2f;
            BuildDemoButtons(crt, y - RowGap, inner);
            BuildDiag(crt, h, inner);
            BuildVisitorHint();
            return;
        }

        if (!hasExp)
        {
            BuildAlignmentOnlyButtons(crt, y - RowGap, inner);
            BuildDiag(crt, h, inner);
            return;
        }

        y -= RowGap;
        AddRule(crt, y, inner);
        y -= RowGap;

        // --- 準備チェック（**2 行ぶんの高さを取る**）---
        // 常態（Server / CV / Tip / Real CP の 4 つ）は 1 行に収まる（実測 319px < 476px）。
        // 収まらなくなるのは Tracking・Upload・Virtual CP edited が足された異常時で、
        // 全部出ると 650px＝パネル幅の 1.4 倍になり、以前は文字が枠の外へはみ出していた。
        // best fit だけに任せると 10px まで縮んで読めなくなるので、**2 行めを許して**
        // 15px のまま折り返させる（650/476 → 2 行 = 36px ≤ 40px）。
        // 一番読ませたい警告が出ている局面なので、小さくするより 1 行ぶん背を高くする。
        const float CheckH = 40f;
        y -= CheckH / 2f;
        _checkText = AddText(NewUi("Check", crt, new Vector2(0, y), new Vector2(inner, CheckH)),
                             15, TextAnchor.MiddleLeft, ColText, true);
        y -= CheckH / 2f;
        y -= RowGap;

        // --- 主ボタン ---
        const float MainH = 52f;
        y -= MainH / 2f;
        float modeW = 118f, gap = 10f;
        float mainW = inner - modeW - gap;
        AddButtonAt(crt, new Vector2(-inner / 2f + mainW / 2f, y), new Vector2(mainW, MainH),
                    null, MainButtonAction, 21,
                    () => exp != null && exp.State == ExperimentController.TrialState.Running
                          ? "Abort" + Key("Y") : "Start trial" + Key("B"),
                    // **準備が揃うまで始めさせない**（CanStartTrial に測量済みが含まれる）
                    () => exp != null && (exp.State == ExperimentController.TrialState.Running
                                          || exp.CanStartTrial),
                    new Color(0.16f, 0.34f, 0.55f, 0.92f));
        AddButtonAt(crt, new Vector2(inner / 2f - modeW / 2f, y), new Vector2(modeW, MainH),
                    null, null, 15,
                    () => exp == null ? "" : exp.AllTrialsDone ? "done"
                          : $"{Mathf.Max(0, exp.PlannedTrials - exp.CompletedTrials)} left",
                    () => false,                       // 表示専用
                    new Color(1f, 1f, 1f, 0.06f));
        y -= MainH / 2f + 10f;

        // --- 副ボタン ---
        // **実験の流れに必要なものだけを置く。**
        // 送信は試行が自動で制御するので手動の送信ボタン（Auto / Send one）は要らず、
        // 押せば進行中の試行を壊すだけだった。手動位置合わせ（Manual / Unlock / Recenter）も
        // 自動位置合わせを評価する実験では使う場面が無く、誤爆すると測定が無効になる。
        // 残すのは「準備に要るもの（先端校正・CP の配置と保存）」と「見比べる（Splats）」だけ。
        const float SubH2 = 40f, SGap = 8f;
        float sbW = (inner - SGap * 2f) / 3f;
        float[] xs = { -(sbW + SGap), 0f, (sbW + SGap) };
        Color exBtn = new Color(1f, 1f, 1f, 0.10f);
        Color alBtn = new Color(1f, 1f, 1f, 0.06f);

        // 物理ボタンの割当は **文脈で変わる**（Y は 4 役、A/B は短長押しで 2 役ずつ）。
        // 以前はラベル側で割当タグを出し分けていたが、**押せない局面ではボタンごと無効にする**
        // ほうが正しい。出し分けだけだと、たとえば編集中に「Set Real CP」を Poke できてしまい、
        // 測定と編集が同時に走って右トリガーが両方に効く（記録と配置が同時に起きる）。
        // 無効にしてしまえば、ラベルの割当は「有効なときの割当」で常に正しい。
        Func<bool> running   = () => exp != null && exp.State == ExperimentController.TrialState.Running;
        Func<bool> surveying = () => cpMeasure != null && cpMeasure.IsRunning;
        Func<bool> editing   = () => cpEdit != null && cpEdit.IsRunning;

        y -= SubH2 / 2f;
        AddButtonAt(crt, new Vector2(xs[0], y), new Vector2(sbW, SubH2), null,
                    () => tipCalib?.ToggleCalibration(), 15,
                    () => (tipCalib != null && tipCalib.IsRunning ? "Finish calib" : "Calibrate tip")
                          + Key("Y"),
                    // Y が校正に割り当たるのは「試行中でも測量中でも編集中でもない」ときだけ
                    // （→ ExperimentController.HandleExperimentInput）。校正中は終了に要るので通す。
                    () => tipCalib != null
                          && (tipCalib.IsRunning || (!running() && !surveying() && !editing())),
                    exBtn);
        // Real CP の測定は **実験の前に 1 回だけ**。Virtual CP（モデル上）も Real CP（部屋）も
        // 試行では変わらないので、試行ごとに測り直す必要は無い。
        // **やり直しを "Re-set" とは書かない。** このプロジェクトの RESET は試行リセット
        // （UDP type=4）を指すので、押すと試行が飛ぶと読まれる。"Redo" にする。
        // **測定中の割当タグは A ではなく Y。** A 短押しは「仮置きを確定して次へ」に取られており、
        // 測定中に A を押しても中断しない（余分な 1 押しで 6 点の測量が消えるのを避けるため
        // → ExperimentController.HandleAButton）。中断は Y だけ。
        AddButtonAt(crt, new Vector2((xs[1] + xs[2]) / 2f, y), new Vector2(sbW * 2f + SGap, SubH2), null,
                    () => cpMeasure?.ToggleSurvey(), 15,
                    () => cpMeasure != null && cpMeasure.IsRunning
                          ? "Cancel" + Key("Y")
                          : (cpMeasure != null && cpMeasure.IsSurveyed ? "Redo Real CP" : "Set Real CP")
                            + Key("A tap"),
                    () => cpMeasure != null && !running() && !editing(), exBtn);
        y -= SubH2 + SGap;

        // --- model 側 CP の VR 編集（位置合わせ前の準備。任意）---
        // Editor のマウス配置は視線方向の奥行きが取れず、その誤差は系統誤差として報告値に乗る。
        // **位置合わせ済みでは押せない**: オーバーレイが重なって見える状態で動かすと、
        // 無意識に実物へ寄せてしまい誤差が 0 側にバイアスする（答えを見て定規を合わせるのと同じ）。
        //
        // **2 行に割る。** 物理ボタンの割当をラベルに添えると 1 行 4 分割では文字が隣へはみ出す
        // （ボタンの Text は Overflow なのでクリップされず、隣のラベルに重なって両方読めなくなる）。
        // 割当を出さないより、パネルが 1 行ぶん高くなるほうがましと判断した。
        // **`Skip` が要る**のは、直したいのが CP_04 だけという場合に、前へ進む手段が「置く」しか
        // 無いと CP_01..03 まで置き直す羽目になるため（合っている点を触れば精度が下がる）。
        //
        // **編集中の 3 つ（Skip / Revert this / Revert all）は物理ボタンに載せてある。**
        // 以前は Poke（空中のボタンを手で突く）専用だったが、40px の板を 3cm 以内まで突く操作は
        // 実機で狙えず、「機能が無い」と受け取られた。編集中は A 短押し・B 短押し・B 長押しが
        // どれも他の役を持たない（測量トグルと試行開始は編集中ブロック）ので、そこへ載せた。
        float eEdit = 288f;
        float eSkip = inner - eEdit - SGap;
        AddButtonAt(crt, new Vector2(-inner / 2f + eEdit / 2f, y), new Vector2(eEdit, SubH2), null,
                    () => cpEdit?.ToggleEdit(), 15,
                    // 開始は A 長押しのみ。終了は Y でもできる（測量の「中断」と同じ指）
                    () => cpEdit != null && cpEdit.IsRunning
                          ? "Done editing" + Key("Y / A hold")
                          : "Edit Virtual CP" + Key("A hold"),
                    () => cpEdit != null && (cpEdit.IsRunning || string.IsNullOrEmpty(cpEdit.BlockReason)),
                    exBtn);
        // A 短押しは 1 つの意味（「この点を確定して次へ」）だが、**仮置きの有無でラベルが変わる**。
        // 測定中も同じボタンが確定を担うので、ここは編集専用にせず両モードから使う。
        AddButtonAt(crt, new Vector2(inner / 2f - eSkip / 2f, y), new Vector2(eSkip, SubH2), null,
                    () => { if (cpMeasure != null && cpMeasure.IsRunning) cpMeasure.ConfirmPoint();
                            else cpEdit?.AcceptCurrent(); }, 15,
                    () => ((cpMeasure != null && cpMeasure.HasPending) || (cpEdit != null && cpEdit.HasPending)
                           ? "Confirm" : "Skip") + Key("A"),
                    // 仮置きがあるなら常に押せる。無いときの Skip は編集中だけの機能で、
                    // 末尾では「次」が無いので落とす（押しても Reject が出るだけ）。
                    () => (cpMeasure != null && cpMeasure.HasPending)
                          || (cpEdit != null && cpEdit.IsRunning
                              && (cpEdit.HasPending || !cpEdit.AtLastPoint)), exBtn);
        y -= SubH2 + SGap;

        float eRev = (inner - SGap) / 2f;
        AddButtonAt(crt, new Vector2(-inner / 2f + eRev / 2f, y), new Vector2(eRev, SubH2), null,
                    () => cpEdit?.RevertCurrent(), 15,
                    () => "Revert this" + Key("B"),
                    () => cpEdit != null && cpEdit.IsRunning && cpEdit.CurrentIsEdited, exBtn);
        AddButtonAt(crt, new Vector2(inner / 2f - eRev / 2f, y), new Vector2(eRev, SubH2), null,
                    () => cpEdit?.RevertAll(), 15,
                    () => (cpEdit != null && cpEdit.EditedCount > 0
                           ? $"Revert all ({cpEdit.EditedCount})" : "Revert all") + Key("B hold"),
                    () => cpEdit != null && cpEdit.IsRunning && cpEdit.EditedCount > 0, exBtn);
        y -= SubH2 + SGap;

        // 3DGS の表示トグルだけは残す。パススルーと重ねて見比べるのが位置合わせの確認そのもの。
        // **いまどちらなのかをラベルに出す。** 起動直後は非表示で始まる（startHidden）ので、
        // 「Show / hide」だと消えているのが仕様なのか故障なのか分からない。
        // 隣にデモ展示モードへの入口を置く（CP も先端校正も要らないので、最初の画面からそのまま入れる。
        // 行を足さずに半分に割ったので、パネルの高さは変えていない）。
        float half = (inner - SGap) / 2f;
        AddButtonAt(crt, new Vector2(-inner / 2f + half / 2f, y), new Vector2(half, SubH2), null,
                    () => align?.ToggleSplatVisible(), 15,
                    () => (align != null && align.SplatVisible ? "Hide splats" : "Show splats") + Key("X"),
                    null, alBtn);
        AddButtonAt(crt, new Vector2(inner / 2f - half / 2f, y), new Vector2(half, SubH2), null,
                    () => exp?.SwitchToDemo(), 15,
                    () => "Demo mode" + Key("both sticks hold"),
                    () => exp != null && exp.CanSwitchToDemo, alBtn);

        BuildDiag(crt, h, inner);
    }

    private void MainButtonAction()
    {
        if (exp == null) return;
        if (exp.State == ExperimentController.TrialState.Running) exp.AbortTrial();
        else exp.StartTrial();
    }

    /// <summary>実験コンポーネントが無いシーン（位置合わせ単体の動作確認）用のボタン群。
    /// **2 列 3 行**。物理ボタンの割当をラベルに添えるので 3 列だと文字が隣へはみ出す
    /// （ボタンの Text は Overflow でクリップされない）。この局面では姿勢操作が全部生きている
    /// （PoseControls = Full）ので、割当は文脈で変わらず常に出してよい。</summary>
    private void BuildAlignmentOnlyButtons(RectTransform crt, float y, float inner)
    {
        const float H = 40f, G = 8f;
        float w = (inner - G) / 2f;
        float xl = -inner / 2f + w / 2f, xr = inner / 2f - w / 2f;
        Color c = new Color(1f, 1f, 1f, 0.06f);
        y -= H / 2f;
        AddButtonAt(crt, new Vector2(xl, y), new Vector2(w, H), null, () => align?.ToggleSplatVisible(), 15,
                    () => (align != null && align.SplatVisible ? "Splats ✓" : "Splats") + Key("X"), null, c);
        AddButtonAt(crt, new Vector2(xr, y), new Vector2(w, H), null, () => uploader?.ToggleAuto(), 15,
                    () => (uploader != null && uploader.AutoCaptureOn ? "Auto ✓" : "Auto") + Key("L grip hold"), null, c);
        y -= H + G;
        AddButtonAt(crt, new Vector2(xl, y), new Vector2(w, H), null, () => uploader?.SendOne(), 15,
                    () => "Send one" + Key("L grip tap"), null, c);
        AddButtonAt(crt, new Vector2(xr, y), new Vector2(w, H), null, () => align?.Unlock(), 15,
                    () => "Unlock" + Key("Y"), null, c);
        y -= H + G;
        AddButtonAt(crt, new Vector2(xl, y), new Vector2(w, H), null, () => align?.RecenterInFront(), 15,
                    () => "Recenter" + Key("R stick"), null, c);
        AddButtonAt(crt, new Vector2(xr, y), new Vector2(w, H), null, () => align?.AutoLevelAndFloorSnap(), 15,
                    () => "Level + floor" + Key("L stick"), null, c);
    }

    private void BuildDiag(RectTransform crt, float h, float inner)
    {
        float y = -h / 2f + Pad + 20f;
        AddRule(crt, y + 18f, inner);
        _diagText = AddText(NewUi("Diag", crt, new Vector2(0, y - 2f), new Vector2(inner, 34f)),
                            12, TextAnchor.UpperLeft, ColFaint, false);
    }

    /// <summary>細い罫線。面で区切るより視界を邪魔しない。</summary>
    private void AddRule(RectTransform parent, float y, float width)
    {
        var go = NewUi("Rule", parent, new Vector2(0, y), new Vector2(width, 1f));
        go.AddComponent<Image>().color = ColRule;
    }

    /// <summary>ボタンラベルに添える物理ボタンの割当。角括弧で囲って**押すもの**だと分かる形にし、
    /// 色は淡くしない（→ <see cref="ColKey"/>）。</summary>
    private static string Key(string binding) =>
        $"  <size=13><color=#{ColorUtility.ToHtmlStringRGB(ColKey)}>[{binding}]</color></size>";

    // かつて「物理ボタンが無い」印として `[poke]`（＝パネルを手で突く）を出していたが、
    // 空中に浮いた 40px の板を 3cm 以内まで突く操作は実機で狙えず、印の意味も伝わらなかった。
    // **HUD の全ボタンに物理割当を用意する**方針に変えたので、この印は不要になった。

    private void AddButtonAt(RectTransform parent, Vector2 pos, Vector2 size, string label, Action action,
                             int fontSize, Func<string> dynamicLabel, Func<bool> isEnabled, Color color)
    {
        var go = NewUi("Btn", parent, pos, size);
        var img = go.AddComponent<Image>();
        img.color = color;

        // ラベルの矩形はボタンより一回り小さくする。ボタン一杯だと文字が枠線に貼り付くうえ、
        // best fit が「収まっている」と判断する幅がボタンの端までになり、隣のボタンとの
        // 隙間（SGap = 8px）まで文字が迫って 2 つのラベルが 1 続きに見える。
        var lblGo = NewUi("Label", go.GetComponent<RectTransform>(), Vector2.zero,
                          new Vector2(Mathf.Max(24f, size.x - 16f), Mathf.Max(14f, size.y - 6f)));
        var lbl = AddText(lblGo, fontSize, TextAnchor.MiddleCenter, ColText, false);
        lbl.text = label ?? dynamicLabel?.Invoke() ?? "";

        _buttons.Add(new HudButton
        {
            rect = go.GetComponent<RectTransform>(),
            image = img, label = lbl, action = action,
            dynamicLabel = dynamicLabel, isEnabled = isEnabled, baseColor = color,
        });
    }

    private static GameObject NewUi(string name, RectTransform parent, Vector2 anchoredPos, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;
        return go;
    }

    // best fit の下限。1px = 1mm・約 1.2m 先なので、10px = 10mm ≒ 0.5° がぎりぎり読める大きさ。
    // ここまで縮めても収まらない文字列は**書いたほうが長すぎる**ので、Truncate で切って構わない。
    private const int   MinFontPx    = 10;
    private const float MinFontScale = 0.60f;   // 1 行の欄（縮まないと行が丸ごと消える）
    private const float MinFontScaleWrap = 0.70f; // 複数行の欄（行数で吸収できるぶん控えめ）

    /// <param name="wrap">true なら複数行を想定した欄（高さも複数行ぶん確保してある）。
    /// false は 1 行の欄。**どちらも枠の外へは出さない**（下の best fit）。</param>
    private Text AddText(GameObject go, int fontSize, TextAnchor anchor, Color color, bool wrap)
    {
        var t = go.AddComponent<Text>();
        t.font = uiFont != null ? uiFont : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = fontSize;
        t.color = color;
        t.alignment = anchor;
        t.supportRichText = true;

        // **枠の外へ文字を出さない。**
        // かつて 1 行の欄は HorizontalWrapMode.Overflow だった。Overflow はクリップしないので、
        // 内容が想定より長いと文字がパネルの外側や隣のボタンの上へそのまま伸びる（実機で発生:
        // 長い診断行・準備チェック行・`Place virtual CP_04 on the splats` のような長い見出し）。
        // かといって Wrap だけにすると、1 行ぶんの高さしか無い欄では 2 行目が縦 Truncate で
        // **黙って消える**。そこで Wrap ＋ best fit にし、収まらないときは字を少し小さくする。
        // best fit の上限は fontSize なので、通常の長さの内容は今までどおりの大きさのまま。
        // **レイアウト（rect の幅・高さ）のほうは変えていない**ので、行の積み上げは従来どおり。
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Truncate;
        t.resizeTextForBestFit = true;
        t.resizeTextMaxSize = fontSize;
        t.resizeTextMinSize = Mathf.Min(fontSize, Mathf.Max(MinFontPx,
            Mathf.RoundToInt(fontSize * (wrap ? MinFontScaleWrap : MinFontScale))));
        return t;
    }
}
