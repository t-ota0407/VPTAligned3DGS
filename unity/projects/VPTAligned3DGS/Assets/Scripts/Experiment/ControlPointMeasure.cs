using System.Collections;
using System.Globalization;
using System.Linq;
using UnityEngine;

/// <summary>
/// コントロールポイントの**実環境側の位置を 1 回だけ実測する**（HUD 名 **Set Real CP**）。
///
/// **用語**: CP_01 は 1 つの物理的特徴で、記録すべき座標が 2 つある。
///   Virtual CP … 3DGS モデル上の座標。Editor か [[ControlPointEdit]] で置く（ログ名 cp_model_edit）
///   Real CP    … 同じ特徴の実物の位置。本コンポーネントで実測する（ログ名 cp_survey）
/// この呼び分けは HUD と**ワールドのラベル**（`VCP_01` / `RCP_01` → [[CpGizmos.VirtualLabel]]）
/// にだけ出る。クラス名・イベント名・JSON の鍵は `CP_01` のまま（過去ログとの互換）。
///
/// 誤差の定義に必要なのは、1 点につき次の 2 つだけ:
///   local … Virtual CP の座標（[[ControlPointMarker]] の localPosition）
///   tip   … Real CP の座標（校正済みコントローラ先端で実測したワールド座標）
/// 候補変換 T に対する誤差は |T(local) - tip|。どちらも T の推定に使っていないので評価が成立する。
/// （写真測量の用語では、推定に使わず検証にだけ使うこの 6 点は control point ではなく
///  **check point**（検証点）にあたる。呼称は CP のまま通し、論文でその旨を書く。）
///
/// **どちらも試行ごとに変わらない。** model 側は Editor で固定、実物は部屋が動かない限り固定。
/// だから測量は先端校正と同じ「実験の前に 1 回」の準備ステップでよく、以後は全試行で再利用する。
/// 以前は試行ごとに測り直していたが、それは
///   - 手間が試行数ぶん増える
///   - 測り方を間違える機会が試行数ぶん増える（実際、1 か所に立ったまま右トリガーを 6 回引き、
///     6 点すべてを同じ場所で記録した試行が出た）
/// だけで、得るものが無かった。
///
/// **唯一の前提は「Quest のワールド原点が動かないこと」。** 原点が回ると、測量した tip は
/// 過去の座標系の値になり使えなくなる。[[TrackingOriginWatcher]] が検出したら
/// [[InvalidateSurvey]] で無効化し、測量からやり直させる（黙って古い値を使わせない）。
///
/// 記録は ExperimentLogger 経由で JSONL（type=cp_survey）に残り、UDP で PC へも送られる。
/// 各試行の trial_start には、そのとき有効だった測量の通し番号が入るので、解析側で対応づく。
///
/// **記録は 2 段階（仮置き → 確定）。** 右トリガーで置いた時点ではまだ記録せず、A で確定して
/// 初めて次の点へ進む（→ <see cref="Place"/> / <see cref="ConfirmPoint"/>）。
/// 1 回のトリガーで確定していた頃は、**押した瞬間の先端位置がそのまま最終値**だった。
/// トリガーを引く動作自体でコントローラが数 mm 動くうえ、当てた場所が本当に狙った特徴かを
/// 確認する機会が無く、ずれても「1 つ戻ってやり直す」（＝前の点まで巻き戻す）しか手が無かった。
/// 仮置きを挟めば、白い点として置いた位置を**別の角度から見て確かめ**、納得するまで
/// 右トリガーで置き直してから確定できる。確定するまで記録も進行も起きない。
///
/// 見えるもの（測定中）。**ラベルの文字で V/R を見分ける**（色は照明とパススルーの背景で
/// 印象が変わるうえ、測定中は同じ点の 2 つの側がすぐ隣に並ぶ）:
///   球＋`VCP_nn`   … Virtual CP。シアン=未測 / 黄=いま触る点 / 緑=測定済み。位置合わせ前でも出す。
///                    ただし**球は目標ではない**。位置合わせ前なので実物の上には無く、
///                    「次はどの点か」を示すだけ。触るのはあくまで実物。
///   白い点＋`RCP_nn ?` … 仮置き。右トリガーで置いた位置。**まだ記録されていない。**
///   橙の点＋`RCP_nn`   … Real CP。A で確定した位置。押し損ね・同じ場所での二重記録に
///                    その場で気づくための目印。
/// **スプラットは測定中は消す**（実物を覆い隠さないため → <see cref="StartSurvey"/>）。
/// どのモデル特徴が CP_i なのか確かめたいときだけ X で覗く。
/// </summary>
public class ControlPointMeasure : MonoBehaviour
{
    [Header("References（未設定なら自動取得）")]
    [SerializeField] private AlignmentManager align;
    [SerializeField] private Transform rightControllerAnchor;
    [SerializeField] private ControllerTipCalibration tipCalib;
    [SerializeField] private GaussianSplatController splatController;

    [Header("Settings")]
    [Tooltip("表示する球の半径[m]。既定 0.025 = 直径 5cm。\n" +
             "CpGizmos.RefDistM(1m) 以内での実寸で、遠い点は距離に比例して拡大される")]
    [SerializeField] private float markerRadius = 0.025f;
    [Tooltip("CP 名ラベルの文字の高さ[m]（CpGizmos.RefDistM 以内での実寸）")]
    [SerializeField] private float labelHeightM = 0.05f;
    [Tooltip("先端が未校正でも測量を許す（既定 false: 系統誤差が乗るので止める）")]
    [SerializeField] private bool allowUncalibrated = false;
    [Tooltip("前の点からこの距離[m]以上、先端が動いていないと記録しない。\n" +
             "実機で「コントローラを動かさないまま右トリガーを 6 回引き、全点を同じ場所で" +
             "記録してしまう」（2 秒で 6 点・実測値の広がり 6cm）が起きた。誤差は数メートルに" +
             "なるが、位置合わせが悪いのか測り方が悪いのか、数字からは区別がつかない。\n" +
             "CP どうしが近い場合に備え、実際のしきい値は「この値」と「model 上の点間距離の半分」の" +
             "小さいほうを使う。")]
    [SerializeField] private float minTipSeparationM = 0.10f;

    private ControlPointMarker[] _markers;
    private bool _warnedUncalibrated;

    // ---- 表示物 ----
    // 生成・着色・毎フレームの追従は [[CpGizmos]] に委譲する（splatRoot の負スケール対策も含む）。
    // **この球は model 側 CP の VR 編集（[[ControlPointEdit]]）とも共有する。** 双方が自前で
    // 作ると同じマーカーの下に球が 2 つ出て z-fight するので、作るのはここだけにして
    // 編集側は色と点の追加だけを借りる（<see cref="Gizmos"/> / <see cref="ExternalGizmoHold"/>）。
    private CpGizmos       _gizmos;
    private CpGizmos.Layer _tipLayer;       // 確定した位置に出す橙の点
    private CpGizmos.Layer _previewLayer;   // 仮置き（未確定）の白い点
    private Transform      _head;

    // ---- 測量の状態 ----
    private bool      _running;          // 測量中
    private int       _idx;              // 測量中の対象 CP
    private int       _surveyId;         // 完了した測量の通し番号（1 から）
    private Vector3[] _tips;             // 実測したワールド座標（_markers と同じ並び）
    private bool[]    _got;              // その点を測ったか
    private string    _invalidReason = "";

    // ---- 仮置き（右トリガーで置いてから A で確定するまで）----
    // **確定するまで _tips / _got には触らない。** 未確定の値が測量に混ざると、
    // 「置いたつもりで確定していない点」を歯抜けとして検出できなくなる（→ Finish）。
    private bool    _hasPending;
    private Vector3 _pendingTip;
    private int     _pendingRetries;     // その点を置き直した回数（確定行に残す）

    // 「動いていないのに次の点を記録した」の検出用
    private bool    _hasPrevTip;
    private Vector3 _prevTip;
    private int     _prevMarkerIdx = -1;
    private string  _rejectReason = "";
    private float   _lastRejectT = -99f;

    // 配色は CpGizmos の定義（編集モードと意味を揃える）
    private static readonly Color ColPending = CpGizmos.ColPending;
    private static readonly Color ColCurrent = CpGizmos.ColCurrent;
    private static readonly Color ColDone    = CpGizmos.ColDone;
    private static readonly CultureInfo Inv  = CultureInfo.InvariantCulture;

    // ---------------- 公開状態 ----------------

    /// <summary>いま測量中か。</summary>
    public bool IsRunning => _running;
    /// <summary>使える測量があるか（全点ぶん揃っていて、原点も動いていない）。</summary>
    public bool IsSurveyed => _surveyId > 0 && string.IsNullOrEmpty(_invalidReason);
    /// <summary>有効な測量の通し番号（無ければ 0）。試行はこの番号を記録する。</summary>
    public int  SurveyId => IsSurveyed ? _surveyId : 0;
    /// <summary>測量が無効化されている理由（空なら問題なし）。</summary>
    public string InvalidReason => _invalidReason;

    public int    MarkerCount  => _markers != null ? _markers.Length : 0;
    public int    CurrentIndex => _idx;

    /// <summary>右トリガーで置いたが、まだ A で確定していない点を抱えているか。
    /// HUD はこれで指示文とボタンの意味を切り替える。</summary>
    public bool HasPending => _running && _hasPending;
    /// <summary>いまの仮置きを何回置き直したか（0 = 一発で決めた）。</summary>
    public int  PendingRetries => _pendingRetries;

    /// <summary>集めた CP マーカー（名前順）。model 側 CP の編集がこの並びを共有する。</summary>
    public ControlPointMarker[] Markers => _markers;

    /// <summary>球とラベルの実体。**作るのはここだけ**で、編集側は借りて着色するだけ。</summary>
    public CpGizmos Gizmos => _gizmos;

    /// <summary>測量中でなくても球を出し続けてほしい、という外部（編集モード）からの要求。
    /// これが無いと、編集中は「測量中でも姿勢適用済みでもない」ので球が消えてしまう。</summary>
    public bool ExternalGizmoHold { get; set; }

    /// <summary>いま対象にしている CP の**内部名**（`CP_01`）。ログ・JSON はこちらを使う。</summary>
    public string CurrentName  => (_markers != null && _markers.Length > 0)
                                  ? _markers[Mathf.Clamp(_idx, 0, _markers.Length - 1)].name : "-";
    /// <summary>その CP を HUD に出すときの表示名（`RCP_01`）。ワールドの橙の点と同じ文字にして、
    /// 「HUD で指示された点」と「視界の中の点」を突き合わせられるようにする。</summary>
    public string CurrentLabel => CpGizmos.RealLabel(CurrentName);
    /// <summary>測量の進捗 0..1。</summary>
    public float  Progress     => MarkerCount > 0 ? DoneCount / (float)MarkerCount : 0f;
    public int    DoneCount    { get { int n = 0; if (_got != null) foreach (var b in _got) if (b) n++; return n; } }

    /// <summary>直近 2.5 秒以内に「押したが受け付けられなかった」があったか（HUD 表示用）。</summary>
    public bool   RecentlyRejected => Time.realtimeSinceStartup - _lastRejectT < 2.5f;
    /// <summary>直近の拒否理由（HUD 用）。無ければ空。</summary>
    public string RejectReason => RecentlyRejected ? _rejectReason : "";

    /// <summary>3DGS の姿勢が一度でも適用されたか。球の位置が**実物の上に載る**条件。</summary>
    private bool PoseApplied =>
        align != null && (align.HasAcceptedSim3 || align.State == AlignmentManager.AlignState.Locked);

    /// <summary>球が「実物のところ」に出ているか（＝ 案内としてそのまま信用してよいか）。
    /// 位置合わせ前でも球は出すが、その位置は実世界と無関係。測量中は手動でシーンを寄せて
    /// 対応を取る（→ <see cref="AlignmentManager.PoseControlMode.SticksOnly"/>）。</summary>
    public bool CanGuideVisually => PoseApplied;

    /// <summary>球・ラベル・実測点を出す局面か。
    ///
    /// **Real CP の測定中と Virtual CP の編集中は <see cref="AlignmentManager.SplatVisible"/> に
    /// 連動させない。** 測定中はスプラットを消す（実物を覆い隠さないため → <see cref="StartSurvey"/>）
    /// が、以前ここが `&amp;&amp; SplatVisible` だったので球まで一緒に消え、「次はどの点か」の案内が
    /// 丸ごと無くなってしまう。この 2 つの局面では X は「スプラットを覗く／隠す」だけの意味に
    /// なり、球は出したままにする。
    /// それ以外（位置合わせ済みの通常表示）では従来どおり 3DGS と一緒に消える。</summary>
    private bool GizmosActive =>
        _running || ExternalGizmoHold
        || (PoseApplied && (align == null || align.SplatVisible));

    /// <summary>HUD 表示用の 1 行サマリ。</summary>
    public string StatusLine
    {
        get
        {
            if (_markers == null || _markers.Length == 0) return "No Virtual CP";
            if (_running)
                return (_hasPending ? $"Confirm {CurrentLabel}?" : $"Setting {CurrentLabel}")
                       + $" ({DoneCount}/{_markers.Length})";
            if (IsSurveyed) return $"Real CP set: {_markers.Length} points (#{_surveyId})";
            return string.IsNullOrEmpty(_invalidReason) ? "Real CP not set yet" : _invalidReason;
        }
    }

    /// <summary>Real CP の測定を受け付けられるか。受け付けられないなら理由を返す（空文字なら OK）。</summary>
    public string BlockReason
    {
        get
        {
            if (_markers == null || _markers.Length == 0) return "No Virtual CP placed";
            if (!allowUncalibrated && !(tipCalib != null && tipCalib.IsCalibrated))
                return tipCalib != null && tipCalib.HasStaleCalibration
                       ? "Tip offset is from a previous session. Calibrate now."
                       : "Calibrate the controller tip first";
            if (rightControllerAnchor == null) return "Right controller not detected";
            return "";
        }
    }

    /// <summary>CP の配置そのものが怪しいときの警告（HUD の準備チェック用）。空なら問題なし。
    ///
    /// Editor の初期値のまま残った CP は、モデル原点のすぐ近くにある丸い座標になりがちで、
    /// 実物に対応していない。そのまま測ると誤差が数メートルになり、位置合わせが悪いのか
    /// CP が未配置なのか、数字からは区別がつかない（実機で CP_04〜06 がこれだった）。
    /// 断定はできないので「疑い」として出す。</summary>
    public string SetupWarning
    {
        get
        {
            if (_markers == null || _markers.Length < 3) return "";
            var sus = new System.Collections.Generic.List<string>();
            for (int i = 0; i < _markers.Length; i++)
                if (IsSuspect(i)) sus.Add(CpGizmos.VirtualLabel(_markers[i].name));
            return sus.Count == 0 ? ""
                   : $"{string.Join(", ", sus)} may still be unplaced (near the model origin)";
        }
    }

    /// <summary>その CP が仮座標のまま残っている疑いがあるか（<see cref="SetupWarning"/> の 1 点版）。
    /// 編集モードが「まずここを直すべき」を赤で示すのに使う。</summary>
    public bool IsSuspect(int i)
    {
        if (_markers == null || _markers.Length < 3 || i < 0 || i >= _markers.Length) return false;
        var d = new float[_markers.Length];
        for (int k = 0; k < _markers.Length; k++) d[k] = _markers[k].transform.localPosition.magnitude;
        var s = (float[])d.Clone(); System.Array.Sort(s);
        float med = s[s.Length / 2];
        if (med <= 0.5f) return false;
        return d[i] < 0.25f * med;
    }

    // ---------------- ライフサイクル ----------------

    private void Awake()
    {
        // 編集モードが Start より前に球へ触れても困らないよう、実体は Awake で用意する。
        _gizmos  = new CpGizmos(markerRadius, labelHeightM);
        _tipLayer = _gizmos.NewLayer("Tip", CpGizmos.ColTip, 1.2f);
        // 仮置きは**別レイヤ**にする。Layer の色はマテリアル生成時に決まるので、
        // 同じレイヤで白→橙に塗り替えることができない。層を分ければ
        // 「白が消えて橙が出る」＝確定した、が見た目でもはっきりする。
        _previewLayer = _gizmos.NewLayer("Preview", CpGizmos.ColPreview, 1.2f);
    }

    private void Start()
    {
        if (align == null) align = FindFirstObjectByType<AlignmentManager>();
        if (tipCalib == null) tipCalib = FindFirstObjectByType<ControllerTipCalibration>();
        if (splatController == null) splatController = FindFirstObjectByType<GaussianSplatController>();
        var rig0 = FindFirstObjectByType<OVRCameraRig>();
        if (rightControllerAnchor == null && rig0 != null) rightControllerAnchor = rig0.rightControllerAnchor;
        _head = rig0 != null ? rig0.centerEyeAnchor : (Camera.main != null ? Camera.main.transform : null);
        RefreshMarkers();
    }

    /// <summary>シーン上の ControlPointMarker を名前順で集め直す。</summary>
    public void RefreshMarkers()
    {
        _gizmos.DestroyAll();
        _markers = FindObjectsByType<ControlPointMarker>(FindObjectsSortMode.None)
                   .OrderBy(m => m.name).ToArray();
        _tips = new Vector3[_markers.Length];
        _got  = new bool[_markers.Length];
        _idx = 0;
        Debug.Log("[ControlPointMeasure] マーカー " + _markers.Length + " 個を検出");
    }

    private void Update()
    {
        if (_markers == null || _markers.Length == 0) return;

        // 球とラベルを出す条件は GizmosActive を参照（測定中・編集中はスプラットの表示に
        // 連動させない）。描画の失敗でこの下の入力受付まで落とさない（→ CpGizmos.Draw）。
        CpGizmos.Draw(() =>
        {
            if (!_gizmos.Built) { _gizmos.Build(_markers); RepaintSurveyColors(); }
            _gizmos.SetVisible(GizmosActive);

            // 「次はここ」の棒を立てる点。編集中は編集側が指すので触らない。
            if (_running) _gizmos.SetCurrent(_idx);
            else if (!ExternalGizmoHold) _gizmos.SetCurrent(-1);
        });

        if (!_running) return;

        // **入力の受付は表示に連動させない。** X で見比べたときに、押しても記録されない
        // 状態へ黙って落ちるのを防ぐ。
        //
        // 右トリガー = 仮置き（何度でも置き直せる）／ A = 確定して次へ（ExperimentController から）。
        // 左トリガーは「1 手戻す」で統一する: 仮置きがあればそれを捨て、無ければ前の点へ戻る。
        if (OVRInput.GetDown(OVRInput.RawButton.RIndexTrigger)) Place();
        if (OVRInput.GetDown(OVRInput.RawButton.LIndexTrigger))
        {
            if (_hasPending) DiscardPending();
            else             Back();
        }
    }

    // ---------------- 測量 ----------------

    /// <summary>測量の開始／終了トグル（HUD ボタン・物理ボタン用）。</summary>
    public void ToggleSurvey()
    {
        if (_running) CancelSurvey();
        else          StartSurvey();
    }

    public void StartSurvey()
    {
        string block = BlockReason;
        if (!string.IsNullOrEmpty(block))
        {
            // **黙って戻らない。** 押して何も起きないと、押せていないのか条件が足りないのかを
            // 実機で区別できない（編集モードの StartEdit は同じ理由で既に Reject を出している）。
            _rejectReason = block;
            _lastRejectT = Time.realtimeSinceStartup;
            StartCoroutine(RejectHaptic());
            Debug.LogWarning("[ControlPointMeasure] 測量を開始できません: " + block);
            return;
        }
        _running = true;
        _idx = 0;
        _got = new bool[_markers.Length];
        _tips = new Vector3[_markers.Length];
        _hasPrevTip = false; _prevMarkerIdx = -1; _rejectReason = "";
        _hasPending = false; _pendingRetries = 0;
        CpGizmos.Draw(() =>
        {
            _tipLayer.ClearAll();              // 前回の測量の記録点は残さない
            _previewLayer.ClearAll();          // 仮置きも持ち越さない
            RepaintSurveyColors();
        });
        // **Real CP を測る間はスプラットを消す。** ここで見るべきは実環境だけで、位置合わせ前の
        // スプラットは実世界と無関係な場所に浮いており、先端を当てたい実物そのものを覆い隠す
        // （tip の精度に直接効くので、見た目ではなく測定の問題）。収集中に消すのと同じ理由
        // （→ ExperimentController の SetSplatVisible(false)）。
        // 球・ラベル・橙の点は消えない（GizmosActive は測定中 SplatVisible に依らない）ので、
        // 「次はどの点か」の案内は残る。どのモデル特徴だったか確かめたいときは X で覗く。
        if (align != null) align.SetSplatVisible(false);
        ExperimentLogger.LogStatic("cp_survey_start", "\"points\":" + _markers.Length);
        Debug.Log($"[ControlPointMeasure] Real CP の測定開始: {_markers.Length} 点。"
                  + "各 CP の実物まで移動して先端を当て、右トリガーで仮置き → 白い点を確かめて A で確定。"
                  + "（触るのは実物。球は「次はどの点か」の目印でしかない。"
                  + "どのモデル特徴だったか確かめたいときは X でスプラットを覗く）");
    }

    public void CancelSurvey()
    {
        if (!_running) return;
        _running = false;
        // 未確定の仮置きは記録されていないので、そのまま捨てる（白い点も残さない）。
        _hasPending = false; _pendingRetries = 0;
        CpGizmos.Draw(() => _previewLayer.ClearAll());
        ExperimentLogger.LogStatic("cp_survey_cancel", "\"done\":" + DoneCount);
        Debug.Log("[ControlPointMeasure] 測量を中断しました（それまでの記録は使いません）");
    }

    /// <summary>測量を無効にする（ワールド原点が動いたときなど）。
    /// 測った tip は過去の座標系の値なので、黙って使い続けさせない。</summary>
    public void InvalidateSurvey(string why)
    {
        if (_surveyId == 0 && !_running) return;
        _invalidReason = string.IsNullOrEmpty(why) ? "The Real CP set is no longer valid" : why;
        if (_running) CancelSurvey();
        // 記録点はワールド座標なので、原点が動いた時点でもう実物の上に無い。
        // 残しておくと「そこで測った」という誤った目印になるので消す。
        _tipLayer.ClearAll();
        _previewLayer.ClearAll();
        ExperimentLogger.LogStatic("cp_survey_invalidated",
            "\"why\":\"" + (why ?? "").Replace("\"", "'") + "\",\"survey\":" + _surveyId);
        Debug.LogWarning("[ControlPointMeasure] 測量を無効化: " + _invalidReason
                         + "（実物の位置を測り直してください）");
    }

    /// <summary>コントローラ先端のワールド位置（校正済みオフセットを適用）。</summary>
    private bool TryGetTip(out Vector3 tip)
    {
        tip = Vector3.zero;
        if (rightControllerAnchor == null) { Debug.LogWarning("[ControlPointMeasure] 右コントローラ未検出"); return false; }

        bool calibrated = tipCalib != null && tipCalib.IsCalibrated;
        if (!calibrated && !allowUncalibrated)
        {
            _rejectReason = "Controller tip is not calibrated";
            _lastRejectT = Time.realtimeSinceStartup;
            StartCoroutine(RejectHaptic());
            if (!_warnedUncalibrated)
            {
                _warnedUncalibrated = true;
                Debug.LogError("[ControlPointMeasure] 先端が未校正です。追跡原点と物理的先端の差（数cm）が"
                               + "そのまま誤差に乗るため測量を中止します。先に先端校正を行ってください。");
            }
            return false;
        }
        Vector3 offset = tipCalib != null ? tipCalib.TipOffsetM : Vector3.zero;
        tip = rightControllerAnchor.TransformPoint(offset);
        return true;
    }

    /// <summary>移動量チェックの下限[m]。model 側の情報からどれだけ小さい値が出ても、しきい値を
    /// ここより下げない。**このガードが黙って無効になるのを防ぐ床。**
    /// 実際に起きた事故（先端を動かさず 6 点連打）は 6 点全体の広がりが 6cm だったので、
    /// 3cm あれば連続する 2 点の間で検出できる。</summary>
    private const float MinNeedM = 0.03f;

    /// <summary>「前の点から先端が動いていない」記録を弾く。
    ///
    /// 別々の CP を触ったなら、先端はその 2 点の間隔ぶん動いているはず。動いていないなら
    /// 同じ場所で連打しただけで、その記録は「測っていない」ことを意味する。
    /// **誤差の大小では弾かない**（それをやると測定結果に都合よくバイアスがかかる）。
    /// 見るのは「動いたかどうか」だけで、これは測定対象と独立している。
    ///
    /// **比べるのは tip どうし**（実測値と実測値）で、Virtual CP の位置とは比べない。
    /// 位置合わせ前の Virtual CP は実世界と無関係な場所にあるので、そこからの距離には
    /// 何の意味も無い（→ <see cref="CanGuideVisually"/>）。</summary>
    private bool TipMovedEnough(Vector3 tip)
    {
        if (!_hasPrevTip || _prevMarkerIdx < 0 || _prevMarkerIdx == _idx) return true;

        // model 上でその 2 点がどれだけ離れているか（ローカル座標間の距離。姿勢・符号に依らない）。
        float dModel = Vector3.Distance(_markers[_idx].transform.localPosition,
                                        _markers[_prevMarkerIdx].transform.localPosition);
        if (dModel < 1e-3f) return true;        // model 上で同一座標の 2 点（設定ミス）は素通し

        // 既定は固定 10cm。ただし 2 つの CP が実際に 20cm 以内にあると、それでは正しい記録まで
        // 弾いてしまう（このシーンの CP_02–CP_03 は実距離 15cm 程度）。そこで model 上の距離で
        // 緩めるが、**それを m に直せるときだけ**にする。以前は splatRoot のスケールを無条件に
        // 掛けていたが、位置合わせ前のあの値は手動合わせの途中値（今のシーンでは 0.7）で
        // 「model 1 単位が何 m か」を表していない。小さいスケールが入っているとしきい値が
        // 1cm 未満まで下がり、**このガードが存在する理由そのもの**（動かさず連打）を素通しさせる。
        float need = minTipSeparationM;
        if (TryGetMetricScale(out float s))
            need = Mathf.Max(MinNeedM, Mathf.Min(need, 0.5f * dModel * s));

        float moved = Vector3.Distance(tip, _prevTip);
        if (moved >= need) return true;

        _rejectReason = $"Tip has not moved ({moved * 100f:F0} cm since the last point). " +
                        "Walk to this point and touch it.";
        _lastRejectT = Time.realtimeSinceStartup;
        StartCoroutine(RejectHaptic());
        Debug.LogWarning($"[ControlPointMeasure] {_markers[_idx].name}: 前の点から {moved * 100f:F1} cm " +
                         $"しか動いていません（必要 {need * 100f:F0} cm）。同じ場所で連打していませんか。");
        return false;
    }

    /// <summary>model 座標 1 単位が実世界で何 m か。**分かるときだけ true を返す。**
    ///
    /// 分かるのは 2 通りだけ:
    ///   (a) 姿勢が適用済み   … splatRoot のスケールがそのまま比になっている
    ///   (b) 実測点が 2 点以上 … 実測した tip 間の距離 ÷ model 上の距離（**実測値だけで決まる**）
    /// それ以外では「分からない」を返し、呼び出し側は固定しきい値にフォールバックする。
    ///
    /// **位置合わせ前の splatRoot のスケールは使わない。** あれは手動合わせの途中値でしかなく、
    /// model 単位と m の比を表していない（このシーンのシーン初期値は 0.7）。以前はこれを無条件に
    /// 使っていて、「未合わせなら 1 にフォールバック」と書いてあったが、lossyScale が 0 に
    /// なることは無いのでその分岐は一度も通っていなかった。
    ///
    /// (b) は**中央値**を取る。CP の一部が仮座標のまま（model 上の距離が実物と対応しない）でも、
    /// 少数なら中央値なら引きずられない（→ <see cref="SetupWarning"/>）。</summary>
    private bool TryGetMetricScale(out float s)
    {
        s = 0f;
        if (_markers == null || _markers.Length == 0) return false;

        if (PoseApplied)
        {
            var root = _markers[0].transform.parent;
            s = root != null ? Mathf.Abs(root.lossyScale.x) : 0f;
            return s > 1e-4f;
        }

        // 実測済みの点だけから比を出す。model 側の座標も実測値も splatRoot の姿勢に依らないので、
        // 位置合わせ前でもこの比だけは意味を持つ。
        if (_tips == null || _got == null) return false;
        var ratios = new System.Collections.Generic.List<float>();
        for (int i = 0; i < _markers.Length; i++)
        {
            if (!_got[i]) continue;
            for (int j = i + 1; j < _markers.Length; j++)
            {
                if (!_got[j]) continue;
                float dm = Vector3.Distance(_markers[i].transform.localPosition,
                                            _markers[j].transform.localPosition);
                if (dm < 1e-3f) continue;
                ratios.Add(Vector3.Distance(_tips[i], _tips[j]) / dm);
            }
        }
        if (ratios.Count == 0) return false;
        ratios.Sort();
        s = ratios[ratios.Count / 2];
        return s > 1e-4f;
    }

    /// <summary>右トリガー: いまの先端位置を**仮置き**する（まだ記録しない）。
    ///
    /// 何度でも押し直せる。押すたびに白い点が移るだけで、_tips には書かない。
    /// 移動量ゲート（<see cref="TipMovedEnough"/>）はここで掛ける ── 「動かさず連打」は
    /// 置いた瞬間に気づけたほうがよく、比較相手は**確定済み**の前の点なので、
    /// 同じ点を何度置き直しても判定は変わらない。</summary>
    private void Place()
    {
        if (!TryGetTip(out Vector3 tip)) return;
        if (!TipMovedEnough(tip)) return;

        if (_hasPending) _pendingRetries++;
        else { _hasPending = true; _pendingRetries = 0; }
        _pendingTip = tip;
        _rejectReason = "";

        var m = _markers[_idx];
        StartCoroutine(Haptic());
        CpGizmos.Draw(() =>
            // ラベルに `?` を付ける。確定済みの橙の点と**文字でも**区別できないと、
            // 色の印象がパススルーの背景で変わったときに「もう記録した」と読み違える。
            _previewLayer.SetWorld(_idx, tip, CpGizmos.RealLabel(m.name) + " ?"));
        Debug.Log($"[ControlPointMeasure] {m.name} を仮置き tip={tip}"
                  + (_pendingRetries > 0 ? $"（{_pendingRetries} 回目の置き直し）" : "")
                  + " ── A で確定、右トリガーで置き直し、左トリガーで取り消し");
    }

    /// <summary>A: 仮置きを確定して次の点へ。**ここで初めて記録される。**</summary>
    public void ConfirmPoint()
    {
        if (!_running) return;
        if (!_hasPending)
        {
            // 押して無反応にしない。何をすれば進むのかをそのまま返す。
            _rejectReason = "Nothing to confirm — touch the point and pull the right trigger first";
            _lastRejectT = Time.realtimeSinceStartup;
            StartCoroutine(RejectHaptic());
            return;
        }
        Record(_pendingTip);
    }

    /// <summary>左トリガー: 仮置きを捨てる（点は進めない）。</summary>
    private void DiscardPending()
    {
        if (!_hasPending) return;
        int i = _idx;
        _hasPending = false; _pendingRetries = 0;
        CpGizmos.Draw(() => _previewLayer.Clear(i));
        StartCoroutine(RejectHaptic());
        Debug.Log("[ControlPointMeasure] 仮置きを取り消しました → " + CurrentName + " を置き直してください");
    }

    /// <summary>仮置きを実測値として確定する（<see cref="ConfirmPoint"/> からのみ呼ぶ）。</summary>
    private void Record(Vector3 tip)
    {
        var m = _markers[_idx];
        Vector3 lp = m.transform.localPosition;
        _tips[_idx] = tip;
        _got[_idx] = true;
        int retries = _pendingRetries;
        _hasPending = false; _pendingRetries = 0;

        // 参考値: いまの姿勢での見かけの誤差。測量そのものには使わない（解析は後から
        // 保存済みキーフレームで時刻ごとに解き直して計算する）が、実物を取り違えたときに
        // その場で気づく手がかりになる。
        float err = PoseApplied ? Vector3.Distance(m.transform.position, tip) : -1f;

        ExperimentLogger.LogStatic("cp_survey",
            "\"survey\":" + (_surveyId + 1) +
            ",\"name\":\"" + m.name + "\"" +
            ",\"local\":[" + F(lp.x, 5) + "," + F(lp.y, 5) + "," + F(lp.z, 5) + "]" +
            ",\"tip\":["   + F(tip.x, 5) + "," + F(tip.y, 5) + "," + F(tip.z, 5) + "]" +
            ",\"err_m\":" + F(err, 4) +
            // 確定するまでに何度置き直したか。0 が続くなら仮置きを見比べずに確定している
            // （＝2 段階にした意味が無い）ことの手がかりになる。解析側は未知の鍵として無視する。
            ",\"retries\":" + retries +
            ",\"tip_residual_mm\":" + F(tipCalib != null ? tipCalib.ResidualMm : -1f, 2) +
            // 校正の出どころを残す。前回セッションの値を流用した測定は、事後に区別できないと
            // 系統誤差の有無を判断できなくなる（解析スクリプトが警告を出す）。
            ",\"tip_source\":\"" + (tipCalib != null ? tipCalib.State.ToString() : "None") + "\"");
        Debug.Log($"[ControlPointMeasure] 測量 {m.name} tip={tip}"
                  + (err >= 0f ? $"（現在の見かけ誤差 {err * 100f:F1}cm）" : ""));

        _hasPrevTip = true; _prevTip = tip; _prevMarkerIdx = _idx; _rejectReason = "";
        StartCoroutine(Haptic());

        // **状態遷移を先に済ませ、描画は後ろに置く。** 逆順にすると描画の失敗が測量を止める
        // （実機で実際に止まった → <see cref="CpGizmos.Draw"/> の解説）。
        int rec = _idx;
        Advance();
        CpGizmos.Draw(() =>
        {
            _gizmos.SetColor(rec, ColDone);
            _previewLayer.Clear(rec);        // 白（仮置き）を消してから橙（確定）を出す
            // 実測した位置に点と CP 名を残す（押し損ね・同じ場所での二重記録にその場で気づくため）。
            // ラベルは `RCP_..`（model 側の球は `VCP_..`）。同名だと、すぐ隣に並ぶ 2 つを
            // 色でしか見分けられない（→ CpGizmos.RealLabel）。
            // ワールド固定で置く: 実測値はワールドで意味を持つので、シーンを動かしても付いてこない。
            _tipLayer.SetWorld(rec, tip, CpGizmos.RealLabel(m.name));
        });
    }

    /// <summary>1 つ前の点に戻る（測り直し）。飛ばす手段は用意しない
    /// ——欠けた測量は使えないので、「飛ばせる」ことに意味が無い。</summary>
    private void Back()
    {
        if (_idx <= 0) return;
        int from = _idx;
        _idx--;
        _got[_idx] = false;
        _prevMarkerIdx = -1;         // 測り直しの直後は移動量チェックの基準を捨てる
        CpGizmos.Draw(() =>
        {
            _gizmos.SetColor(from, ColPending);
            _tipLayer.Clear(_idx);   // 取り消した記録の目印を残さない
            _gizmos.SetColor(_idx, ColCurrent);
        });
        Debug.Log("[ControlPointMeasure] 1 つ戻る → " + CurrentName);
    }

    private void Advance()
    {
        _idx++;
        if (_idx >= _markers.Length)
        {
            _idx = _markers.Length - 1;
            Finish();
            return;
        }
        CpGizmos.Draw(() => _gizmos.SetColor(_idx, ColCurrent));
    }

    private void Finish()
    {
        // 全点そろっていなければ完了にしない（歯抜けの測量は使えない）。
        // ここも黙って戻らない ── 「最後まで測ったのに完了しない」は原因が見えない状態の代表例。
        for (int i = 0; i < _got.Length; i++)
        {
            if (_got[i]) continue;
            _rejectReason = $"{CpGizmos.RealLabel(_markers[i].name)} is not set yet — cannot finish";
            _lastRejectT = Time.realtimeSinceStartup;
            StartCoroutine(RejectHaptic());
            Debug.LogWarning("[ControlPointMeasure] 未測の点（" + _markers[i].name + "）が残っています。完了にしません。");
            return;
        }
        _running = false;
        _surveyId++;
        _invalidReason = "";
        ExperimentLogger.LogStatic("cp_survey_done",
            "\"survey\":" + _surveyId + ",\"points\":" + _markers.Length +
            ",\"span_m\":" + F(TipSpanM, 3));
        Debug.Log($"[ControlPointMeasure] 測量 #{_surveyId} 完了（{_markers.Length} 点 / "
                  + $"実測点の広がり {TipSpanM:F2} m）。以降の全試行でこの値を使います。");
    }

    /// <summary>実測した点の広がり[m]（外接直方体の対角）。小さすぎれば測り方が壊れている。</summary>
    public float TipSpanM
    {
        get
        {
            if (_tips == null || DoneCount < 2) return 0f;
            Vector3 mn = Vector3.positiveInfinity, mx = Vector3.negativeInfinity;
            for (int i = 0; i < _tips.Length; i++)
            {
                if (!_got[i]) continue;
                mn = Vector3.Min(mn, _tips[i]); mx = Vector3.Max(mx, _tips[i]);
            }
            return Vector3.Distance(mn, mx);
        }
    }

    // ---------------- 表示 ----------------

    /// <summary>測量の進み具合どおりに球を塗り直す（シアン=未測 / 黄=いま触る点 / 緑=測定済み）。
    /// 編集モードが球を借りて別の色に塗るので、戻ってきたときにここで復帰させる。</summary>
    public void RepaintSurveyColors()
    {
        if (!_gizmos.Built || _markers == null) return;
        for (int i = 0; i < _markers.Length; i++)
        {
            bool done = _got != null && i < _got.Length && _got[i];
            _gizmos.SetColor(i, done ? ColDone : (i == _idx ? ColCurrent : ColPending));
        }
    }

    private void LateUpdate()
    {
        if (!_gizmos.Built || !GizmosActive) return;
        // 起動直後はリグがまだ出来ていないことがある。取れるようになったら拾う。
        if (_head == null && Camera.main != null) _head = Camera.main.transform;
        CpGizmos.Draw(() => _gizmos.Tick(_head));
    }

    private void OnDestroy() => _gizmos?.DestroyAll();

    private IEnumerator Haptic()
    {
        OVRInput.SetControllerVibration(0.3f, 0.6f, OVRInput.Controller.RTouch);
        yield return new WaitForSeconds(0.06f);
        OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.RTouch);
    }

    /// <summary>受け付けられなかったことを触覚で返す（長め・弱め＝拒否の合図）。
    /// 画面を見ていなくても「記録されていない」と分かるようにする。</summary>
    private IEnumerator RejectHaptic()
    {
        for (int i = 0; i < 2; i++)
        {
            OVRInput.SetControllerVibration(0.1f, 0.25f, OVRInput.Controller.RTouch);
            yield return new WaitForSeconds(0.09f);
            OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.RTouch);
            yield return new WaitForSeconds(0.06f);
        }
    }

    private static string F(float v, int digits) => v.ToString("F" + digits, Inv);
}
