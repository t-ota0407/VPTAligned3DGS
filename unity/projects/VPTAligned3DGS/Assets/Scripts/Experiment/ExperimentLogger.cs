using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// 実験ロガー（評価 E1/E2/E3/E5 用）。全イベントを JSONL で実機ストレージに記録する。
///
/// 既存コードへの変更ゼロで動く設計:
///   - UDPManager.OnSim3Result / AlignmentManager.OnLocked を購読
///   - KeyframeUploader.SentCount / AlignmentManager.LastAutoReason はポーリングで変化検知
/// 出力先は 2 か所（**どちらか一方が失われても解析できるように**）:
///   1. 端末内 JSONL … Application.persistentDataPath/experiments/exp_(timestamp).jsonl
///      取得: adb pull /sdcard/Android/data/(パッケージ名)/files/experiments/
///   2. **PC へ UDP 送信** … 解析に必要な行だけを [[UDPManager.SendEvent]] で送り、
///      PC 側 run.py が received/quest_events.jsonl に貯める。
///
/// なぜ 2 系統か: CP の実測値は実験を通して 6 点しかなく（測量は実験前に 1 回だけ）、
/// **後から測り直すと座標系が変わってしまうのでやり直しが効かない**。
/// 端末内にしか残らないと、実験のたびにケーブルを繋いで引き抜く必要があり、
/// 引き忘れると 10 試行ぶんのデータが端末の中に取り残される。
/// PC へ送っておけば、実験が終わった時点で解析に必要なものが全部 PC 側に揃う。
/// UDP は取りこぼすので、PONG の ev_ack を見て未達分を再送する（UDPManager 側）。
///
/// 送るのは <see cref="UdpTypes"/> の行だけ。fps や kf_sent まで送ると毎秒何十行にもなり、
/// キーフレームの帯域を食う。
/// </summary>
public class ExperimentLogger : MonoBehaviour
{
    public static ExperimentLogger Instance { get; private set; }

    [Header("References（未設定なら自動取得）")]
    [SerializeField] private AlignmentManager align;
    [SerializeField] private UDPManager udp;
    [SerializeField] private KeyframeUploader uploader;
    [SerializeField] private GaussianSplatController splat;

    [Header("Settings")]
    [Tooltip("FPS 統計のウィンドウ長 [s]（E5）")]
    [SerializeField] private float fpsWindowS = 5f;

    private StreamWriter _w;
    private int    _lastSentCount;
    private string _lastGateReason;

    /// <summary>PC へ送る行の種類。**解析に必要なものだけ**。
    ///
    /// CP 実測値（cp_survey）が本命。試行の対応づけに trial_start/trial_end、
    /// 試行の有効性判定に tracking_origin_changed、先端校正の妥当性に tip_calib_*、
    /// 収束の記録に auto_aligned が要る。fps / kf_sent / sim3_gate は毎秒何十行にもなるので送らない
    /// （端末内 JSONL には従来どおり全部残る）。
    ///
    /// cp_model_* は VR で置き直した model 側 CP（<see cref="ControlPointEdit"/>）。
    /// これは解析用ではなく、**PC 側で controlpoints_&lt;modelId&gt;.json を組み立てて Editor へ
    /// 戻す**ための経路（adb pull を挟まずにシーンへ恒久化できる）。</summary>
    private static readonly System.Collections.Generic.HashSet<string> UdpTypes =
        new System.Collections.Generic.HashSet<string>
        {
            "session_start", "session_end", "scene_info",
            "trial_start", "trial_end",
            "cp_survey", "cp_survey_start", "cp_survey_done",
            "cp_survey_cancel", "cp_survey_invalidated",
            "cp_model_edit", "cp_model_point", "cp_model_edit_done", "cp_model_edit_revert",
            "tracking_origin_changed",
            "tip_calib_done", "tip_calib_reused", "tip_calib_fail",
            "auto_aligned", "locked",
        };

    // このアプリ起動を識別する ID（= 端末内ログのファイル名）。PC 側は複数起動ぶんの
    // イベントを 1 本の JSONL に貯めるため、これが無いと試行番号が衝突して区別できない
    // （trial は起動ごとに 1 から振り直される）。
    private string _run = "";
    // PC へ送った行の通し番号。1 から連番。PC はこれで重複除去と ACK を行う。
    private uint _evSeq;

    // 試行（trial）: 1 回のアプリ起動に複数の独立試行を含められるようにする。
    // 旧実装は「1 起動 = 1 試行」前提だったが、E2 の 10 試行を毎回アプリ再起動で回すのは
    // 現実的でないため、ExperimentController がアプリ内で試行を切り替える（→ 各行に trial を付ける）。
    private int    _trial;
    private string _trialMode = "";
    // FPS 集計
    private int _frames; private float _sumDt; private float _maxDt; private float _winStart;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private void Awake()
    {
        Instance = this;
        if (align    == null) align    = FindFirstObjectByType<AlignmentManager>();
        if (udp      == null) udp      = FindFirstObjectByType<UDPManager>();
        if (uploader == null) uploader = FindFirstObjectByType<KeyframeUploader>();
        if (splat    == null) splat    = FindFirstObjectByType<GaussianSplatController>();

        try
        {
            string dir = Path.Combine(Application.persistentDataPath, "experiments");
            Directory.CreateDirectory(dir);
            _run = "exp_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string path = Path.Combine(dir, _run + ".jsonl");
            _w = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
            Debug.Log("[ExperimentLogger] ログ開始: " + path);
        }
        catch (Exception e)
        {
            Debug.LogError("[ExperimentLogger] ログファイルを開けません: " + e.Message);
            _w = null;
        }
        // 端末内に書けなくても PC 送信は続ける（run が空だと PC 側で起動を区別できないので必ず入れる）
        if (string.IsNullOrEmpty(_run)) _run = "exp_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");

        Write("session_start",
            "\"device\":\"" + Escape(SystemInfo.deviceModel) + "\",\"app\":\"" + Escape(Application.version) + "\"");
    }

    private void Start()
    {
        if (udp   != null) udp.OnSim3Result  += OnSim3;
        if (align != null) align.OnLocked    += OnLockedEvt;
        if (align != null) align.OnAutoAligned += OnAutoAlignedEvt;   // E2 の収束点（手動 Lock と区別）
        _winStart = Time.realtimeSinceStartup;

        // E5: スプラット数（aras-p の GaussianSplatAsset.splatCount をリフレクションで安全に取得）
        int count = -1;
        try
        {
            var rend  = splat != null ? splat.Renderer : null;
            var asset = rend != null ? rend.asset : null;
            if (asset != null)
            {
                var p = asset.GetType().GetProperty("splatCount");
                if (p != null) count = (int)p.GetValue(asset);
            }
        }
        catch { /* 取得できなくても実験は継続 */ }
        Write("scene_info", "\"splatCount\":" + count);
    }

    private void OnDestroy()
    {
        if (udp   != null) udp.OnSim3Result  -= OnSim3;
        if (align != null) align.OnLocked    -= OnLockedEvt;
        if (align != null) align.OnAutoAligned -= OnAutoAlignedEvt;
        Write("session_end", null);
        if (_w != null) { _w.Flush(); _w.Close(); _w = null; }
    }

    private void Update()
    {
        // E2: キーフレーム送信の検知（SentCount のポーリング。既存コード無変更）
        if (uploader != null && uploader.SentCount != _lastSentCount)
        {
            _lastSentCount = uploader.SentCount;
            Write("kf_sent", "\"req\":" + _lastSentCount);
        }

        // Sim(3) ゲートの採否（LastAutoReason の変化検知）
        if (align != null && align.LastAutoReason != _lastGateReason)
        {
            _lastGateReason = align.LastAutoReason;
            if (!string.IsNullOrEmpty(_lastGateReason) && !_lastGateReason.StartsWith("("))
                Write("sim3_gate",
                    "\"accepted\":" + (align.LastAutoAccepted ? "true" : "false") +
                    ",\"reason\":\"" + Escape(_lastGateReason) + "\"");
        }

        // E5: FPS 集計
        float dt = Time.unscaledDeltaTime;
        _frames++; _sumDt += dt; if (dt > _maxDt) _maxDt = dt;
        if (Time.realtimeSinceStartup - _winStart >= fpsWindowS && _frames > 0)
        {
            float avg   = _frames / Mathf.Max(1e-4f, _sumDt);
            float worst = 1f / Mathf.Max(1e-4f, _maxDt);
            Write("fps", "\"avg\":" + F(avg, 1) + ",\"worst_frame\":" + F(worst, 1) + ",\"frames\":" + _frames);
            _frames = 0; _sumDt = 0f; _maxDt = 0f; _winStart = Time.realtimeSinceStartup;
        }
    }

    private void OnSim3(Sim3Result r)
    {
        Write("sim3_received",
            "\"sigma\":" + F(r.sigma, 6) +
            ",\"qx\":" + F(r.rot.x, 6) + ",\"qy\":" + F(r.rot.y, 6) +
            ",\"qz\":" + F(r.rot.z, 6) + ",\"qw\":" + F(r.rot.w, 6) +
            ",\"px\":" + F(r.pos.x, 5) + ",\"py\":" + F(r.pos.y, 5) + ",\"pz\":" + F(r.pos.z, 5) +
            ",\"inliers\":" + r.inliers + ",\"n\":" + r.n + ",\"rmse\":" + F(r.rmse, 5));
    }

    /// <summary>E2 の収束イベント。自動位置合わせが採用・適用された瞬間だけ発火する。
    /// 「そのときまでに送ったキーフレーム数」を同じ行に残すので、事後解析で結合が要らない。</summary>
    private void OnAutoAlignedEvt(Sim3Result r)
    {
        Write("auto_aligned",
            "\"kf_sent\":" + (uploader != null ? uploader.SentCount : -1) +
            ",\"sigma\":" + F(r.sigma, 6) +
            ",\"inliers\":" + r.inliers + ",\"n\":" + r.n + ",\"rmse\":" + F(r.rmse, 5));
    }

    private void OnLockedEvt(Vector3 pos, Quaternion rot, Vector3 scale)
    {
        // E2 の収束点 ＆ E3 の再現性解析に使う「適用済み姿勢」
        Write("locked",
            "\"px\":" + F(pos.x, 5) + ",\"py\":" + F(pos.y, 5) + ",\"pz\":" + F(pos.z, 5) +
            ",\"qx\":" + F(rot.x, 6) + ",\"qy\":" + F(rot.y, 6) +
            ",\"qz\":" + F(rot.z, 6) + ",\"qw\":" + F(rot.w, 6) +
            ",\"scale\":" + F(scale.x, 6));
    }

    /// <summary>他コンポーネント（ControlPointMeasure 等）からの記録用。dataJson は {} の中身。</summary>
    public static void LogStatic(string type, string dataJson)
    {
        if (Instance != null) Instance.Write(type, dataJson);
    }

    // ---------------- 試行（trial）の区切り ----------------

    /// <summary>新しい試行を開始する。以降の全レコードにこの試行番号が付く。</summary>
    /// <param name="mode">試行の種類。現在は "collect"（一定時間データを収集するだけ）の 1 種類。
    /// 以前は E1/E2 で分かれていたが、1 回の収集からオフラインで両方を出せるので統合した。
    /// 将来別条件の試行を混ぜたときに区別できるよう、フィールド自体は残してある。</param>
    /// <param name="label">人間可読ラベル（解析時の突き合わせ用。PC の trial.json と揃える）。</param>
    public int BeginTrial(string mode, string label, string extraJson = null)
    {
        _trial++;
        _trialMode = mode ?? "";
        string data = "\"mode\":\"" + Escape(mode) + "\",\"label\":\"" + Escape(label) + "\"";
        if (!string.IsNullOrEmpty(extraJson)) data += "," + extraJson;
        Write("trial_start", data);
        Debug.Log($"[ExperimentLogger] === 試行 {_trial} 開始 ({mode} / {label}) ===");
        return _trial;
    }

    /// <summary>試行を終了する。verdict は "success" / "timeout" / "aborted" 等。</summary>
    public void EndTrial(string verdict, string extraJson = null)
    {
        if (_trial <= 0) return;
        string data = "\"verdict\":\"" + Escape(verdict) + "\"";
        if (!string.IsNullOrEmpty(extraJson)) data += "," + extraJson;
        Write("trial_end", data);
        Debug.Log($"[ExperimentLogger] === 試行 {_trial} 終了: {verdict} ===");
    }

    private void Write(string type, string dataJson)
    {
        // PC へ送る行には通し番号を振る。**端末内 JSONL と PC 側 JSONL で同じ行になる**ので、
        // どちらを読んでも、また両方を混ぜても、(run, seq) で重複を除いて突き合わせられる。
        bool upload = UdpTypes.Contains(type) && udp != null;
        uint seq = upload ? ++_evSeq : 0;

        var sb = new StringBuilder(320);
        sb.Append("{\"run\":\"").Append(Escape(_run)).Append('"');
        if (upload) sb.Append(",\"seq\":").Append(seq);
        sb.Append(",\"t\":").Append(Time.realtimeSinceStartup.ToString("F3", Inv))
          .Append(",\"wall\":\"").Append(DateTime.Now.ToString("O"))
          .Append("\",\"trial\":").Append(_trial)
          .Append(",\"mode\":\"").Append(Escape(_trialMode))
          .Append("\",\"type\":\"").Append(type).Append('"');
        if (!string.IsNullOrEmpty(dataJson)) sb.Append(',').Append(dataJson);
        sb.Append('}');
        string line = sb.ToString();

        if (_w != null) _w.WriteLine(line);
        if (upload) udp.SendEvent(seq, line);
    }

    private static string F(float v, int digits) => v.ToString("F" + digits, Inv);
    private static string Escape(string s) =>
        (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
}
