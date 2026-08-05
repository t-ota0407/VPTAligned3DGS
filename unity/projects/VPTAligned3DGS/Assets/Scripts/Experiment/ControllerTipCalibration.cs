using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

/// <summary>
/// コントローラ先端オフセットのピボット校正（評価 E1 の前提）。
///
/// なぜ必要か:
///   E1 は「3DGS 上のコントロールポイント」と「実物の同じ点に当てたコントローラ先端」の距離を測る。
///   ところが Unity が返す controllerAnchor の位置は**コントローラ内部の追跡原点**であって、
///   実際に物へ当てる物理的な先端ではない。両者は数 cm 離れているため、補正しないとその差が
///   まるごと系統誤差として計測値に乗る。測ろうとしているオーバーレイ誤差と同じオーダーなので、
///   補正しないと結果が意味をなさない。
///
/// 原理（pivot calibration）:
///   先端を実空間の 1 点に固定したままコントローラの向きだけを変えると、各サンプル i について
///       p_i + R_i · o = c   （p_i,R_i = 追跡姿勢, o = 求めたい先端オフセット(ローカル), c = 固定点(ワールド)）
///   が成り立つ。未知数は o と c の 6 個。各サンプルが 3 本の式を与えるので、
///       [ R_i | -I ] · [o; c] = -p_i
///   を全サンプルで積み上げて最小二乗で解けばよい（閉形式・反復なし）。
///   向きの多様性が足りないと正規方程式が悪条件になるため、回転の広がりを監視して警告する。
///   残差 RMS はそのまま校正精度の指標になるので、論文に「先端校正残差 X mm」と書ける。
///
/// 使い方（実機）— 物理的にやることは「すりこぎを回す」動き:
///   1. 動かない 1 点を選ぶ（机の角・ネジの頭・棚の縁の角など、毎回同じ場所に当てられるもの）。
///   2. その点にコントローラの**物理的な先端を押し当てる**。
///   3. HUD の「先端校正」を押す（または StartCalibration()）。
///   4. **先端をその点から離さないまま、本体だけを大きく振る**。前後左右に倒す、
///      グリップを回す、当てる角度を変える。先端は動かず、本体の向きだけが変わる状態。
///   5. 条件（サンプル数・向きの広がり）が揃うと自動で解き、結果を保存する。
///
/// よくある失敗:
///   - 先端が浮く／ずれる    → 「先端が動かない」という前提が崩れ、残差が大きくなる
///   - 振りが小さい（数度）  → 式が似たものばかりになり悪条件。残差は小さく見えるのに解はずれる
///   - 向きを変えず平行移動  → 回転が無いと情報が増えない
///
/// 手動上書き: manualOffsetM に非ゼロを入れると校正より優先する（逃げ道）。
/// </summary>
public class ControllerTipCalibration : MonoBehaviour
{
    private const string PrefKey = "VPTAlign_TipOffset";

    /// <summary>校正値の出どころ。**「一度校正した」と「今このセッションで有効」は別物**なので区別する。
    ///
    /// 保存値が今の物理構成に対応している保証はない（別のコントローラを握っている／治具を
    /// 付け外しした／前回の校正がゲートぎりぎりで通っていた、等はアプリからは判別できない）。
    /// ずれていても E1 の全測定に系統誤差として乗り、事後のログからは検出できない。
    /// よって既定では **セッションごとの校正を要求**し、前回値は参照としてのみ保持する。</summary>
    public enum TipState
    {
        None,        // 校正値なし
        Restored,    // 前回セッションの保存値がある（そのままでは計測に使わない）
        Calibrated,  // このセッションで校正した
        Manual,      // Inspector で手動指定（責任は指定した人にある）
    }

    [Header("References（未設定なら自動取得）")]
    [Tooltip("校正対象のコントローラ。E1 は右手で計測するので既定は右。")]
    [SerializeField] private Transform controllerAnchor;
    [SerializeField] private bool useRightController = true;

    [Header("Manual override")]
    [Tooltip("非ゼロならこの値を先端オフセットとして使う（校正結果より優先）。単位は m・コントローラのローカル座標。")]
    [SerializeField] private Vector3 manualOffsetM = Vector3.zero;

    [Header("Policy")]
    [Tooltip("セッションごとに校正を要求する（既定 ON・推奨）。\n" +
             "OFF にすると前回の保存値をそのまま計測に使う。保存値が今の物理構成に対応している\n" +
             "保証はなく、ずれていても E1 の全測定に系統誤差として乗るため、通常は ON のままにする。\n" +
             "前回値は OFF でなくても参照として保持され、校正完了時に差分を報告する。")]
    [SerializeField] private bool requireCalibrationEachSession = true;
    [Tooltip("前回値との差がこの[mm]を超えたら警告する（治具の付け外しや別コントローラの検出）。")]
    [SerializeField] private float driftWarnMm = 5f;

    [Header("Sampling")]
    [Tooltip("直前のサンプルからこの角度[deg]以上向きが変わったら 1 サンプル採る（同じ姿勢の重複を避ける）。")]
    [SerializeField] private float minSampleAngleDeg = 6f;
    [Tooltip("解くのに必要な最小サンプル数。")]
    [SerializeField] private int minSamples = 24;
    [Tooltip("サンプル数の上限（これに達したら解く）。")]
    [SerializeField] private int maxSamples = 120;
    [Tooltip("向きの広がり（最大ペア角）がこの[deg]未満だと悪条件。警告して結果を採用しない。")]
    [SerializeField] private float minSpreadDeg = 45f;
    [Tooltip("この秒数で打ち切って、集まった分だけで解く。")]
    [SerializeField] private float maxDurationS = 20f;

    [Header("Result (read-only)")]
    [SerializeField] private Vector3 calibratedOffsetM;
    [SerializeField] private float   residualMm = -1f;
    [SerializeField] private TipState state = TipState.None;
    // 前回セッションの保存値（参照用。校正完了時に差分を出して整合性チェックにする）
    private Vector3  _storedOffsetM;
    private float    _storedResidualMm = -1f;
    private DateTime _storedUtc;
    private bool     _hasStored;

    private readonly List<Vector3>    _pos = new List<Vector3>();
    private readonly List<Quaternion> _rot = new List<Quaternion>();
    private bool  _running;
    private float _startT;
    private float _spreadCacheDeg;    // 向きの広がり（サンプル追加時に増分更新）
    private float _liveResidualMm = -1f;   // 校正中の暫定残差（先端が滑ったらここに出る）

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>計測に使う先端オフセット（コントローラのローカル座標・m）。</summary>
    public Vector3 TipOffsetM =>
        state == TipState.Manual ? manualOffsetM
        : (state == TipState.None ? Vector3.zero : calibratedOffsetM);

    /// <summary>校正値の出どころ。</summary>
    public TipState State => state;

    /// <summary>**この構成で計測してよいか。** HUD の ✓ とゲートはこれを見る。
    /// 前回セッションの保存値（Restored）は、既定では ✓ にしない
    /// — 今の物理構成に対応している保証がないため（クラス冒頭の説明を参照）。</summary>
    public bool IsCalibrated =>
        state == TipState.Calibrated || state == TipState.Manual ||
        (state == TipState.Restored && !requireCalibrationEachSession);

    /// <summary>前回セッションの値が残っているが、まだこのセッションで校正していない状態。
    /// HUD はこれを「✗」ではなく「▲ 前回値」として区別して見せる。</summary>
    public bool HasStaleCalibration => state == TipState.Restored && requireCalibrationEachSession;

    /// <summary>保存値の古さ（時間）。不明なら負。</summary>
    public float StoredAgeHours =>
        _hasStored ? (float)(DateTime.UtcNow - _storedUtc).TotalHours : -1f;

    /// <summary>校正残差 RMS[mm]（未校正なら負）。校正の良し悪しの指標。</summary>
    public float ResidualMm => residualMm;

    public bool IsRunning => _running;
    /// <summary>校正中の進捗 0..1（サンプル数と経過時間のうち進んでいる方）。HUD のバー用。</summary>
    public float Progress => !_running ? 0f : Mathf.Clamp01(Mathf.Max(
        _pos.Count / (float)Mathf.Max(1, minSamples),
        (Time.realtimeSinceStartup - _startT) / Mathf.Max(0.1f, maxDurationS)));
    /// <summary>校正中に集まったサンプル数。</summary>
    public int SampleCount => _pos.Count;
    /// <summary>校正中の向きの広がり[deg]。45° 程度まで回さないと解が悪条件になる。
    /// 全ペア計算は O(n^2) なので、サンプル追加時に増分更新した値を返す（毎フレーム再計算しない）。</summary>
    public float CurrentSpreadDeg => _spreadCacheDeg;
    /// <summary>必要な広がり[deg]（HUD 表示用）。</summary>
    public float RequiredSpreadDeg => minSpreadDeg;
    /// <summary>必要なサンプル数（HUD 表示用）。</summary>
    public int RequiredSamples => minSamples;

    /// <summary>校正中の暫定残差[mm]（まだ解けていなければ負）。先端が滑ると跳ね上がる。</summary>
    public float LiveResidualMm => _liveResidualMm;

    /// <summary>今この瞬間に何をすべきか（HUD の見出し）。
    /// 「先端を固定したまま回してください」だけでは何をするか伝わらなかったので、
    /// 進行に応じて具体的な指示を出す。</summary>
    public string GuidanceTitle
    {
        get
        {
            if (!_running) return "Tip calibration";
            if (_pos.Count == 0)                return "Press the tip on a fixed point";
            if (_spreadCacheDeg < minSpreadDeg) return "Now swing the controller widely";
            if (_pos.Count < minSamples)        return "Keep going";
            return "Ready to finish";
        }
    }

    /// <summary>具体的な動作の説明（HUD の副見出し）。</summary>
    public string GuidanceSub
    {
        get
        {
            if (!_running) return "";
            if (_pos.Count == 0)
                return "Use a desk corner or similar. Keep the tip on it the whole time.";
            if (_spreadCacheDeg < minSpreadDeg)
                return "Tilt it far in every direction, like grinding with a pestle";
            if (_pos.Count < minSamples)
                return "Same motion, tip still on the point";
            // ボタンの割当は書かない。HUD が自前の配色で添える（AlignmentHud.Btn）ので、
            // ここに書くと同じ内容が二重に出るうえ、割当を変えたときに直し忘れる。
            return "Finish when ready, or keep going for a better fit";
        }
    }
    /// <summary>先端オフセットの大きさ[m]（HUD 表示用）。</summary>
    public float OffsetMagnitudeM => TipOffsetM.magnitude;

    /// <summary>HUD 表示用の 1 行サマリ。</summary>
    public string StatusLine
    {
        get
        {
            if (_running) return $"Calibrating {_pos.Count}/{minSamples}";
            switch (state)
            {
                case TipState.Manual:     return $"Tip: manual {Fmt(manualOffsetM)}";
                case TipState.Calibrated: return $"Tip: calibrated, residual {residualMm:F1} mm";
                case TipState.Restored:
                    string age = StoredAgeHours < 1f ? "under 1 h"
                               : StoredAgeHours < 48f ? $"{StoredAgeHours:F0} h ago"
                               : $"{StoredAgeHours / 24f:F0} d ago";
                    return requireCalibrationEachSession
                        ? $"Tip: previous session ({age}) — recalibrate"
                        : $"Tip: reusing previous value ({age})";
                default:                  return "Tip: not calibrated";
            }
        }
    }

    private void Awake()
    {
        if (controllerAnchor == null)
        {
            var rig = FindFirstObjectByType<OVRCameraRig>();
            if (rig != null)
                controllerAnchor = useRightController ? rig.rightControllerAnchor : rig.leftControllerAnchor;
        }
        LoadSaved();
    }

    private void Update()
    {
        if (!_running || controllerAnchor == null) return;

        Quaternion q = controllerAnchor.rotation;
        // 前のサンプルから十分に向きが変わったときだけ採る（静止中に同じ姿勢を溜め込まない）
        if (_rot.Count == 0 || Quaternion.Angle(_rot[_rot.Count - 1], q) >= minSampleAngleDeg)
        {
            // 新サンプルと既存すべての角度差を見て、広がりの最大値を更新（O(n) / サンプル）
            for (int i = 0; i < _rot.Count; i++)
            {
                float a = Quaternion.Angle(_rot[i], q);
                if (a > _spreadCacheDeg) _spreadCacheDeg = a;
            }
            _pos.Add(controllerAnchor.position);
            _rot.Add(q);
            OVRInput.SetControllerVibration(0.2f, 0.2f,
                useRightController ? OVRInput.Controller.RTouch : OVRInput.Controller.LTouch);

            // 暫定解を出しておく。先端が点から滑ると残差がすぐ跳ね上がるので、
            // 「10 秒回してから失敗と分かる」のではなく、その場で気づいて当て直せる。
            if (_pos.Count >= 8 && _pos.Count % 4 == 0 &&
                Solve(_pos, _rot, out _, out _, out float rms))
                _liveResidualMm = rms * 1000f;
        }
        else
        {
            OVRInput.SetControllerVibration(0f, 0f,
                useRightController ? OVRInput.Controller.RTouch : OVRInput.Controller.LTouch);
        }

        if (_pos.Count >= maxSamples || Time.realtimeSinceStartup - _startT >= maxDurationS)
            FinishCalibration();
    }

    // ---------------- 制御 ----------------

    /// <summary>HUD ボタン用: 未実行なら開始、実行中なら今あるサンプルで打ち切って解く。</summary>
    public void ToggleCalibration()
    {
        if (_running) FinishCalibration();
        else          StartCalibration();
    }

    public void StartCalibration()
    {
        if (controllerAnchor == null)
        {
            Debug.LogError("[ControllerTipCalibration] controllerAnchor が取得できません（OVRCameraRig 無し）");
            return;
        }
        _pos.Clear(); _rot.Clear();
        _spreadCacheDeg = 0f;
        _liveResidualMm = -1f;
        _running = true;
        _startT = Time.realtimeSinceStartup;
        Debug.Log("[ControllerTipCalibration] 校正開始: 机の角などに先端を押し当て、" +
                  "先端を離さないまま本体を大きく振ってください（すりこぎを回す動き）");
        ExperimentLogger.LogStatic("tip_calib_start", null);
    }

    /// <summary>校正を打ち切って、集まったサンプルで解く。</summary>
    public void FinishCalibration()
    {
        _running = false;
        OVRInput.SetControllerVibration(0f, 0f,
            useRightController ? OVRInput.Controller.RTouch : OVRInput.Controller.LTouch);

        int n = _pos.Count;
        if (n < minSamples)
        {
            Debug.LogWarning($"[ControllerTipCalibration] サンプル不足 {n}/{minSamples} → 破棄。もう一度、先端を固定して大きく回してください");
            ExperimentLogger.LogStatic("tip_calib_fail", "\"reason\":\"samples\",\"n\":" + n);
            return;
        }

        float spread = MaxPairwiseAngle(_rot);
        if (spread < minSpreadDeg)
        {
            Debug.LogWarning($"[ControllerTipCalibration] 向きの広がりが不足 {spread:F0}°(<{minSpreadDeg}°) → 悪条件のため破棄。" +
                             "先端は固定したまま、もっと大きく傾けて回してください");
            ExperimentLogger.LogStatic("tip_calib_fail",
                "\"reason\":\"spread\",\"n\":" + n + ",\"spread_deg\":" + spread.ToString("F1", Inv));
            return;
        }

        if (!Solve(_pos, _rot, out Vector3 o, out Vector3 c, out float rmsM))
        {
            Debug.LogWarning("[ControllerTipCalibration] 正規方程式が解けません（悪条件）→ 破棄");
            ExperimentLogger.LogStatic("tip_calib_fail", "\"reason\":\"singular\",\"n\":" + n);
            return;
        }

        // 前回値との差。治具の付け外しや別コントローラの取り違えは、ここに現れる。
        // 毎セッション校正する運用にしておくと、この差分がタダで整合性チェックになる。
        float driftMm = _hasStored ? (o - _storedOffsetM).magnitude * 1000f : -1f;

        calibratedOffsetM = o;
        residualMm = rmsM * 1000f;
        state = TipState.Calibrated;
        Save();

        Debug.Log($"[ControllerTipCalibration] 校正完了: offset={Fmt(o)} |offset|={o.magnitude * 100f:F1}cm " +
                  $"残差RMS={residualMm:F1}mm samples={n} spread={spread:F0}°" +
                  (driftMm >= 0f ? $" 前回値との差={driftMm:F1}mm" : " （前回値なし）"));
        if (driftMm > driftWarnMm)
            Debug.LogWarning($"[ControllerTipCalibration] 前回の校正値と {driftMm:F1}mm 違います（閾値 {driftWarnMm}mm）。" +
                             "別のコントローラを持っている／治具を付け外しした／どちらかの校正が不良、" +
                             "のいずれかを疑ってください。");

        ExperimentLogger.LogStatic("tip_calib_done",
            "\"ox\":" + o.x.ToString("F5", Inv) + ",\"oy\":" + o.y.ToString("F5", Inv) +
            ",\"oz\":" + o.z.ToString("F5", Inv) +
            ",\"residual_mm\":" + residualMm.ToString("F2", Inv) +
            ",\"n\":" + n + ",\"spread_deg\":" + spread.ToString("F1", Inv) +
            ",\"drift_mm\":" + driftMm.ToString("F2", Inv) +
            ",\"pivot\":[" + c.x.ToString("F4", Inv) + "," + c.y.ToString("F4", Inv) + "," + c.z.ToString("F4", Inv) + "]");

        // 次回の差分比較の基準を更新する
        _storedOffsetM = o; _storedResidualMm = residualMm; _storedUtc = DateTime.UtcNow; _hasStored = true;
    }

    /// <summary>前回セッションの保存値を、このセッションでも使うと明示的に宣言する（逃げ道）。
    /// 校正し直す時間が無い場合の緊急手段。ログに残るので、解析側でこの測定を区別できる。</summary>
    public void UseStoredCalibration()
    {
        if (!_hasStored) { Debug.LogWarning("[ControllerTipCalibration] 保存値がありません"); return; }
        state = TipState.Calibrated;      // 以降このセッションでは校正済み扱い
        Debug.LogWarning($"[ControllerTipCalibration] 前回値をこのセッションで使用します " +
                         $"({Fmt(calibratedOffsetM)} 残差 {residualMm:F1}mm / {StoredAgeHours:F0}時間前)。" +
                         "物理構成が前回と同じであることを確認してください。");
        ExperimentLogger.LogStatic("tip_calib_reused",
            "\"residual_mm\":" + residualMm.ToString("F2", Inv) +
            ",\"age_h\":" + StoredAgeHours.ToString("F1", Inv));
    }

    public void ClearCalibration()
    {
        state = manualOffsetM.sqrMagnitude > 1e-9f ? TipState.Manual : TipState.None;
        calibratedOffsetM = Vector3.zero; residualMm = -1f;
        _hasStored = false; _storedResidualMm = -1f;
        PlayerPrefs.DeleteKey(PrefKey); PlayerPrefs.Save();
        Debug.Log("[ControllerTipCalibration] 校正結果を消去しました");
    }

    // ---------------- 数値計算（純粋関数・テスト可能） ----------------

    /// <summary>ピボット校正の閉形式解。samples は追跡姿勢 (p_i, R_i)。
    /// [R_i | -I]·[o; c] = -p_i を最小二乗で解き、o（ローカル先端）・c（固定点）・残差RMS[m] を返す。
    /// 悪条件（向きの多様性不足）なら false。</summary>
    public static bool Solve(IList<Vector3> pos, IList<Quaternion> rot,
                             out Vector3 offset, out Vector3 pivot, out float rmsM)
    {
        offset = Vector3.zero; pivot = Vector3.zero; rmsM = -1f;
        int n = pos.Count;
        if (n < 3 || rot.Count != n) return false;

        // 正規方程式 ATA(6x6)・ATb(6) を素直に積み上げる（n は高々数百なので速度は問題にならない）
        var ATA = new double[6, 6];
        var ATb = new double[6];
        var A = new double[3, 6];

        for (int i = 0; i < n; i++)
        {
            Matrix4x4 R = Matrix4x4.Rotate(rot[i]);
            for (int r = 0; r < 3; r++)
            {
                A[r, 0] = R[r, 0]; A[r, 1] = R[r, 1]; A[r, 2] = R[r, 2];
                A[r, 3] = (r == 0) ? -1.0 : 0.0;
                A[r, 4] = (r == 1) ? -1.0 : 0.0;
                A[r, 5] = (r == 2) ? -1.0 : 0.0;
            }
            double[] b = { -pos[i].x, -pos[i].y, -pos[i].z };
            for (int r = 0; r < 3; r++)
                for (int j = 0; j < 6; j++)
                {
                    ATb[j] += A[r, j] * b[r];
                    for (int k = 0; k < 6; k++) ATA[j, k] += A[r, j] * A[r, k];
                }
        }

        if (!SolveLinear6(ATA, ATb, out double[] x)) return false;

        offset = new Vector3((float)x[0], (float)x[1], (float)x[2]);
        pivot  = new Vector3((float)x[3], (float)x[4], (float)x[5]);

        double sum = 0.0;
        for (int i = 0; i < n; i++)
        {
            Vector3 predicted = pos[i] + rot[i] * offset;   // ワールド先端
            sum += (predicted - pivot).sqrMagnitude;
        }
        rmsM = Mathf.Sqrt((float)(sum / n));
        return true;
    }

    /// <summary>6x6 の連立一次方程式を部分ピボット選択つき Gauss 消去で解く。悪条件なら false。</summary>
    private static bool SolveLinear6(double[,] M, double[] rhs, out double[] x)
    {
        const int N = 6;
        var a = new double[N, N + 1];
        double maxAbs = 0.0;
        for (int i = 0; i < N; i++)
        {
            for (int j = 0; j < N; j++) { a[i, j] = M[i, j]; maxAbs = System.Math.Max(maxAbs, System.Math.Abs(M[i, j])); }
            a[i, N] = rhs[i];
        }
        x = new double[N];
        // ピボットがスケールに対して極端に小さければ「解なし（悪条件）」と判定する。
        // 向きの多様性が足りない校正はここで弾かれる。
        double tol = System.Math.Max(1e-12, maxAbs * 1e-10);

        for (int col = 0; col < N; col++)
        {
            int piv = col;
            for (int r = col + 1; r < N; r++)
                if (System.Math.Abs(a[r, col]) > System.Math.Abs(a[piv, col])) piv = r;
            if (System.Math.Abs(a[piv, col]) < tol) return false;
            if (piv != col)
                for (int j = col; j <= N; j++) { (a[col, j], a[piv, j]) = (a[piv, j], a[col, j]); }

            for (int r = col + 1; r < N; r++)
            {
                double f = a[r, col] / a[col, col];
                if (f == 0.0) continue;
                for (int j = col; j <= N; j++) a[r, j] -= f * a[col, j];
            }
        }
        for (int i = N - 1; i >= 0; i--)
        {
            double s = a[i, N];
            for (int j = i + 1; j < N; j++) s -= a[i, j] * x[j];
            x[i] = s / a[i, i];
        }
        return true;
    }

    /// <summary>サンプル姿勢の最大ペア角[deg]。校正の条件数の代理指標。</summary>
    public static float MaxPairwiseAngle(IList<Quaternion> rot)
    {
        float best = 0f;
        for (int i = 0; i < rot.Count; i++)
            for (int j = i + 1; j < rot.Count; j++)
            {
                float a = Quaternion.Angle(rot[i], rot[j]);
                if (a > best) best = a;
            }
        return best;
    }

    // ---------------- 永続化 ----------------

    // 保存形式: "x,y,z,residualMm,savedUtcIso"（旧形式 "x,y,z" / "x,y,z,residual" も読める）
    private void Save()
    {
        PlayerPrefs.SetString(PrefKey, string.Join(",",
            calibratedOffsetM.x.ToString("R", Inv),
            calibratedOffsetM.y.ToString("R", Inv),
            calibratedOffsetM.z.ToString("R", Inv),
            residualMm.ToString("R", Inv),
            DateTime.UtcNow.ToString("O")));
        PlayerPrefs.Save();
    }

    private void LoadSaved()
    {
        // 手動指定が最優先。指定されているなら「その人が責任を持つ値」として扱う。
        if (manualOffsetM.sqrMagnitude > 1e-9f)
        {
            state = TipState.Manual;
            Debug.Log($"[ControllerTipCalibration] 手動指定のオフセットを使用: {Fmt(manualOffsetM)}");
            return;
        }

        string s = PlayerPrefs.GetString(PrefKey, "");
        if (string.IsNullOrEmpty(s)) return;
        var t = s.Split(',');
        if (t.Length < 3) return;
        if (!float.TryParse(t[0], System.Globalization.NumberStyles.Float, Inv, out float x) ||
            !float.TryParse(t[1], System.Globalization.NumberStyles.Float, Inv, out float y) ||
            !float.TryParse(t[2], System.Globalization.NumberStyles.Float, Inv, out float z)) return;

        calibratedOffsetM = new Vector3(x, y, z);
        if (t.Length >= 4 && float.TryParse(t[3], System.Globalization.NumberStyles.Float, Inv, out float rm))
            residualMm = rm;
        _storedUtc = DateTime.UtcNow;
        if (t.Length >= 5 && DateTime.TryParse(t[4], Inv, System.Globalization.DateTimeStyles.RoundtripKind,
                                               out DateTime saved))
            _storedUtc = saved;
        _storedOffsetM = calibratedOffsetM; _storedResidualMm = residualMm; _hasStored = true;

        // **前回値は「復元済み」であって「校正済み」ではない。**
        // 今の物理構成に対応している保証がないので、既定ではこのままでは計測に使わせない。
        state = TipState.Restored;
        Debug.Log($"[ControllerTipCalibration] 前回の校正値を参照として読み込み: {Fmt(calibratedOffsetM)} " +
                  $"残差 {residualMm:F1}mm（{StoredAgeHours:F0}時間前）" +
                  (requireCalibrationEachSession
                      ? " → このセッションで校正し直してください（差分を整合性チェックに使います）"
                      : " → requireCalibrationEachSession=false のためそのまま使用します"));
    }

    private static string Fmt(Vector3 v) =>
        $"({v.x * 100f:F1}, {v.y * 100f:F1}, {v.z * 100f:F1})cm";
}
