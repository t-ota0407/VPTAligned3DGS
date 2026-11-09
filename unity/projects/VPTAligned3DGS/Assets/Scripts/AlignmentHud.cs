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
///   - スタッフパネル（この主パネル）。Y＋B 長押しで開閉する。開いている間は右グリップでパネルだけ隠せる
///     （スタッフモードのまま。来場者の間の右グリップは何もしない）
///     （来場者はグリップを握ったまま操作するので、偶然開いてしまう）。
///
/// **ファイル構成（partial クラス）**: このファイル = 共通（表示・追従・各欄への流し込み・Poke）/
/// `AlignmentHud.Experiment.cs` = 実験の局面ごとの表示とボタン / `AlignmentHud.Demo.cs` = デモの
/// スタッフパネルと来場者向けの案内 / `AlignmentHud.Layout.cs` = UI の構築（行の積み上げ・ボタン・文字の欄）。
/// </summary>
public partial class AlignmentHud : MonoBehaviour
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
            // デモ: 主パネル（スタッフ用）はスタッフパネルを開いている間だけ出す。スタッフは右グリップで
            // パネルだけ隠せる（DemoController.StaffPanelHidden。スタッフモードのまま）。来場者の間は
            // 右グリップに何もさせない（来場者は握ったまま操作するので、偶然開いてしまう）。
            bool want = demo.StaffMode && !demo.StaffPanelHidden;
            if (_visible != want) { SetVisible(want); if (_visible) PlaceInFront(); }
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

        RefreshExperiment();
    }

    private int Accepted => align != null ? align.AcceptedCount : 0;
    private int Rejected => align != null ? align.RejectedCount : 0;

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

    /// <summary>ボタンラベルに添える物理ボタンの割当。角括弧で囲って**押すもの**だと分かる形にし、
    /// 色は淡くしない（→ <see cref="ColKey"/>）。</summary>
    private static string Key(string binding) =>
        $"  <size=13><color=#{ColorUtility.ToHtmlStringRGB(ColKey)}>[{binding}]</color></size>";

    // かつて「物理ボタンが無い」印として `[poke]`（＝パネルを手で突く）を出していたが、
    // 空中に浮いた 40px の板を 3cm 以内まで突く操作は実機で狙えず、印の意味も伝わらなかった。
    // **HUD の全ボタンに物理割当を用意する**方針に変えたので、この印は不要になった。
}
