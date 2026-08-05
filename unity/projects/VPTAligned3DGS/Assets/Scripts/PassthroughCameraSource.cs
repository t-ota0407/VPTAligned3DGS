using System;
using UnityEngine;
using Meta.XR;

/// <summary>
/// パススルーカメラ取得パイプライン（M3 の土台 / Specification.md §4.3）。
///
/// Meta XR の <c>PassthroughCameraAccess</c> をラップし、外観ベース自動位置合わせ(Stage B/C)に必要な
/// 「カメラ画像テクスチャ・内部パラメータ K(Intrinsics)・ワールド姿勢・キャプチャ時刻」を一元提供する。
///
/// 前提: PassthroughCameraAccess 本体は Building Block「Passthrough Camera Access」でシーンに配置する
///       （権限 HEADSET_CAMERA・OculusProjectConfig の isPassthroughCameraAccessEnabled 等もそれが設定）。
///       本コンポーネントは既存の PassthroughCameraAccess を参照して使うだけ。
///
/// M3 ステップ1 の目的: 実機でカメラフレーム・姿勢・内部パラメータが安定取得できることの確認。
///       以後のステップで、ここから特徴抽出→マッチ→PnP→相似変換（必要なら UDP で PC へ）を積む。
/// </summary>
[DefaultExecutionOrder(-100)] // RequestedResolution を PCA 起動前に設定したいので早めに Awake する
public class PassthroughCameraSource : MonoBehaviour
{
    [Header("References")]
    [Tooltip("未設定なら起動時に自動取得。Building Block で配置された PassthroughCameraAccess。")]
    [SerializeField] private PassthroughCameraAccess pca;

    [Header("Camera resolution")]
    [Tooltip("取得解像度を固定する。Intrinsics(K)の基準と一致させるため。" +
             "Quest3 は 1280x960(4:3) と 1280x1280(1:1, v83+/広い垂直FOV/正方ピクセル) を持つ。" +
             "K がクリーンな 1280x1280 を既定にする。")]
    [SerializeField] private Vector2Int requestedResolution = new Vector2Int(1280, 1280);

    [Header("Debug")]
    [SerializeField] private bool logStatus = true;
    [SerializeField] private float logIntervalSec = 1f;
    [Tooltip("任意。状態を UDP でも送る（既定オフ。CV用キーフレームと同チャンネルを汚さないため）。")]
    [SerializeField] private bool sendStatusOverUdp = false;
    [Tooltip("sendStatusOverUdp=true のときの送信先。")]
    [SerializeField] private UDPManager udpManager;

    public bool IsReady => pca != null && pca.IsPlaying;
    public Texture CurrentTexture { get; private set; }
    public Pose CurrentPose { get; private set; }
    public DateTime CurrentTimestamp { get; private set; }
    public Vector2Int CurrentResolution => pca != null ? pca.CurrentResolution : default;

    private float _nextLog;
    private bool  _diagLogged;

    private void Awake()
    {
        if (pca == null) pca = FindFirstObjectByType<PassthroughCameraAccess>();
        if (pca == null)
        {
            Debug.LogWarning("[PassthroughCameraSource] PassthroughCameraAccess が見つかりません。" +
                             "Building Block『Passthrough Camera Access』をシーンに追加してください。");
            return;
        }

        // 取得解像度を固定（Intrinsics の基準解像度と一致させ、K_for_image を一意に正しくする）。
        // PCA 起動前に設定する必要があるため Awake で行う（DefaultExecutionOrder で早めに実行）。
        try { pca.RequestedResolution = requestedResolution; }
        catch (Exception e) { Debug.LogWarning("[PassthroughCameraSource] RequestedResolution 設定失敗: " + e.Message); }
    }

    private void Update()
    {
        if (pca == null || !pca.IsPlaying) return;

        if (pca.IsUpdatedThisFrame)
        {
            CurrentTexture   = pca.GetTexture();
            CurrentPose      = pca.GetCameraPose();
            CurrentTimestamp = pca.Timestamp;
        }

        // 一度だけ: 解像度整合を検証（CurrentResolution と Intrinsics.SensorResolution が一致するか）
        if (!_diagLogged)
        {
            _diagLogged = true;
            var i = pca.Intrinsics;
            var cur = pca.CurrentResolution;
            var sen = i.SensorResolution;
            Debug.Log($"[PassthroughCameraSource] resolution check: current={cur} sensor(K基準)={sen} " +
                      $"focal={i.FocalLength} principal={i.PrincipalPoint}");
            if (cur != sen)
                Debug.LogWarning($"[PassthroughCameraSource] 解像度不整合 current={cur} != sensor={sen}。" +
                                 "K が画像と一致しません。PassthroughCameraAccess の RequestedResolution を " +
                                 $"{sen.x}x{sen.y} に設定してください（Inspector でも可）。");
            else
                Debug.Log("[PassthroughCameraSource] 解像度整合 OK（K は画像基準で正しい）");
        }

        if (logStatus && Time.time >= _nextLog)
        {
            _nextLog = Time.time + logIntervalSec;
            string msg = $"[PassthroughCameraSource] playing={pca.IsPlaying} res={pca.CurrentResolution} " +
                         $"posePos={CurrentPose.position} ts={CurrentTimestamp:HH:mm:ss.fff}";
            Debug.Log(msg);
            if (sendStatusOverUdp && udpManager != null) udpManager.Send(msg); // 既定オフ
        }
    }

    /// <summary>最新フレーム（テクスチャ・撮影時刻のワールド姿勢・タイムスタンプ）を取得。未準備なら false。</summary>
    public bool TryGetLatest(out Texture tex, out Pose pose, out DateTime ts)
    {
        tex = CurrentTexture; pose = CurrentPose; ts = CurrentTimestamp;
        return IsReady && tex != null;
    }

    /// <summary>内部パラメータ（焦点距離・主点・センサ解像度）。未準備なら false。</summary>
    public bool TryGetIntrinsics(out Vector2 focal, out Vector2 principal, out Vector2Int sensorRes)
    {
        focal = default; principal = default; sensorRes = default;
        if (pca == null) return false;
        var i = pca.Intrinsics;
        focal = i.FocalLength; principal = i.PrincipalPoint; sensorRes = i.SensorResolution;
        return true;
    }
}
