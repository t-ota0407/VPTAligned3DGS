using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Experimental.Rendering;   // GraphicsFormat（修正5）
using UnityEngine.Rendering;                // AsyncGPUReadback（修正5）

/// <summary>
/// キーフレーム・オフロード送信（M3 ステップ2 / Specification.md §5.4）。
///
/// パススルーカメラの 1 フレーム（画像 JPEG ＋ 内部パラメータ K ＋ 撮影時刻のワールド姿勢 ＋ タイムスタンプ）を
/// UDP で LAN 内 PC に送る。重い CV（特徴抽出・マッチ・PnP・相似変換）は PC 側で行う想定。
///
/// 送信トリガ: 左グリップ（押し込みエッジ）または public CaptureAndSend()。
/// パケット: 13B ヘッダ [ 'V','P', type, reqId(4), idx(2), total(2), len(2) ] + ペイロード。
///   type=1: META（UTF-8 JSON: 解像度・K・姿勢・時刻・チャンク数・JPEGバイト数）
///   type=2: IMG チャンク（JPEG の分割）
/// 信頼性は当面ベストエフォート（欠損時は再キャプチャ）。NACK 再送は後続(§5.4)。
/// </summary>
public class KeyframeUploader : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private PassthroughCameraSource source;
    [SerializeField] private UDPManager udp;

    [Header("Encoding")]
    [Tooltip("送信画像の長辺[px]（ダウンスケール）")]
    [SerializeField] private int maxLongEdge = 512;
    [Tooltip("JPEG 品質 (1-100)")]
    [SerializeField] private int jpgQuality = 60;
    [Tooltip("1チャンクのペイロード上限[byte]（IPフラグメンテーション回避）")]
    [SerializeField] private int chunkPayload = 1100;

    [Header("Trigger")]
    [Tooltip("左グリップ短押しで単発送信 / 長押し(1s)で自動モードのトグル")]
    [SerializeField] private bool leftGripToCapture = true;

    [Header("Auto capture (P2-a)")]
    [Tooltip("自動キャプチャの初期状態（実行時は左グリップ長押しでトグル）")]
    [SerializeField] private bool autoCapture = false;
    [Tooltip("前回送信位置からこの並進[m]で自動送信")]
    [SerializeField] private float autoTransM = 0.15f;      // §13 kf.transM
    [Tooltip("前回送信向きからこの回転[deg]で自動送信")]
    [SerializeField] private float autoRotDeg = 10f;        // §13 kf.rotDeg
    [Tooltip("自動送信の最短間隔[s]（UDP帯域・PC処理レート保護）。\n" +
             "確定には 20 枚程度必要なので、2.0 だと送信だけで 40 秒かかり 60 秒の制限に対して" +
             "余裕がなくなる。1.0 にすると 20 秒で揃い、CV の処理待ちを吸収できる。")]
    [SerializeField] private float autoMinIntervalS = 1.0f;
    [Tooltip("この角速度[deg/s]を超えるフレームは送らない（モーションブラー回避）")]
    [SerializeField] private float autoMaxAngVelDeg = 30f;  // §13 kf.maxAngVel

    private const float LongPressS = 1.0f;

    private uint _reqId;
    // グリップ状態機械（短押し=単発 / 長押し=自動トグル）
    private bool  _gripPressed;
    private float _gripDownTime;
    private bool  _gripLongHandled;
    // 自動キャプチャ用トラッキング
    private bool       _hasSent;
    private Vector3    _lastSentPos;
    private Quaternion _lastSentRot = Quaternion.identity;
    private float      _lastSentTime = -999f;
    private float      _lastFrameAngVel;                 // 修正1: 直近カメラフレームの角速度 [deg/s]
    private bool       _hasPrevFrame;
    private DateTime   _prevFrameTs;
    private Quaternion _prevFrameRot = Quaternion.identity;

    /// <summary>自動モードの現在状態（HUD 用・P3）。</summary>
    public bool AutoCaptureOn => autoCapture;
    /// <summary>自動モードのトグル（HUD ボタン用・P3）。</summary>
    public void ToggleAuto() { autoCapture = !autoCapture; Debug.Log("[KeyframeUploader] autoCapture = " + autoCapture); }
    /// <summary>単発送信（HUD ボタン用・P3）。</summary>
    public void SendOne() => CaptureAndSend();
    /// <summary>累計送信キーフレーム数（HUD 用・P3）。試行リセットで 0 に戻る。</summary>
    public int SentCount => (int)_reqId;

    /// <summary>自動キャプチャの明示的な設定（実験制御用）。</summary>
    public void SetAuto(bool on)
    {
        if (autoCapture == on) return;
        autoCapture = on;
        Debug.Log("[KeyframeUploader] autoCapture = " + autoCapture);
    }

    /// <summary>実験の新しい試行のために送信状態を初期化する（評価 E1/E2）。
    ///
    /// reqId を 0 に戻すのは、PC が試行ごとに新しい session_* フォルダへ req_000001.jpg から
    /// 保存し直すため（試行内で連番が揃い、E2 の「ロックまでのキーフレーム数」がそのまま読める）。
    /// 送信中のコルーチンも止め、前試行の画像が新セッションに混ざらないようにする。</summary>
    public void ResetForNewTrial(bool startAuto)
    {
        StopAllCoroutines();          // 送信途中のチャンクを打ち切る
        _reqId = 0;
        _hasSent = false;
        _lastSentTime = -999f;
        _lastSentRot = Quaternion.identity;
        _lastSentPos = Vector3.zero;
        _hasPrevFrame = false;
        _lastFrameAngVel = 0f;
        _gripPressed = false; _gripLongHandled = false;
        autoCapture = startAuto;
        Debug.Log("[KeyframeUploader] ResetForNewTrial: 送信状態を初期化 autoCapture=" + autoCapture);
    }

    private void Awake()
    {
        if (source == null) source = FindFirstObjectByType<PassthroughCameraSource>();
        if (udp == null)    udp    = FindFirstObjectByType<UDPManager>();
    }

    /// <summary>左グリップによる手動送信・自動モード切替を受け付けるか。
    /// 試行の実行中に誤って押すと、自動送信が止まったり余計なキーフレームが混ざって
    /// その試行が壊れる。ExperimentController が試行中だけ false にする。</summary>
    public bool ManualGripEnabled { get; set; } = true;

    private void Update()
    {
        if (leftGripToCapture && ManualGripEnabled) HandleGrip();
        else if (!ManualGripEnabled) { _gripPressed = false; _gripLongHandled = false; }
        if (autoCapture) HandleAutoCapture();
    }

    // 左グリップ: 短押し=単発送信 / 長押し(1s)=自動モードのトグル
    // 明示的に左＝LHandTrigger（利き手で左右が入れ替わる Primary/Secondary は使わない。HUD は右=RHandTrigger）。
    private void HandleGrip()
    {
        float g = OVRInput.Get(OVRInput.RawAxis1D.LHandTrigger);
        if (!_gripPressed && g >= 0.8f)              // 押下エッジ
        {
            _gripPressed = true; _gripDownTime = Time.time; _gripLongHandled = false;
        }
        else if (_gripPressed && g >= 0.8f)          // 保持中
        {
            if (!_gripLongHandled && Time.time - _gripDownTime >= LongPressS)
            {
                autoCapture = !autoCapture;          // 長押し → 自動モード切替
                _gripLongHandled = true;
                Debug.Log("[KeyframeUploader] autoCapture = " + autoCapture);
            }
        }
        else if (_gripPressed && g < 0.5f)           // 離すエッジ
        {
            _gripPressed = false;
            if (!_gripLongHandled) CaptureAndSend();  // 短押し → 単発送信
        }
    }

    // 移動量ベースの自動キャプチャ（角速度・最短間隔でゲート）
    private void HandleAutoCapture()
    {
        if (source == null || udp == null) return;
        if (!source.TryGetLatest(out var tex, out var pose, out var ts) || tex == null) return;

        // 修正1: カメラフレームが更新された時だけ、カメラのタイムスタンプ差で角速度を更新する。
        // 描画フレーム間隔(約72Hz)で割ると、カメラ未更新=0 / 更新直後=過大評価となり、ブレ判定が機能しない。
        if (!_hasPrevFrame || ts != _prevFrameTs)
        {
            if (_hasPrevFrame)
            {
                float dt = Mathf.Max(1e-3f, (float)(ts - _prevFrameTs).TotalSeconds);
                _lastFrameAngVel = Quaternion.Angle(_prevFrameRot, pose.rotation) / dt;
            }
            _prevFrameRot = pose.rotation; _prevFrameTs = ts; _hasPrevFrame = true;
        }

        float now = Time.time;

        if (now - _lastSentTime < autoMinIntervalS) return;   // 最短間隔
        if (_lastFrameAngVel > autoMaxAngVelDeg) return;                // ブレ回避
        bool moved = !_hasSent
            || Vector3.Distance(pose.position, _lastSentPos) >= autoTransM
            || Quaternion.Angle(_lastSentRot, pose.rotation) >= autoRotDeg;
        if (!moved) return;

        CaptureAndSend();   // _lastSent* は CaptureAndSend が記録
    }

    public void CaptureAndSend()
    {
        if (source == null || udp == null) { Debug.LogWarning("[KeyframeUploader] source/udp 未設定"); return; }
        if (!source.TryGetLatest(out var tex, out var pose, out var ts) || tex == null)
        { Debug.LogWarning("[KeyframeUploader] カメラ未準備"); return; }
        // 自動キャプチャの基準（単発送信でも更新して直後の重複自動送信を防ぐ）
        _lastSentPos = pose.position; _lastSentRot = pose.rotation; _lastSentTime = Time.time; _hasSent = true;
        StartCoroutine(CaptureRoutine(tex, pose, ts));
    }

    private IEnumerator CaptureRoutine(Texture src, Pose pose, DateTime ts)
    {
        // ---- ダウンスケールして JPEG 化 ----
        int sw = src.width, sh = src.height;
        float k = Mathf.Min(1f, maxLongEdge / (float)Mathf.Max(sw, sh));
        int w = Mathf.Max(1, Mathf.RoundToInt(sw * k));
        int h = Mathf.Max(1, Mathf.RoundToInt(sh * k));

        // 修正5: ReadPixels(同期GPU待ち)によるフレームヒッチを避け、AsyncGPUReadback で非同期に読み出す。
        // pose はキャプチャ時点で確定済みのため、読み出しが数フレーム遅れても画像と姿勢の対応は崩れない。
        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(src, rt);
        var readback = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32);
        while (!readback.done) yield return null;
        RenderTexture.ReleaseTemporary(rt);   // 読み出し完了後に解放（先に返すと再利用され得る）
        if (readback.hasError) { Debug.LogWarning("[KeyframeUploader] GPU readback 失敗（このキーフレームは破棄）"); yield break; }

        byte[] jpg;
        using (var raw = ImageConversion.EncodeNativeArrayToJPG(
                   readback.GetData<byte>(), GraphicsFormat.R8G8B8A8_UNorm, (uint)w, (uint)h, 0, jpgQuality))
        {
            jpg = raw.ToArray();
        }

        uint req = ++_reqId;
        int total = Mathf.CeilToInt(jpg.Length / (float)chunkPayload);

        // ---- META（K・姿勢・時刻） ----
        // 修正C: 取得失敗時（K=0）を警告して PC 側で除外できるようにする
        if (!source.TryGetIntrinsics(out var focal, out var principal, out var sensorRes))
            Debug.LogWarning("[KeyframeUploader] Intrinsics 取得失敗（K=0 のまま送信）。PC 側で本 req を除外すること。");
        var sb = new StringBuilder(256);
        sb.Append("{\"req\":").Append(req)
          .Append(",\"w\":").Append(w).Append(",\"h\":").Append(h)
          .Append(",\"sw\":").Append(sensorRes.x).Append(",\"sh\":").Append(sensorRes.y)
          .Append(",\"fx\":").Append(focal.x.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(",\"fy\":").Append(focal.y.ToString("F4", System.Globalization.CultureInfo.InvariantCulture))
          .Append(",\"cx\":").Append(principal.x.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(",\"cy\":").Append(principal.y.ToString("F4", System.Globalization.CultureInfo.InvariantCulture))
          .Append(",\"px\":").Append(pose.position.x.ToString("F5", System.Globalization.CultureInfo.InvariantCulture)).Append(",\"py\":").Append(pose.position.y.ToString("F5", System.Globalization.CultureInfo.InvariantCulture)).Append(",\"pz\":").Append(pose.position.z.ToString("F5", System.Globalization.CultureInfo.InvariantCulture))
          .Append(",\"qx\":").Append(pose.rotation.x.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)).Append(",\"qy\":").Append(pose.rotation.y.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)).Append(",\"qz\":").Append(pose.rotation.z.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)).Append(",\"qw\":").Append(pose.rotation.w.ToString("F6", System.Globalization.CultureInfo.InvariantCulture))
          .Append(",\"ts\":\"").Append(ts.ToString("O")).Append("\"")
          .Append(",\"n\":").Append(total).Append(",\"len\":").Append(jpg.Length).Append("}");
        byte[] meta = Encoding.UTF8.GetBytes(sb.ToString());
        udp.SendRaw(Packet(1, req, 0, (ushort)1, meta, meta.Length));

        // ---- IMG チャンク ----
        for (int i = 0; i < total; i++)
        {
            int off = i * chunkPayload;
            int len = Mathf.Min(chunkPayload, jpg.Length - off);
            byte[] payload = new byte[len];
            Array.Copy(jpg, off, payload, 0, len);
            udp.SendRaw(Packet(2, req, (ushort)i, (ushort)total, payload, len));
            if ((i & 7) == 7) yield return null; // 数チャンクごとに1フレーム空けて取りこぼし低減
        }
        Debug.Log($"[KeyframeUploader] sent req={req} img={w}x{h} jpg={jpg.Length}B chunks={total}");
    }

    // 13B ヘッダ + ペイロード
    private static byte[] Packet(byte type, uint reqId, ushort idx, ushort total, byte[] payload, int len)
    {
        byte[] p = new byte[13 + len];
        p[0] = (byte)'V'; p[1] = (byte)'P'; p[2] = type;
        p[3] = (byte)(reqId & 0xFF); p[4] = (byte)((reqId >> 8) & 0xFF); p[5] = (byte)((reqId >> 16) & 0xFF); p[6] = (byte)((reqId >> 24) & 0xFF);
        p[7] = (byte)(idx & 0xFF); p[8] = (byte)((idx >> 8) & 0xFF);
        p[9] = (byte)(total & 0xFF); p[10] = (byte)((total >> 8) & 0xFF);
        p[11] = (byte)(len & 0xFF); p[12] = (byte)((len >> 8) & 0xFF);
        Array.Copy(payload, 0, p, 13, len);
        return p;
    }
}
