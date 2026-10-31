using System;
using UnityEngine;

/// <summary>
/// Quest のトラッキング原点（ワールド座標系そのもの）が動いたことを検出する。
///
/// **なぜ必要か。**
/// 自動位置合わせは「COLMAP のカメラ中心」と「Quest ワールドのカメラ中心」の対応から Sim(3) を解く。
/// この Quest ワールドは *固定されている前提* だが、実際には recenter・トラッキングロスからの復帰・
/// 空間の再ローカライズで **ヨー回転＋水平移動** の形で飛ぶ。原点が飛ぶと:
///   - 収集中に起きた場合 … キーフレームの姿勢が途中から別座標系になり、Sim(3) の対応が壊れる。
///   - 適用後に起きた場合 … splatRoot はワールド座標に置かれているので実世界に対してずれる。
///     実験者には「位置合わせが失敗した」ようにしか見えない。
///
/// 実機で 3 試行を突き合わせたところ、試行間の解の差が **スケール 1.000・回転軸 (0,1,0)・
/// 鉛直移動 0** の相似変換になっていた。これは推定誤差では出ない形（誤差は最も弱く拘束される
/// 傾き方向に出る。ヨーは逆に最もよく決まる）で、原点そのものが回ったことを意味する。
/// 検出できないと、この種の試行が「位置合わせの失敗」として混入し、評価が無意味になる。
///
/// **検出の三重化**（どれか 1 つでも拾えれば良い。取りこぼすほうが害が大きい）:
///   1. `OVRPlugin.GetLocalTrackingSpaceRecenterCount()` … 明示的な recenter。最も確実。
///   2. `OVRManager.TrackingLost` / `TrackingAcquired` … 追跡の喪失と復帰。
///   3. 頭部姿勢の不連続 … 上 2 つが鳴らない「静かな」再ローカライズを拾う。
///      頭は瞬間移動しないので、**速度**が人体の限界を超えたら座標系が動いたと判断できる。
///      フレーム落ち（dt が伸びる）で誤検出しないよう、距離ではなく速度で見る。
///
/// 実験（ExperimentController: 試行の破棄・測量の無効化）とデモ（DemoController: 収集のやり直し・
/// 表示中の再確認）の両方が使うので、Experiment/ ではなく Scripts/ 直下に置く。
/// </summary>
public class TrackingOriginWatcher : MonoBehaviour
{
    public enum Cause { None, Recenter, TrackingLost, Jump }

    [Header("References (auto-find if empty)")]
    [Tooltip("頭（CenterEyeAnchor）。未設定なら OVRCameraRig / Camera.main から取得。")]
    [SerializeField] private Transform head;

    [Header("Jump detection")]
    [Tooltip("この距離[m]未満の変化は無視（dt が極小のときに速度が暴れるのを防ぐ）。")]
    [SerializeField] private float minJumpM = 0.05f;
    [Tooltip("頭の並進速度の上限[m/s]。全力疾走でも 3 m/s 程度なので、超えたら座標系が動いた。")]
    [SerializeField] private float maxSpeedMps = 4.0f;
    [Tooltip("この角度[deg]未満の変化は無視。")]
    [SerializeField] private float minJumpDeg = 10f;
    [Tooltip("頭の角速度の上限[deg/s]。人が首を振る最速でも 600 deg/s 程度。")]
    [SerializeField] private float maxAngSpeedDps = 800f;
    [Tooltip("起動直後この秒数は判定しない（トラッキング確立前の姿勢は当てにならない）。")]
    [SerializeField] private float warmupS = 3f;
    [Tooltip("1 度検出したらこの秒数は再検出しない（1 つの事象を多重に数えない）。")]
    [SerializeField] private float refractoryS = 1.0f;

    /// <summary>原点が動いたときに発火する（原因・並進量[m]・回転量[deg]）。</summary>
    public event Action<Cause, float, float> OnOriginChanged;

    /// <summary>起動してからの検出回数。</summary>
    public int EventCount { get; private set; }
    /// <summary>直近の <see cref="Mark"/> からの検出回数。ExperimentController が試行ごとに使う。</summary>
    public int EventsSinceMark { get; private set; }
    public Cause LastCause { get; private set; } = Cause.None;
    public float LastJumpM { get; private set; }
    public float LastJumpDeg { get; private set; }
    /// <summary>直近の検出時刻（realtimeSinceStartup）。未検出なら負。</summary>
    public float LastEventTime { get; private set; } = -1f;
    /// <summary>直近の検出から経過した秒数（未検出なら大きな値）。</summary>
    public float SinceLastEventS =>
        LastEventTime < 0f ? float.MaxValue : Time.realtimeSinceStartup - LastEventTime;

    /// <summary>人が読む 1 行説明（HUD 用）。</summary>
    public string LastDescription =>
        LastCause == Cause.None ? "" :
        LastCause == Cause.Recenter     ? "view was recentered" :
        LastCause == Cause.TrackingLost ? "tracking was lost" :
        $"tracking jumped {LastJumpM * 100f:F0} cm / {LastJumpDeg:F0}°";

    private bool _hasPrev;
    private Vector3 _prevPos;
    private Quaternion _prevRot = Quaternion.identity;
    private int _recenterCount = int.MinValue;
    private float _startT;
    private bool _trackingLostPending;

    private void Awake()
    {
        if (head == null)
        {
            var rig = FindFirstObjectByType<OVRCameraRig>();
            if (rig != null) head = rig.centerEyeAnchor;
            if (head == null && Camera.main != null) head = Camera.main.transform;
        }
        if (head == null)
            Debug.LogWarning("[TrackingOriginWatcher] 頭の Transform が見つからず、ジャンプ検出は無効です。");
    }

    private void OnEnable()
    {
        _startT = Time.realtimeSinceStartup;
        OVRManager.TrackingLost += OnTrackingLost;
    }

    private void OnDisable()
    {
        OVRManager.TrackingLost -= OnTrackingLost;
    }

    /// <summary>ここからの検出回数を数え直す（試行の開始時に呼ぶ）。</summary>
    public void Mark()
    {
        EventsSinceMark = 0;
        // 直後の 1 フレームは姿勢差分の基準が古いままなので、取り直させる。
        _hasPrev = false;
    }

    private void OnTrackingLost()
    {
        // イベントはメインスレッド外から来ることは無いが、Update 側の抑制と足並みを揃えるため
        // フラグにしてから Update で 1 本化する。
        _trackingLostPending = true;
    }

    private void Update()
    {
        // 1) 明示的な recenter（最も確実な信号）
        int rc = OVRPlugin.GetLocalTrackingSpaceRecenterCount();
        if (_recenterCount == int.MinValue) _recenterCount = rc;
        else if (rc != _recenterCount) { _recenterCount = rc; Report(Cause.Recenter, 0f, 0f); }

        // 2) トラッキングの喪失（復帰時に原点が動くことがある）
        if (_trackingLostPending) { _trackingLostPending = false; Report(Cause.TrackingLost, 0f, 0f); }

        // 3) 頭部姿勢の不連続
        if (head == null) return;
        Vector3 pos = head.position;
        Quaternion rot = head.rotation;
        float dt = Time.unscaledDeltaTime;

        if (_hasPrev && Time.realtimeSinceStartup - _startT >= warmupS && dt > 1e-4f)
        {
            float dM = Vector3.Distance(pos, _prevPos);
            float dDeg = Quaternion.Angle(rot, _prevRot);
            // 距離ではなく速度で判定する。フレーム落ちで dt が伸びた回は距離だけ見ると誤検出する。
            bool jumped = (dM >= minJumpM && dM / dt > maxSpeedMps)
                       || (dDeg >= minJumpDeg && dDeg / dt > maxAngSpeedDps);
            if (jumped) Report(Cause.Jump, dM, dDeg);
        }

        _prevPos = pos; _prevRot = rot; _hasPrev = true;
    }

    private void Report(Cause cause, float m, float deg)
    {
        // 1 つの事象で recenter とジャンプが同時に立つので、不応期でまとめる。
        if (LastEventTime >= 0f && Time.realtimeSinceStartup - LastEventTime < refractoryS) return;

        LastCause = cause; LastJumpM = m; LastJumpDeg = deg;
        LastEventTime = Time.realtimeSinceStartup;
        EventCount++; EventsSinceMark++;

        Debug.LogWarning($"[TrackingOriginWatcher] トラッキング原点が動きました: {cause} " +
                         $"({m * 100f:F0} cm / {deg:F0} deg) — 進行中の位置合わせは無効です");
        ExperimentLogger.LogStatic("tracking_origin_changed",
            "\"cause\":\"" + cause + "\",\"jump_m\":" + m.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"jump_deg\":" + deg.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
        OnOriginChanged?.Invoke(cause, m, deg);
    }
}
