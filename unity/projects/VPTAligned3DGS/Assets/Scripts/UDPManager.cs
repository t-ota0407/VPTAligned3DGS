using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>PC が解いた Sim(3)（M3 自動位置合わせ結果）＋その品質指標（P1-b: Quest 側ゲート用）。
/// sigma は符号付き一様スケール（負=鏡像反射）。rot/pos は splatRoot にそのまま適用する。</summary>
public struct Sim3Result
{
    public float sigma;
    public Quaternion rot;
    public Vector3 pos;
    public int inliers;   // RANSAC インライア数
    public int n;         // 使用キーフレーム数
    public float rmse;    // world 残差 RMSE [m]
}

/// <summary>
/// UDP 送信マネージャ + PC サーバ自動検出。
///
/// PC サーバ(run.py)が LAN にビーコン(既定 UDP 50001)を流すので、これを受信して
/// 送信先 IP を自動学習し、キーフレームはその IP へ「ユニキャスト」送信する。
/// 学習した IP は PlayerPrefs に保存し、次回起動時はビーコンが来る前から即利用する。
///
/// これにより、PC の IP を毎回入力して再ビルドする必要がなくなる。
/// 手動で固定したい場合は manualIPAddress に IP を入れる（その場合は自動検出より優先）。
/// </summary>
public class UDPManager : MonoBehaviour
{
    [Header("Keyframe send")]
    [Tooltip("キーフレーム送信ポート（サーバの --port と一致）")]
    [SerializeField] private int sendPort = 50000;

    [Header("Auto-discovery")]
    [Tooltip("PC サーバのビーコンを受信して送信先 IP を自動設定する")]
    [SerializeField] private bool autoDiscovery = true;
    [Tooltip("ビーコン受信ポート（サーバの --beacon-port と一致）")]
    [SerializeField] private int discoveryPort = 50001;

    [Header("Manual override (optional)")]
    [Tooltip("ここに IP を入れると自動検出より優先して固定送信先にする（空なら自動検出）")]
    [SerializeField] private string manualIPAddress = "";

    [Header("Result receive (PC→Quest)")]
    [Tooltip("PC が Sim(3) 結果を返送してくるポート（run.py の --result-port と一致）")]
    [SerializeField] private int resultPort = 50002;

    [Header("Liveness (疎通確認)")]
    [Tooltip("PING を送る間隔[s]。PC は PONG で即応答する。")]
    [SerializeField] private float pingIntervalS = 2f;
    [Tooltip("この秒数 PONG が来なければ「サーバ未応答」と判定する。")]
    [SerializeField] private float serverTimeoutS = 6f;

    private const string PrefKeyIP   = "VPTAlign_ServerIP";
    private static readonly byte[] BeaconMagic = Encoding.ASCII.GetBytes("VPTSRV1");
    private const byte TYPE_RESULT = 3;
    private const byte TYPE_RESET  = 4;
    private const byte TYPE_PING   = 5;
    private const byte TYPE_PONG   = 6;
    private const byte TYPE_EVENT  = 7;

    // 試行リセットの通し番号。ヘッダの reqId に載せ、PC 側は同じ番号の重複を無視する
    // （UDP はベストエフォートなので同じ RESET を数回送る。重複でセッションが複数切られるのを防ぐ）。
    //
    // **起動ごとに異なる基点から始める**（Start で初期化）。単純な 1,2,3… だとアプリを
    // 再起動したときに番号が振り出しに戻り、PC 側が「前に見た番号」と一致して RESET を
    // 重複と誤判定する。その結果セッションが切り替わらず、新しい試行のキーフレームが
    // 前の試行のファイルを上書きして CV が読めなくなる（実機で発生）。
    // 時刻由来にするので、フィールド初期化子で Random を呼ぶ制約にも掛からない。
    private uint _resetId;

    /// <summary>PC から Sim(3)（自動位置合わせ結果）を受信したとき（メインスレッドで発火）。
    /// 品質指標（inliers/n/rmse）込みで渡し、受け手（AlignmentManager）が採否ゲートする（P1-b）。</summary>
    public event Action<Sim3Result> OnSim3Result;

    private UdpClient  _send;
    private IPEndPoint _endPoint;   // 現在の送信先（null = 未確定）
    private bool       _sendReady;
    private bool       _pinned;     // 手動指定で固定中

    // discovery（別スレッドが書き、メインスレッドが読む）
    private Thread          _discThread;
    private volatile bool   _discRun;
    private volatile string _learnedIP;
    private volatile int    _learnedPort;

    // result 受信（別スレッドが JSON を書き、メインスレッドが解析）
    private Thread          _resultThread;
    private volatile bool   _resultRun;
    private volatile string _resultJson;
#if UNITY_ANDROID && !UNITY_EDITOR
    private AndroidJavaObject _mcLock;
#endif

    [Serializable]
    private class Sim3Msg
    {
        public float sigma, qx, qy, qz, qw, px, py, pz, rmse;
        public int inliers, n;
    }

    /// <summary>PONG の中身（run.py の build_pong_payload と鍵名を一致させること）。</summary>
    [Serializable]
    private class PongMsg
    {
        public int seq;
        public string session = "";
        public int kf;
        public bool cv_alive;
        public float cv_age = -1f;      // ハートビート無しは -1（0 と区別する）
        public string cv_scene = "";
        public int cv_window;
        public int cv_ok;               // CV が採用したキーフレーム累計
        public bool cv_pub;             // 直近の解を公開したか
        public string cv_why = "";      // 直近の解の採否理由（棄却ならその理由）
        public bool cv_busy;            // 今まさに hloc で処理中か
        public int cv_busy_n;           // 処理中のキーフレーム枚数
        public int ev_ack;              // PC が連続して受信済みの実験イベント seq
        public int ev_n;                // PC が保存した実験イベント数
    }

    // ---- 実験イベントの送信（ACK 付き再送）----
    //
    // CP の実測値は 1 試行 6 点しかなく、測り直しが効かない。キーフレームのように
    // 「落ちたら次を送ればいい」データではないので、投げっぱなしにはできない。
    // PONG に載る ev_ack（PC が連続して受け取れた seq）を見て、未達分だけを再送する。
    private class PendingEvent
    {
        public uint seq;
        public byte[] pkt;
        public float lastSentT;
    }
    private readonly System.Collections.Generic.List<PendingEvent> _outbox =
        new System.Collections.Generic.List<PendingEvent>();
    private float _nextOutboxT;
    private const float EventResendIntervalS = 1.0f;   // 未達分を見直す間隔
    private const int   EventResendPerTick   = 6;      // 1 回に再送する上限（帯域を食い過ぎない）
    private const int   OutboxWarnSize       = 200;    // これを超えたら異常（サーバ未起動など）

    // ---- 疎通確認（PING/PONG）の状態 ----
    // 「送信先 IP を知っている」ことと「サーバが動いている」ことは別物。IP は前回起動時の
    // 学習値が PlayerPrefs に残るし手動設定もできるので、往復を確認しないと疎通判定にならない。
    private uint  _pingSeq;
    private float _nextPingT;
    private readonly long[] _pingSentTicks = new long[16];   // seq&15 → 送信時刻（RTT 計算用）
    private long   _lastPongTicks;        // Interlocked 経由（long は volatile 不可）
    private volatile string _pongJson;
    private PongMsg _pong = new PongMsg();
    private float  _rttMs = -1f;
    private long   _lastBeaconTicks;

    private void Start()
    {
        // 起動ごとに違う基点にする（アプリ再起動で番号が衝突するのを避ける。上の説明を参照）
        _resetId = (uint)(DateTime.UtcNow.Ticks & 0x00FFFFFF);

        _send = new UdpClient { EnableBroadcast = true };
        _sendReady = true;

        if (!string.IsNullOrEmpty(manualIPAddress))
        {
            // 手動指定を最優先・固定（自動検出は動かさない）
            SetTarget(manualIPAddress, sendPort, "manual");
            // 修正4: パース成功時のみ固定する。失敗時は自動検出へフォールバック（送信不能で詰むのを防ぐ）。
            _pinned = _endPoint != null;
            if (!_pinned) Debug.LogWarning("[UDPManager] manualIPAddress が不正のため自動検出にフォールバックします");
        }
        if (!_pinned)
        {
            // 前回学習した IP を即時採用（ビーコンが来る前から送れる）
            string saved = PlayerPrefs.GetString(PrefKeyIP, "");
            if (!string.IsNullOrEmpty(saved)) SetTarget(saved, sendPort, "saved");

            if (autoDiscovery) StartDiscovery();
        }

        StartResultListener();   // PC からの Sim(3) 返送はユニキャストなので常に待受（lock 不要）

        Debug.Log($"[UDPManager] start. autoDiscovery={autoDiscovery && !_pinned} " +
                  $"target={(_endPoint == null ? "<none>" : _endPoint.ToString())} resultPort={resultPort}");
    }

    private void Update()
    {
        SendPingIfDue();
        ConsumePong();
        ServiceOutbox();

        // PC から Sim(3) 結果を受信していたらメインスレッドで解析・通知（手動固定中でも適用する）
        string rj = _resultJson;
        if (!string.IsNullOrEmpty(rj))
        {
            _resultJson = null;
            try
            {
                var m = JsonUtility.FromJson<Sim3Msg>(rj);
                var res = new Sim3Result
                {
                    sigma   = m.sigma,
                    rot     = new Quaternion(m.qx, m.qy, m.qz, m.qw),
                    pos     = new Vector3(m.px, m.py, m.pz),
                    inliers = m.inliers,
                    n       = m.n,
                    rmse    = m.rmse,
                };
                Debug.Log($"[UDPManager] Sim(3) 受信: scale={m.sigma:F4} inliers={m.inliers}/{m.n} rmse={m.rmse:F4}");
                OnSim3Result?.Invoke(res);
            }
            catch (Exception e) { Debug.LogWarning("[UDPManager] Sim(3) JSON 解析失敗: " + e.Message); }
        }

        if (_pinned) return;

        // discovery スレッドが見つけた IP をメインスレッドで反映＆保存
        string ip = _learnedIP;
        if (string.IsNullOrEmpty(ip)) return;
        _learnedIP = null;

        int port = _learnedPort > 0 ? _learnedPort : sendPort;
        if (_endPoint == null || _endPoint.Address.ToString() != ip || _endPoint.Port != port)
        {
            SetTarget(ip, port, "discovery");
            PlayerPrefs.SetString(PrefKeyIP, ip);
            PlayerPrefs.Save();
        }
    }

    // ----------------------------------------------------------------
    // Public
    // ----------------------------------------------------------------

    /// <summary>UTF-8 文字列を送信先へユニキャスト送信する。</summary>
    public void Send(string message) => SendRaw(Encoding.UTF8.GetBytes(message));

    /// <summary>バイト配列を送信先へ送信する。</summary>
    public void SendRaw(byte[] data)
    {
        if (!_sendReady || data == null || data.Length == 0) return;
        if (_endPoint == null) { WarnNoTargetOnce(); return; }
        try { _send.Send(data, data.Length, _endPoint); }
        catch (Exception e) { Debug.LogError("[UDPManager] 送信エラー: " + e.Message); }
    }

    // ---------------- 疎通確認（PING/PONG） ----------------

    /// <summary>一定間隔で PING を送る。PC は PONG を即返す。
    /// 副次的な効果として、キーフレームを 1 枚も送っていない段階でも PC が Quest の IP を
    /// 学習できる（従来はキーフレーム到着まで返送先が分からなかった）。</summary>
    private void SendPingIfDue()
    {
        if (!_sendReady || _endPoint == null) return;
        if (Time.unscaledTime < _nextPingT) return;
        _nextPingT = Time.unscaledTime + Mathf.Max(0.2f, pingIntervalS);

        uint seq = ++_pingSeq;
        _pingSentTicks[seq & 15] = DateTime.UtcNow.Ticks;

        byte[] payload = Encoding.UTF8.GetBytes("{}");
        byte[] pkt = new byte[13 + payload.Length];
        pkt[0] = (byte)'V'; pkt[1] = (byte)'P'; pkt[2] = TYPE_PING;
        pkt[3] = (byte)(seq & 0xFF); pkt[4] = (byte)((seq >> 8) & 0xFF);
        pkt[5] = (byte)((seq >> 16) & 0xFF); pkt[6] = (byte)((seq >> 24) & 0xFF);
        pkt[7] = 0; pkt[8] = 0; pkt[9] = 1; pkt[10] = 0;
        pkt[11] = (byte)(payload.Length & 0xFF); pkt[12] = (byte)((payload.Length >> 8) & 0xFF);
        Array.Copy(payload, 0, pkt, 13, payload.Length);
        SendRaw(pkt);
    }

    /// <summary>受信スレッドが置いた PONG をメインスレッドで解析する。</summary>
    private void ConsumePong()
    {
        string pj = _pongJson;
        if (string.IsNullOrEmpty(pj)) return;
        _pongJson = null;
        try
        {
            var m = JsonUtility.FromJson<PongMsg>(pj);
            if (m == null) return;
            bool wasAlive = _pong != null && _pong.seq > 0;
            _pong = m;

            // RTT: 該当 seq の送信時刻から。取りこぼしで一致しない場合は更新しない。
            long sent = _pingSentTicks[(uint)m.seq & 15];
            if (sent > 0)
            {
                double ms = (DateTime.UtcNow - new DateTime(sent)).TotalMilliseconds;
                if (ms >= 0 && ms < 10000) _rttMs = (float)ms;
            }
            if (!wasAlive)
                Debug.Log($"[UDPManager] サーバ疎通 OK（session={m.session} CV={(m.cv_alive ? "稼働中" : "未検出")}）");
        }
        catch (Exception e) { Debug.LogWarning("[UDPManager] PONG 解析失敗: " + e.Message); }
    }

    // ---------------- 実験イベントの送信（type=7・ACK 付き） ----------------

    /// <summary>実験ログの 1 行を PC へ送る（CP 実測値・試行の開始終了など）。
    ///
    /// 届いたことを PONG の ev_ack で確認するまで送信箱に残し、未達なら再送する。
    /// これがあるので、実験後に adb pull しなくても PC 側だけで解析が完結する。
    /// CP 実測値は 1 試行 6 点しかなく測り直しが効かないので、投げっぱなしにはできない。
    /// </summary>
    /// <param name="seq">呼び出し側（ExperimentLogger）が振る通し番号。1 から連番であること。</param>
    /// <param name="json">JSONL の 1 行そのもの（run と seq を含むこと）。</param>
    public void SendEvent(uint seq, string json)
    {
        if (string.IsNullOrEmpty(json)) return;
        byte[] payload = Encoding.UTF8.GetBytes(json);
        if (payload.Length > 1100)
        {
            // 分割はしない。1 行が MTU を超えるのは想定外なので、黙って壊すより気づかせる。
            Debug.LogError($"[UDPManager] 実験イベントが大きすぎます（{payload.Length}B）: 送信しません");
            return;
        }
        byte[] pkt = new byte[13 + payload.Length];
        pkt[0] = (byte)'V'; pkt[1] = (byte)'P'; pkt[2] = TYPE_EVENT;
        pkt[3] = (byte)(seq & 0xFF); pkt[4] = (byte)((seq >> 8) & 0xFF);
        pkt[5] = (byte)((seq >> 16) & 0xFF); pkt[6] = (byte)((seq >> 24) & 0xFF);
        pkt[7] = 0; pkt[8] = 0; pkt[9] = 1; pkt[10] = 0;
        pkt[11] = (byte)(payload.Length & 0xFF); pkt[12] = (byte)((payload.Length >> 8) & 0xFF);
        Array.Copy(payload, 0, pkt, 13, payload.Length);

        _outbox.Add(new PendingEvent { seq = seq, pkt = pkt, lastSentT = Time.unscaledTime });
        SendRaw(pkt);                       // 初回はすぐ送る
        if (_outbox.Count == OutboxWarnSize)
            Debug.LogWarning($"[UDPManager] 未送達の実験イベントが {OutboxWarnSize} 件たまりました。"
                             + "run.py が動いているか確認してください。");
    }

    /// <summary>ACK 済みを捨て、未達分を再送する。</summary>
    private void ServiceOutbox()
    {
        if (_outbox.Count == 0) return;

        // PC が連続受信できた seq 以下は確実に保存されている。捨ててよい。
        int ack = _pong != null ? _pong.ev_ack : 0;
        if (ack > 0)
        {
            int removed = _outbox.RemoveAll(e => e.seq <= (uint)ack);
            if (removed > 0 && _outbox.Count == 0)
                Debug.Log("[UDPManager] 実験イベントはすべて PC に届きました");
        }
        if (_outbox.Count == 0) return;

        if (Time.unscaledTime < _nextOutboxT) return;
        _nextOutboxT = Time.unscaledTime + EventResendIntervalS;
        if (_endPoint == null) return;

        int sent = 0;
        for (int i = 0; i < _outbox.Count && sent < EventResendPerTick; i++)
        {
            var e = _outbox[i];
            if (Time.unscaledTime - e.lastSentT < EventResendIntervalS) continue;
            e.lastSentT = Time.unscaledTime;
            SendRaw(e.pkt);
            sent++;
        }
    }

    /// <summary>PC からの受領確認がまだ来ていない実験イベントの数。
    /// 実験の終わりにこれが 0 なら、CP 実測値は PC 側に揃っている。</summary>
    public int PendingEventCount => _outbox.Count;
    /// <summary>PC が保存した実験イベントの数（PONG 由来）。</summary>
    public int ServerEventCount => _pong != null ? _pong.ev_n : 0;

    /// <summary>試行リセット（type=4）を PC へ送る。PC は新しい session_* フォルダを切り、
    /// WSL2 の localize.watch は最新セッション追従で蓄積（Cc/Cw）を捨てる。
    ///
    /// これにより「run.py と Quest アプリを両方再起動する」ことなく独立した試行を回せる。
    /// UDP なので取りこぼしに備えて同じ resetId で repeat 回送る。
    /// PC 側は「同じ resetId かつ数秒以内」のときだけ重複とみなす。
    /// </summary>
    /// <param name="infoJson">PC の trial.json に残すメタ（例: {"trial":3,"label":"trial-03"}）。</param>
    public void SendReset(string infoJson, int repeat = 3)
    {
        if (!_sendReady) return;
        if (_endPoint == null) { WarnNoTargetOnce(); return; }

        uint id = ++_resetId;
        byte[] payload = Encoding.UTF8.GetBytes(infoJson ?? "{}");
        byte[] pkt = new byte[13 + payload.Length];
        pkt[0] = (byte)'V'; pkt[1] = (byte)'P'; pkt[2] = TYPE_RESET;
        pkt[3] = (byte)(id & 0xFF); pkt[4] = (byte)((id >> 8) & 0xFF);
        pkt[5] = (byte)((id >> 16) & 0xFF); pkt[6] = (byte)((id >> 24) & 0xFF);
        pkt[7] = 0; pkt[8] = 0; pkt[9] = 1; pkt[10] = 0;
        pkt[11] = (byte)(payload.Length & 0xFF); pkt[12] = (byte)((payload.Length >> 8) & 0xFF);
        Array.Copy(payload, 0, pkt, 13, payload.Length);

        for (int i = 0; i < Mathf.Max(1, repeat); i++) SendRaw(pkt);
        Debug.Log($"[UDPManager] RESET 送信 id={id} {infoJson}");
    }

    /// <summary>送信先 IP が分かっているか。**疎通の判定には使わないこと**
    /// （前回学習した IP や手動設定値が残るため、サーバが落ちていても true になる）。
    /// 疎通は <see cref="ServerAlive"/> を見る。</summary>
    public bool HasTarget => _endPoint != null;

    /// <summary>直近 serverTimeoutS 秒以内に PONG を受け取ったか。= サーバが実際に応答している。</summary>
    public bool ServerAlive => SecondsSinceLastPong >= 0f && SecondsSinceLastPong <= serverTimeoutS;

    /// <summary>最後に PONG を受けてからの秒数（一度も受けていなければ -1）。</summary>
    public float SecondsSinceLastPong
    {
        get
        {
            long t = System.Threading.Interlocked.Read(ref _lastPongTicks);
            return t == 0 ? -1f : (float)(DateTime.UtcNow - new DateTime(t)).TotalSeconds;
        }
    }

    /// <summary>直近の往復時間[ms]（未計測なら負）。</summary>
    public float RttMs => _rttMs;

    /// <summary>WSL2 の CV ワーカ（localize.watch）が動いているか。サーバ未応答なら false。
    /// run.py だけ起動していて CV が未起動だと、キーフレームは届くのに Sim(3) が永遠に
    /// 返らない。実験前にここで気づけるようにしている。</summary>
    public bool CvAlive => ServerAlive && _pong != null && _pong.cv_alive;

    /// <summary>PC 側が今使っているセッションフォルダ名（PONG 由来）。</summary>
    public string ServerSession => _pong != null ? _pong.session : "";
    /// <summary>PC 側が今セッションで受信済みのキーフレーム数（PONG 由来）。</summary>
    public int ServerKeyframeCount => _pong != null ? _pong.kf : 0;
    /// <summary>CV ハートビートの古さ[s]（-1 = ハートビート無し）。</summary>
    public float CvAgeS => _pong != null ? _pong.cv_age : -1f;
    /// <summary>CV が採用したキーフレームの累計（PC 側）。</summary>
    public int CvAcceptedCount => _pong != null ? _pong.cv_ok : 0;
    /// <summary>直近の解を公開したか（false なら棄却されている）。</summary>
    public bool CvPublished => _pong != null && _pong.cv_pub;
    /// <summary>直近の解の採否理由。**ロックしないときはまずここを見る**
    /// （PC 側で棄却されているのか、そもそも解けていないのかが分かる）。</summary>
    public string CvReason => _pong != null ? _pong.cv_why : "";
    /// <summary>PC が今まさにキーフレームを処理中か。hloc の 1 バッチは数秒かかるので、
    /// この間は「未応答」ではなく「処理中」と見せないと誤解を招く。</summary>
    public bool CvBusy => ServerAlive && _pong != null && _pong.cv_busy;
    /// <summary>処理中のキーフレーム枚数。</summary>
    public int CvBusyCount => _pong != null ? _pong.cv_busy_n : 0;
    /// <summary>サーバは応答しているが CV の状況をまだ一度も受け取っていない（起動直後など）。
    /// 「停止」と区別して「確認中」と見せるための判定。</summary>
    public bool CvUnknown => ServerAlive && _pong != null && !_pong.cv_alive && _pong.cv_age < 0f;

    /// <summary>最後にビーコンを受けてからの秒数（-1 = 一度も受けていない）。
    /// ブロードキャストが遮断される LAN では常に -1 になりうる（手動 IP 運用）。診断用。</summary>
    public float SecondsSinceLastBeacon
    {
        get
        {
            long t = System.Threading.Interlocked.Read(ref _lastBeaconTicks);
            return t == 0 ? -1f : (float)(DateTime.UtcNow - new DateTime(t)).TotalSeconds;
        }
    }

    /// <summary>送信先の説明（HUD 用）。疎通状態まで含める。
    /// **文言は英語で書く。** これは HUD の診断行にそのまま出るので、他の HUD 文言（すべて英語）と
    /// 混ざると読みにくい。ログやツールチップの日本語はそのままでよい（HUD には出ない）。
    /// 診断行は 1 行に 5 項目を詰めるので、**短く**保つことも要件（→ AlignmentHud.SetDiag）。</summary>
    public string TargetDescription
    {
        get
        {
            if (_endPoint == null) return "no server (no beacon / no manual IP)";
            string src = _pinned ? "manual" : "auto";
            if (!ServerAlive)
            {
                float age = SecondsSinceLastPong;
                string why = age < 0f ? "no reply" : $"silent {age:F0}s";
                return $"{_endPoint.Address}:{_endPoint.Port} ({src}) — {why}";
            }
            return $"{_endPoint.Address}:{_endPoint.Port} ({src}) {_rttMs:F0}ms";
        }
    }

    // ----------------------------------------------------------------
    // Private
    // ----------------------------------------------------------------

    private void SetTarget(string ip, int port, string src)
    {
        try
        {
            _endPoint = new IPEndPoint(IPAddress.Parse(ip), port);
            Debug.Log($"[UDPManager] 送信先 = {ip}:{port} ({src})");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[UDPManager] 不正な IP '{ip}': {e.Message}");
        }
    }

    private bool _warned;
    private void WarnNoTargetOnce()
    {
        if (_warned) return;
        _warned = true;
        Debug.LogWarning("[UDPManager] 送信先未確定（サーバのビーコン待ち / 手動IP未設定）。");
    }

    // ---------- discovery ----------

    private void StartDiscovery()
    {
        AcquireMulticastLock();
        _discRun = true;
        _discThread = new Thread(DiscoveryLoop) { IsBackground = true };
        _discThread.Start();
    }

    private void DiscoveryLoop()
    {
        UdpClient rx = null;
        try
        {
            rx = new UdpClient();
            rx.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            rx.Client.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));
            rx.Client.ReceiveTimeout = 1000;
            Debug.Log($"[UDPManager] discovery 待受開始 :{discoveryPort}");

            int idleTicks = 0;
            while (_discRun)
            {
                try
                {
                    var remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] buf = rx.Receive(ref remote);
                    if (buf.Length >= BeaconMagic.Length && StartsWith(buf, BeaconMagic))
                    {
                        int port = sendPort;
                        if (buf.Length >= BeaconMagic.Length + 2)
                            port = buf[BeaconMagic.Length] | (buf[BeaconMagic.Length + 1] << 8);
                        _learnedPort = port;
                        _learnedIP   = remote.Address.ToString(); // メインスレッドが拾う
                        System.Threading.Interlocked.Exchange(ref _lastBeaconTicks, DateTime.UtcNow.Ticks);
                        idleTicks = 0;
                    }
                }
                catch (SocketException)
                {
                    // receive timeout（約1秒）。ループ継続しつつ、未受信が続くなら定期的に通知。
                    if (_learnedIP == null && _endPoint == null && (++idleTicks % 5) == 0)
                        Debug.Log($"[UDPManager] discovery ビーコン待機中... ({idleTicks}s) " +
                                  "受信できない場合は AP がブロードキャストを遮断している可能性");
                }
            }
        }
        catch (Exception e) { Debug.LogError("[UDPManager] discovery エラー: " + e.Message); }
        finally { rx?.Close(); }
    }

    private static bool StartsWith(byte[] b, byte[] prefix)
    {
        for (int i = 0; i < prefix.Length; i++)
            if (b[i] != prefix[i]) return false;
        return true;
    }

    // ---------- result 受信（PC→Quest, ユニキャスト, 13B ヘッダ + JSON）----------

    private void StartResultListener()
    {
        _resultRun = true;
        _resultThread = new Thread(ResultLoop) { IsBackground = true };
        _resultThread.Start();
    }

    private void ResultLoop()
    {
        UdpClient rx = null;
        try
        {
            rx = new UdpClient();
            rx.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            rx.Client.Bind(new IPEndPoint(IPAddress.Any, resultPort));
            rx.Client.ReceiveTimeout = 1000;
            Debug.Log($"[UDPManager] result 待受開始 :{resultPort}");

            while (_resultRun)
            {
                try
                {
                    var remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] buf = rx.Receive(ref remote);
                    // 13B ヘッダ: 'V','P', type, reqId(4), idx(2), total(2), len(2)
                    if (buf.Length < 13 || buf[0] != (byte)'V' || buf[1] != (byte)'P') continue;

                    int len = buf[11] | (buf[12] << 8);
                    if (buf.Length < 13 + len) continue;

                    if (buf[2] == TYPE_RESULT)
                    {
                        _resultJson = Encoding.UTF8.GetString(buf, 13, len);      // メインスレッドが解析
                    }
                    else if (buf[2] == TYPE_PONG)
                    {
                        // 受信時刻はこのスレッドで確定させる（メインスレッドまで待つと RTT が
                        // 1 フレーム分＝十数 ms 膨らむ）。long は volatile 不可なので Interlocked。
                        System.Threading.Interlocked.Exchange(ref _lastPongTicks, DateTime.UtcNow.Ticks);
                        _pongJson = Encoding.UTF8.GetString(buf, 13, len);
                    }
                }
                catch (SocketException) { /* receive timeout: ループ継続（停止チェック） */ }
            }
        }
        catch (Exception e) { Debug.LogError("[UDPManager] result listener エラー: " + e.Message); }
        finally { rx?.Close(); }
    }

    private void AcquireMulticastLock()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        // Android はブロードキャスト/マルチキャストを既定で受信フィルタするため、
        // MulticastLock を取得しないとビーコンを受信できない。
        try
        {
            using (var up   = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var act  = up.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var wifi = act.Call<AndroidJavaObject>("getSystemService", "wifi"))
            {
                _mcLock = wifi.Call<AndroidJavaObject>("createMulticastLock", "vpt-discovery");
                _mcLock.Call("setReferenceCounted", false);
                _mcLock.Call("acquire");
                Debug.Log("[UDPManager] MulticastLock 取得");
            }
        }
        catch (Exception e) { Debug.LogWarning("[UDPManager] MulticastLock 取得失敗: " + e.Message); }
#endif
    }

    private void ReleaseMulticastLock()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try { if (_mcLock != null) { _mcLock.Call("release"); _mcLock = null; } }
        catch { /* ignore */ }
#endif
    }

    private void OnDestroy()         => Shutdown();
    private void OnApplicationQuit() => Shutdown();

    private void Shutdown()
    {
        _discRun = false;
        _resultRun = false;
        try { _discThread?.Join(1200); } catch { /* ignore */ }
        try { _resultThread?.Join(1200); } catch { /* ignore */ }
        ReleaseMulticastLock();
        if (_send != null) { _send.Close(); _send = null; }
        _sendReady = false;
    }
}
