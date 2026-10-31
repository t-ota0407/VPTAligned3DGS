using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// 位置合わせ結果の永続化 v3（空間アンカー・多数決追従）。
///
/// 保存するのは splatRoot の「アンカーから見た相対姿勢」と符号付きスケール。ワールド座標そのものは
/// 保存しない — Quest のトラッキング原点（FloorLevel）は起動・再センタリング・電源の入れ直しで変わり、
/// 保存した数値の基準が失われるため（→ Specification.md §15.2）。アンカーは空間マップの中の点で、
/// OVRSpatialAnchor が毎フレーム「今の原点から見た姿勢」に更新するので、相対姿勢から正しい位置に戻せる。
///
/// v3 での変更（→ Specification.md §15.5 / ImplementationSpec.md §7）:
/// - アンカーは既定 3 個。置き場所は「実際に歩いた軌跡」から互いに離れた点を選ぶ
///   （旧: スプラット原点＝COLMAP 原点の近く。壁の中や部屋の外にあることがあった）。
/// - 復元後は 1 個のアンカーへの親付けをやめ、**追跡中のアンカーの多数決**で毎フレーム姿勢を決める。
///   一致は境界ボックス 8 隅の移動量で判定する（[[AnchorMath]]）。
/// - 復元は 1 回で諦めず、見つかるまで再試行し続ける（電源投入直後は空間の再認識に時間がかかる）。
/// - 保存は「新アンカー作成 → 保存 → JSON 書き換え → 旧アンカー消去」の順
///   （旧: 先に旧を消していたので、作成に失敗すると新旧どちらも失った）。
/// - 保存データにモデルの指紋（GaussianSplatAsset.dataHash / splatCount）を残し、不一致なら復元しない
///   （同名で中身の違う .ply に差し替えると、古い姿勢を黙って当ててしまうため）。
/// - Meta XR SDK v85 で Obsolete になった API（Save / Erase / LoadUnboundAnchors / Localize）から *Async へ移行。
///
/// 姿勢の書き込みは必ず AlignmentManager を通す（姿勢の唯一の真実。ImplementationSpec.md §6.1）。
/// 追従が書き換えるのは <see cref="AlignmentManager.IsAnchored"/> の間だけで、手動操作や新しい Sim(3) で
/// 姿勢が変わったら、次の保存が終わるまで書き換えない。
///
/// 保存先は persistentDataPath/&lt;fileKey&gt;_&lt;modelId&gt;.json。実験とデモは同じアプリなので、
/// デモは fileKey を分ける（[[AppMode]].DemoFileKey）。
/// modelId は [[GaussianSplatController]].ModelId（空ならアセット名を自動採用）。
/// 依存: Meta XR SDK（OVRSpatialAnchor）。OculusProjectConfig の anchorSupport と USE_ANCHOR_API 権限。
/// </summary>
[DefaultExecutionOrder(100)]   // OVRSpatialAnchor が Update で更新した姿勢を、ここの LateUpdate で読む
[RequireComponent(typeof(AlignmentManager))]
public class AnchorPersistence : MonoBehaviour
{
    /// <summary>復元の進み具合。</summary>
    public enum RestoreState
    {
        /// <summary>まだ始めていない。</summary>
        Idle,
        /// <summary>実験モード（復元も保存もしない）。</summary>
        Disabled,
        /// <summary>保存が無い（初回・消去後）。</summary>
        NoSave,
        /// <summary>保存は別のモデルのもの。</summary>
        ModelMismatch,
        /// <summary>保存したアンカーを端末から読み込み中（見つかるまで再試行する）。</summary>
        Loading,
        /// <summary>読み込めた。空間の中で見つけている途中（多数決が揃うまで）。</summary>
        Searching,
        /// <summary>多数決が揃い、姿勢を採用した（以後は追従）。</summary>
        Restored,
    }

    [Header("References")]
    [SerializeField] private Transform splatRoot;
    [SerializeField] private GaussianSplatController splatController;

    [Header("Settings")]
    [Tooltip("保存ファイル名の接頭辞（<key>_<modelId>.json）。デモは DemoController が " +
             "AppMode.DemoFileKey（vptalign_demo）に変える。実験とデモは同じアプリなので、" +
             "名前を分けないと実験の試行開始（ClearSaved）がデモの保存を消してしまう。")]
    [SerializeField] private string fileKey = "vptalign";
    [Tooltip("アンカー操作 1 回あたりのタイムアウト[s]（作成・保存・消去・1 回の Localize）。")]
    [SerializeField] private float timeoutSeconds = 10f;
    [SerializeField] private bool restoreOnStart = true;
    [Tooltip("作成するアンカー数。3 以上推奨（2 個では食い違ったときにどちらが正しいか決められない）。")]
    [SerializeField] private int anchorCount = 3;
    [Tooltip("アンカー同士の最小間隔[m]。歩いた軌跡からこれ以上離れた点を選ぶ。")]
    [SerializeField] private float anchorSeparationM = 1.5f;
    [Tooltip("2 つのアンカーが示す splatRoot 姿勢を「一致」とみなす最大のずれ[m]。\n" +
             "スプラット境界ボックス 8 隅の移動量で測る（AlignmentManager の連続一致と同じ考え方）。" +
             "隅は原点から数 m 離れるので、アンカーの向きの小さな差も拡大されて現れる。要実機調整。")]
    [SerializeField] private float anchorAgreeM = 0.10f;
    [Tooltip("表示に必要な、互いに一致するアンカーの数（保存数がこれより少なければ保存数）。")]
    [SerializeField] private int minAgreeingAnchors = 2;
    [Tooltip("見つからないアンカーの読み込み・Localize を再試行する間隔[s]。")]
    [SerializeField] private float retryIntervalS = 2f;
    [Tooltip("歩いた軌跡を記録する間隔[m]（アンカーの置き場所の候補）。")]
    [SerializeField] private float walkSampleM = 0.5f;
    [Tooltip("Lock / 自動確定（OnLocked）で自動的に保存する。\n" +
             "デモでは DemoController が保存の時機を決める（SaveNow）ので OFF にされる。")]
    [SerializeField] private bool autoSaveOnLock = true;

    [Header("Experiment")]
    [Tooltip("実験モード: 起動時の復元も確定時の保存も行わない。\n" +
             "評価 E2（コールドスタート）は「毎回まっさらな状態から自動で合うか」を測るので、" +
             "前回の姿勢がアンカーから復元されると起動直後にロック済みと記録され、測定が壊れる。\n" +
             "評価 E1 の独立試行でも、前試行の解が初期姿勢として残らないようにするため ON にする。")]
    [SerializeField] private bool experimentMode = false;

    [Header("Save throttle (P2-c)")]
    [SerializeField] private float minSavePosDeltaM = 0.02f;
    [SerializeField] private float minSaveRotDeg = 1f;
    [SerializeField] private float minSaveScaleFrac = 0.005f;
    [SerializeField] private float minSaveIntervalS = 30f;

    private AlignmentManager _align;
    private Transform _head;

    // ---- 現在のアンカー群（復元したもの、または直近に保存したもの） ----
    private class Bound { public OVRSpatialAnchor anchor; public GameObject go; public Pose rel; }
    private readonly List<Bound> _bound = new List<Bound>();
    private float _savedScale = 1f;
    private int   _savedCount;       // 保存されているアンカー数（JSON のレコード数）
    private int   _epoch;            // 保存の入れ替え・消去のたびに進め、古い復元コルーチンを止める
    private bool  _adopted;          // 現在のアンカー群から姿勢を採用済みか
    private bool  _forceSingle;      // スタッフの「Show anyway」: 一致が揃わなくても 1 個で採用する
    private float _searchStartT = -1f;
    private int   _loadAttempts;

    // ---- 追従（毎フレームの多数決） ----
    private readonly List<Pose> _cands = new List<Pose>();
    private readonly List<int>  _agree = new List<int>();
    private Pose _lastPose = Pose.identity;

    // ---- 歩いた軌跡（アンカーの置き場所の候補） ----
    private readonly List<Vector3> _walk = new List<Vector3>();
    private const int MaxWalkPoints = 512;

    // ---- 保存 ----
    private bool       _saving;
    private bool       _dirty;          // 未保存の確定姿勢が保留中か
    private bool       _forceNextSave;  // 次の保存は間引かない（SaveNow）
    private string     _pendingSource = "manual";
    private Vector3    _pendingPos;
    private Quaternion _pendingRot = Quaternion.identity;
    private float      _pendingScale = 1f;
    private bool       _hasSaved;
    private Vector3    _lastSavedPos;
    private Quaternion _lastSavedRot = Quaternion.identity;
    private float      _lastSavedScale = 1f;
    private float      _lastSaveTime = -9999f;

    [Serializable] private class AnchorRecord { public string uuid; public float[] pos; public float[] rot; }
    [Serializable] private class SaveData
    {
        public int version;          // 3（v2 以前のファイルには無い → 0）
        public string modelId;
        public string savedUtc;
        public string source;        // "auto"（自動位置合わせの確定）/ "manual"（手動 Lock）
        public float rmse = -1f;     // 自動のときの world 残差[m]（手動は -1）
        public float scale;          // 符号付き一様スケール（負＝反射）
        public string dataHash;      // GaussianSplatAsset.dataHash（モデルの指紋）
        public int splatCount;
        public AnchorRecord[] anchors;
    }

    // ================= 公開状態（DemoController / HUD 用） =================

    /// <summary>保存が終わったときに発火する（成功なら true）。</summary>
    public event Action<bool> OnSaveFinished;

    public RestoreState State { get; private set; } = RestoreState.Idle;
    /// <summary>保存されているアンカー数。</summary>
    public int  SavedAnchorCount => _savedCount;
    /// <summary>端末から読み込めたアンカー数。</summary>
    public int  LoadedCount { get; private set; }
    /// <summary>空間の中で見つかった（Localize 済みの）アンカー数。</summary>
    public int  LocalizedCount => _bound.Count;
    /// <summary>いま追跡できているアンカー数。</summary>
    public int  TrackedCount { get; private set; }
    /// <summary>いま互いに一致しているアンカー数（多数派の大きさ）。</summary>
    public int  AgreeingCount { get; private set; }
    /// <summary>表示に必要な一致数。</summary>
    public int  RequiredAgreeing => _forceSingle ? 1 : Mathf.Max(1, Mathf.Min(minAgreeingAnchors, Mathf.Max(1, _savedCount)));
    /// <summary>いま多数決が揃っているか（このフレームの LateUpdate の結果）。</summary>
    public bool HasConsensus { get; private set; }
    /// <summary>追跡中のアンカーのうち、多数派から最も外れたもののずれ[m]（計算できなければ負）。</summary>
    public float WorstDisagreementM { get; private set; } = -1f;
    /// <summary>スタッフの強制表示（一致の確認なし）で採用中か。</summary>
    public bool IsForced => _forceSingle;
    public bool HasSave => _savedCount > 0;
    /// <summary>復元を始めてからの秒数（始めていなければ負）。</summary>
    public float SearchElapsedS => _searchStartT < 0f ? -1f : Time.realtimeSinceStartup - _searchStartT;
    public string SavedUtc { get; private set; } = "";
    public string SavedSource { get; private set; } = "";
    public float  SavedRmse { get; private set; } = -1f;

    /// <summary>スタッフの「Show anyway」が意味を持つか（見つかっているが一致が揃わない）。</summary>
    public bool CanForceShow =>
        !experimentMode && !_forceSingle && !HasConsensus && TrackedCount >= 1 && _bound.Count > 0;

    /// <summary>復元できていない理由（HUD 用・英語）。問題が無ければ空。</summary>
    public string ProblemText
    {
        get
        {
            switch (State)
            {
                case RestoreState.Disabled:      return "Persistence is off (experiment mode)";
                case RestoreState.NoSave:        return "No saved alignment";
                case RestoreState.ModelMismatch: return "Saved alignment is for a different 3DGS model";
                case RestoreState.Idle:          return "";
            }
            if (State == RestoreState.Loading)
                return _loadAttempts > 1
                    ? "Saved anchors not found on this headset (boundary reset?)"
                    : "Loading saved anchors…";
            if (HasConsensus) return "";
            if (_bound.Count == 0) return "Room not recognized yet — look around, check the lighting";
            if (TrackedCount >= 2 && AgreeingCount < RequiredAgreeing && WorstDisagreementM >= 0f)
                return $"Anchors disagree by {WorstDisagreementM * 100f:F0} cm";
            return $"Found {TrackedCount} of {RequiredAgreeing} anchors needed — look around";
        }
    }

    /// <summary>実験モードの実行時切替（ExperimentController から呼ぶ）。</summary>
    public bool ExperimentMode
    {
        get => experimentMode;
        set
        {
            experimentMode = value;
            if (value) { _epoch++; State = RestoreState.Disabled; }   // 進行中の復元も止める
            Debug.Log("[AnchorPersistence] experimentMode = " + value);
        }
    }

    /// <summary>Lock / 自動確定で自動保存するか（DemoController が OFF にして SaveNow で明示的に保存する）。</summary>
    public bool AutoSaveOnLock { get => autoSaveOnLock; set => autoSaveOnLock = value; }

    /// <summary>復元が揃ったときに姿勢を採用してよいか（既定 true）。
    /// 裏で探し続けている間にスタッフが再位置合わせ・手動調整を始めたら、DemoController が false にする。
    /// そうしないと、遅れて見つかった古いアンカーが作業中の姿勢を奪ってしまう。</summary>
    public bool AllowAdopt { get; set; } = true;

    // ================= ライフサイクル =================

    private void Awake()
    {
        _align = GetComponent<AlignmentManager>();
        if (splatController == null) splatController = FindFirstObjectByType<GaussianSplatController>();
        if (splatRoot == null && splatController != null) splatRoot = splatController.SplatRoot;
        var rig = FindFirstObjectByType<OVRCameraRig>();
        if (rig != null) _head = rig.centerEyeAnchor;
        if (_head == null && Camera.main != null) _head = Camera.main.transform;
    }

    private void Start()
    {
        if (_align != null) _align.OnLocked += OnAlignmentLocked;
        if (experimentMode)
        {
            State = RestoreState.Disabled;
            Debug.Log("[AnchorPersistence] experimentMode=true → 復元も保存も行いません（コールドスタート測定のため）");
            return;
        }
        if (restoreOnStart) BeginRestore();
    }

    private void OnDestroy()
    {
        if (_align != null) _align.OnLocked -= OnAlignmentLocked;
    }

    private string ModelId
    {
        get
        {
            string id = splatController != null ? splatController.ModelId : "default";
            var sb = new StringBuilder(id.Length);
            foreach (char c in id) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_');
            return sb.Length > 0 ? sb.ToString() : "default";
        }
    }

    private string SavePath => Path.Combine(Application.persistentDataPath, $"{fileKey}_{ModelId}.json");

    /// <summary>保存ファイル名の接頭辞。復元が始まる前（Awake の間）に設定すること。</summary>
    public string FileKey
    {
        get => fileKey;
        set { if (!string.IsNullOrEmpty(value)) fileKey = value; }
    }

    private void Update()
    {
        if (_dirty) TrySavePending();   // 間引かれた保存要求のリトライ
    }

    // ================= 追従（毎フレームの多数決） =================

    private void LateUpdate()
    {
        RecordWalk();

        HasConsensus = false; TrackedCount = 0; AgreeingCount = 0;
        if (experimentMode || _saving || _bound.Count == 0) return;

        _cands.Clear();
        for (int i = 0; i < _bound.Count; i++)
        {
            var b = _bound[i];
            // 一時的に見失ったアンカー（IsTracked=false）は姿勢が古いままなので数えない
            if (b.anchor == null || !b.anchor.IsTracked) continue;
            Transform t = b.anchor.transform;
            _cands.Add(AnchorMath.Compose(new Pose(t.position, t.rotation), b.rel));
        }
        TrackedCount = _cands.Count;
        if (TrackedCount == 0) return;

        GetBounds(out Vector3 mn, out Vector3 mx);
        AnchorMath.LargestAgreeingSet(_cands, _savedScale, mn, mx, anchorAgreeM, _agree);
        AgreeingCount = _agree.Count;
        Pose pose = AnchorMath.Average(_cands, _agree);
        WorstDisagreementM = AnchorMath.MaxShiftFrom(pose, _cands, _savedScale, mn, mx);
        HasConsensus = AgreeingCount >= RequiredAgreeing;
        if (!HasConsensus) return;
        _lastPose = pose;

        if (!_adopted)
        {
            if (!AllowAdopt) return;
            _adopted = true;
            State = RestoreState.Restored;
            if (_align != null) _align.AdoptAnchoredPose(pose.position, pose.rotation, _savedScale, restored: true);
            _hasSaved = true; _lastSavedPos = pose.position; _lastSavedRot = pose.rotation;
            _lastSavedScale = _savedScale; _lastSaveTime = Time.realtimeSinceStartup;
            Debug.Log($"[AnchorPersistence] 復元: {AgreeingCount}/{TrackedCount} 個のアンカーが一致" +
                      $"（見つかった {_bound.Count}/{_savedCount}・{SearchElapsedS:F1}s・最大ずれ {WorstDisagreementM * 100f:F1}cm" +
                      (_forceSingle ? "・スタッフの強制表示" : "") + $"） scale={_savedScale:F4}");
        }
        else if (_align != null)
        {
            _align.SetAnchoredPose(pose.position, pose.rotation, _savedScale);
        }
    }

    /// <summary>いまのアンカー群の多数決姿勢をもう一度採用する（デモで再位置合わせ・手動調整を取り消したとき）。
    /// 一致が揃っていなければ false。</summary>
    public bool TryAdoptCurrent()
    {
        if (!HasConsensus || _align == null) return false;
        _adopted = true;
        State = RestoreState.Restored;
        _align.AdoptAnchoredPose(_lastPose.position, _lastPose.rotation, _savedScale, restored: false);
        return true;
    }

    /// <summary>スタッフの「Show anyway」: 一致が揃わなくても、見つかっているアンカーで表示する。
    /// 互いの確認が無いので HUD に「未検証」と出し続ける。次の保存で解除される。</summary>
    public void ForceShowAnyway()
    {
        if (!CanForceShow) return;
        _forceSingle = true;
        Debug.LogWarning("[AnchorPersistence] スタッフ操作により、一致の確認なしで表示します（未検証）");
    }

    // ================= 歩いた軌跡 =================

    private void RecordWalk()
    {
        if (_head == null) return;
        Vector3 p = _head.position;
        if (_walk.Count > 0 && Vector3.Distance(_walk[_walk.Count - 1], p) < walkSampleM) return;
        if (_walk.Count >= MaxWalkPoints) _walk.RemoveAt(0);
        _walk.Add(p);
    }

    /// <summary>歩いた軌跡を捨てる（再位置合わせの開始時・原点が動いたとき）。
    /// 原点が動く前の点は今の座標系では別の場所を指すので、アンカーの置き場所に使えない。</summary>
    public void ResetWalk() => _walk.Clear();

    // ================= 保存 =================

    /// <summary>いまの splatRoot の姿勢を、間引かずに保存する（デモの確定・手動 Lock）。</summary>
    public void SaveNow(string source)
    {
        if (experimentMode || splatRoot == null) return;
        _pendingPos = splatRoot.position; _pendingRot = splatRoot.rotation; _pendingScale = splatRoot.localScale.x;
        _pendingSource = source;
        _forceNextSave = true;
        _dirty = true;
        TrySavePending();
    }

    private void OnAlignmentLocked(Vector3 pos, Quaternion rot, Vector3 scale)
    {
        if (experimentMode || !autoSaveOnLock) return;   // 試行間で状態を持ち越さない／デモは SaveNow で保存する
        // 最新の確定姿勢を常に保留(dirty)し、条件が整い次第保存する。
        // スロットルで「保存しない=捨てる」にすると、直近保存から 30 秒以内の大きな手動修正が失われる。
        _pendingPos = pos; _pendingRot = rot; _pendingScale = scale.x;
        _pendingSource = _align != null && _align.IsAutoConfirmed ? "auto" : "manual";
        _dirty = true;
        TrySavePending();
    }

    private void TrySavePending()
    {
        if (!_dirty || _saving) return;
        if (_hasSaved && !_forceNextSave)
        {
            float dPos   = Vector3.Distance(_pendingPos, _lastSavedPos);
            float dDeg   = Quaternion.Angle(_pendingRot, _lastSavedRot);
            float dScale = Mathf.Abs(_pendingScale - _lastSavedScale) / Mathf.Max(1e-4f, Mathf.Abs(_lastSavedScale));
            bool tooSmall = dPos < minSavePosDeltaM && dDeg < minSaveRotDeg && dScale < minSaveScaleFrac;
            if (tooSmall) { _dirty = false; return; }   // 保存済みとほぼ同じ → 保存不要（捨ててよいのはここだけ）
            if (Time.realtimeSinceStartup - _lastSaveTime < minSaveIntervalS) return; // 間隔未達 → dirty のまま待つ
        }
        _dirty = false;
        _forceNextSave = false;
        StartCoroutine(SaveRoutine(_pendingPos, _pendingRot, _pendingScale, _pendingSource));
    }

    private IEnumerator SaveRoutine(Vector3 splatPos, Quaternion splatRot, float scale, string source)
    {
        // 例外や早期 yield break でもフラグが確実に戻るよう try/finally でラップ
        _saving = true;
        bool ok = false;
        try { yield return SaveRoutineInner(splatPos, splatRot, scale, source, r => ok = r); }
        finally { _saving = false; }
        OnSaveFinished?.Invoke(ok);
    }

    private IEnumerator SaveRoutineInner(Vector3 splatPos, Quaternion splatRot, float scale, string source,
                                         Action<bool> done)
    {
        var splatPose = new Pose(splatPos, splatRot);
        float rmse = source == "auto" && _align != null ? _align.LastAcceptedRmse : -1f;

        // ---- 1. 新しいアンカーを作る（歩いた範囲から互いに離れた点） ----
        var positions = ChooseAnchorPositions(splatPos);
        var gos = new List<GameObject>();
        var created = new List<OVRSpatialAnchor>();
        foreach (var p in positions)
        {
            var go = new GameObject("VPTAlignAnchor");
            go.transform.SetPositionAndRotation(p, Quaternion.identity);
            gos.Add(go);
            created.Add(go.AddComponent<OVRSpatialAnchor>());
        }
        float t0 = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - t0 < timeoutSeconds)
        {
            bool pending = false;
            foreach (var a in created) if (a != null && !a.Created) { pending = true; break; }
            if (!pending) break;
            yield return null;
        }

        // 相対姿勢は作成完了時点のアンカー姿勢から計算する（作成時に実行系が姿勢を丸めることがある）
        var fresh = new List<Bound>();
        for (int i = 0; i < created.Count; i++)
        {
            var a = created[i];
            // 作成に失敗すると OVRSpatialAnchor は**コンポーネントだけ** Destroy(this) する（GameObject は残る）。
            // a == null を読み飛ばすだけだと空の GameObject が溜まるので、GameObject ごと片付ける。
            if (a == null || !a.Created)
            {
                Debug.LogWarning("[AnchorPersistence] アンカー作成失敗" + (a == null ? "" : "（timeout）"));
                if (gos[i] != null) Destroy(gos[i]);
                continue;
            }
            var ap = new Pose(a.transform.position, a.transform.rotation);
            fresh.Add(new Bound { anchor = a, go = gos[i], rel = AnchorMath.Relative(ap, splatPose) });
        }

        // ---- 2. 端末に保存する（1 個ずつ。一部が失敗しても残りで続ける） ----
        var tasks = new List<OVRTask<OVRResult<OVRAnchor.SaveResult>>>();
        foreach (var b in fresh) tasks.Add(b.anchor.SaveAnchorAsync());
        t0 = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - t0 < timeoutSeconds)
        {
            bool pending = false;
            foreach (var t in tasks) if (!t.IsCompleted) { pending = true; break; }
            if (!pending) break;
            yield return null;
        }
        var saved = new List<Bound>();
        for (int i = 0; i < fresh.Count; i++)
        {
            bool ok = false;
            try { ok = tasks[i].IsCompleted && tasks[i].GetResult().Success; }
            catch (Exception e) { Debug.LogWarning("[AnchorPersistence] 保存結果の取得に失敗: " + e.Message); }
            if (ok) saved.Add(fresh[i]);
            else { Debug.LogWarning("[AnchorPersistence] アンカー保存失敗"); if (fresh[i].go != null) Destroy(fresh[i].go); }
        }
        if (saved.Count == 0)
        {
            Debug.LogWarning("[AnchorPersistence] 保存できたアンカーが 0 → 中止（旧い保存はそのまま残す）");
            done(false);
            yield break;
        }

        // ---- 3. JSON を書き換える（一時ファイル → 置き換え。途中で落ちても旧ファイルは壊れない） ----
        TryGetFingerprint(out string hash, out int count);
        var records = new List<AnchorRecord>();
        foreach (var b in saved)
            records.Add(new AnchorRecord { uuid = b.anchor.Uuid.ToString(), pos = V(b.rel.position), rot = Q(b.rel.rotation) });
        var data = new SaveData
        {
            version = 3, modelId = ModelId, savedUtc = DateTime.UtcNow.ToString("o"),
            source = source, rmse = rmse, scale = scale, dataHash = hash, splatCount = count,
            anchors = records.ToArray(),
        };
        if (!WriteJsonAtomically(SavePath, JsonUtility.ToJson(data, true)))
        {
            // 端末には保存したが JSON が無いと誰も参照できない。消して旧い保存を残す
            yield return EraseAnchors(saved);
            done(false);
            yield break;
        }

        // ---- 4. 入れ替えてから旧アンカーを消す ----
        _epoch++;   // 旧 UUID を探している復元コルーチンを止める（以後はこのアンカー群が正）
        var old = new List<Bound>(_bound);
        _bound.Clear();
        _bound.AddRange(saved);
        _savedScale = scale; _savedCount = saved.Count; LoadedCount = saved.Count;
        _adopted = true; _forceSingle = false;
        State = RestoreState.Restored;
        SavedUtc = data.savedUtc; SavedSource = source; SavedRmse = rmse;
        yield return EraseAnchors(old);

        // 保存の間に姿勢が変わっていなければ、アンカー追従に戻す（変わっていれば次の保存を待つ）
        if (splatRoot != null && _align != null
            && Vector3.Distance(splatRoot.position, splatPos) < 1e-3f
            && Quaternion.Angle(splatRoot.rotation, splatRot) < 0.05f)
            _align.MarkAnchored();

        _hasSaved = true; _lastSavedPos = splatPos; _lastSavedRot = splatRot;
        _lastSavedScale = scale; _lastSaveTime = Time.realtimeSinceStartup;
        Debug.Log($"[AnchorPersistence] 保存: アンカー {saved.Count}/{positions.Count} 個 → {SavePath} " +
                  $"(source={source} scale={scale:F4} rmse={rmse:F4})");
        done(true);
    }

    /// <summary>アンカーの置き場所。歩いた軌跡から互いに離れた点を選び、足りなければ頭の周りに補う
    /// （手動 Lock だけで保存した場合など、ほとんど歩いていないとき）。</summary>
    private List<Vector3> ChooseAnchorPositions(Vector3 splatPos)
    {
        int k = Mathf.Max(1, anchorCount);
        var pts = AnchorMath.SpreadPoints(_walk, k, anchorSeparationM);
        if (pts.Count < k)
        {
            Vector3 c = _head != null ? _head.position : splatPos;
            var extra = new List<Vector3> { c };
            for (int i = 0; i < 6; i++)
                extra.Add(c + Quaternion.AngleAxis(60f * i, Vector3.up) * Vector3.forward * anchorSeparationM);
            foreach (var e in extra)
            {
                if (pts.Count >= k) break;
                bool far = true;
                foreach (var p in pts) if (Vector3.Distance(p, e) < anchorSeparationM * 0.9f) { far = false; break; }
                if (far) pts.Add(e);
            }
        }
        return pts;
    }

    private static bool WriteJsonAtomically(string path, string json)
    {
        string tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, json);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[AnchorPersistence] JSON 書き出し失敗: " + e.Message);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
            return false;
        }
    }

    // ================= 復元 =================

    /// <summary>保存を読んで復元を始める。見つかるまで裏で再試行し続ける（電源投入直後は
    /// 空間の再認識に時間がかかる → Specification.md §15.3）。多数決が揃った時点で姿勢を採用する。</summary>
    public void BeginRestore()
    {
        if (experimentMode) { State = RestoreState.Disabled; return; }
        int myEpoch = ++_epoch;
        _adopted = false; _forceSingle = false;
        _searchStartT = Time.realtimeSinceStartup;
        _loadAttempts = 0; LoadedCount = 0;

        if (!File.Exists(SavePath))
        {
            State = RestoreState.NoSave; _savedCount = 0;
            Debug.Log("[AnchorPersistence] 保存無し（初回）: " + SavePath);
            return;
        }
        SaveData data;
        try { data = JsonUtility.FromJson<SaveData>(File.ReadAllText(SavePath)); }
        catch (Exception e) { Debug.LogWarning("[AnchorPersistence] JSON 読込失敗: " + e.Message); data = null; }
        if (data == null || data.anchors == null || data.anchors.Length == 0)
        {
            State = RestoreState.NoSave; _savedCount = 0;
            Debug.LogWarning("[AnchorPersistence] 保存データが空または壊れています: " + SavePath);
            return;
        }
        _savedCount = data.anchors.Length;
        _savedScale = data.scale;
        SavedUtc = data.savedUtc ?? ""; SavedSource = data.source ?? ""; SavedRmse = data.rmse;

        if (!FingerprintMatches(data))
        {
            State = RestoreState.ModelMismatch;
            Debug.LogWarning($"[AnchorPersistence] 保存は別のモデルのものです（保存 hash={data.dataHash} n={data.splatCount}）→ 復元しない");
            return;
        }
        StartCoroutine(RestoreRoutine(data, myEpoch));
    }

    private IEnumerator RestoreRoutine(SaveData data, int myEpoch)
    {
        var relByUuid = new Dictionary<Guid, Pose>();
        foreach (var rec in data.anchors)
            if (Guid.TryParse(rec.uuid, out var g)) relByUuid[g] = new Pose(Vec(rec.pos), Quat(rec.rot));
        if (relByUuid.Count == 0) { State = RestoreState.NoSave; yield break; }

        // ---- 1. 端末から読み込む（0 件なら再試行。起動直後は空間の読み込みが済んでいないことがある） ----
        State = RestoreState.Loading;
        var unbound = new List<OVRSpatialAnchor.UnboundAnchor>();
        while (myEpoch == _epoch)
        {
            _loadAttempts++;
            unbound.Clear();
            var task = OVRSpatialAnchor.LoadUnboundAnchorsAsync(relByUuid.Keys, unbound);
            while (!task.IsCompleted) yield return null;
            bool ok = false;
            try { ok = task.GetResult().Success; }
            catch (Exception e) { Debug.LogWarning("[AnchorPersistence] 読込結果の取得に失敗: " + e.Message); }
            if (myEpoch != _epoch) yield break;
            if (ok && unbound.Count > 0) break;
            if (_loadAttempts == 1 || _loadAttempts % 10 == 0)
                Debug.LogWarning($"[AnchorPersistence] 保存したアンカーが端末に見つかりません（{_loadAttempts} 回目）→ 再試行");
            yield return WaitRealtime(retryIntervalS);
        }
        if (myEpoch != _epoch) yield break;
        LoadedCount = unbound.Count;
        State = RestoreState.Searching;
        Debug.Log($"[AnchorPersistence] アンカー {unbound.Count}/{relByUuid.Count} 個を読み込み → 空間の中で探します");

        // ---- 2. 空間の中で見つける（Localize）。見つからないものは間隔を置いて再試行し続ける ----
        var remaining = new List<OVRSpatialAnchor.UnboundAnchor>(unbound);
        var running = new Dictionary<Guid, OVRTask<bool>>();
        float nextTry = 0f;
        while (remaining.Count > 0 && myEpoch == _epoch)
        {
            float now = Time.realtimeSinceStartup;
            for (int i = remaining.Count - 1; i >= 0; i--)
            {
                var ub = remaining[i];
                if (running.TryGetValue(ub.Uuid, out var t))
                {
                    if (!t.IsCompleted) continue;
                    bool ok = false;
                    try { ok = t.GetResult(); } catch (Exception e) { Debug.LogWarning("[AnchorPersistence] Localize 例外: " + e.Message); }
                    running.Remove(ub.Uuid);
                    if (!ok) continue;   // 次の再試行で
                }
                if (ub.Localized)
                {
                    Bind(ub, relByUuid);   // 束縛できなかったもの（既に束縛済み等）も、これ以上は試さない
                    remaining.RemoveAt(i);
                }
                else if (!running.ContainsKey(ub.Uuid) && !ub.Localizing && now >= nextTry)
                {
                    try { running[ub.Uuid] = ub.LocalizeAsync(timeoutSeconds); }
                    catch (Exception e) { Debug.LogWarning("[AnchorPersistence] Localize 開始失敗: " + e.Message); remaining.RemoveAt(i); }
                }
            }
            if (now >= nextTry) nextTry = now + retryIntervalS;
            yield return null;
        }
        if (myEpoch == _epoch)
            Debug.Log($"[AnchorPersistence] アンカー {_bound.Count}/{_savedCount} 個を空間の中で発見");
    }

    private bool Bind(OVRSpatialAnchor.UnboundAnchor ub, Dictionary<Guid, Pose> relByUuid)
    {
        if (!relByUuid.TryGetValue(ub.Uuid, out var rel)) return false;
        var go = new GameObject("VPTAlignAnchor");
        var anchor = go.AddComponent<OVRSpatialAnchor>();
        try { ub.BindTo(anchor); }   // AddComponent と同じフレームで束縛する（Start で新規作成されないように）
        catch (Exception e)
        {
            Debug.LogWarning("[AnchorPersistence] BindTo 失敗: " + e.Message);
            Destroy(go);
            return false;
        }
        _bound.Add(new Bound { anchor = anchor, go = go, rel = rel });
        Debug.Log($"[AnchorPersistence] アンカー発見 {ub.Uuid}（{SearchElapsedS:F1}s）");
        return true;
    }

    // ================= 消去 =================

    private IEnumerator EraseAnchors(List<Bound> list)
    {
        var tasks = new List<OVRTask<OVRResult<OVRAnchor.EraseResult>>>();
        foreach (var b in list)
            if (b.anchor != null && b.anchor.Created)
            {
                try { tasks.Add(b.anchor.EraseAnchorAsync()); }
                catch (Exception e) { Debug.LogWarning("[AnchorPersistence] Erase 開始失敗: " + e.Message); }
            }
        float t0 = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - t0 < timeoutSeconds)
        {
            bool pending = false;
            foreach (var t in tasks) if (!t.IsCompleted) { pending = true; break; }
            if (!pending) break;
            yield return null;
        }
        // コンポーネントが先に消えていても GameObject は残るので、GameObject の参照で片付ける
        foreach (var b in list) if (b.go != null) Destroy(b.go);
    }

    /// <summary>保存データとアンカーを消す（やり直し用・実験の試行リセット）。
    /// 保留中の保存要求も落とす。残っていると次の試行で前試行の姿勢が保存されてしまう。</summary>
    public void ClearSaved()
    {
        _epoch++;   // 進行中の復元を止める
        try { if (File.Exists(SavePath)) File.Delete(SavePath); } catch { /* ignore */ }
        var old = new List<Bound>(_bound);
        _bound.Clear();
        if (old.Count > 0) StartCoroutine(EraseAnchors(old));
        _savedCount = 0; LoadedCount = 0;
        _adopted = false; _forceSingle = false;
        _hasSaved = false;
        _dirty = false; _forceNextSave = false;   // 未保存の確定姿勢を持ち越さない
        _lastSaveTime = -9999f;
        SavedUtc = ""; SavedSource = ""; SavedRmse = -1f;
        if (!experimentMode) State = RestoreState.NoSave;
        Debug.Log("[AnchorPersistence] cleared saved data: " + SavePath);
    }

    // ================= helpers =================

    private void GetBounds(out Vector3 mn, out Vector3 mx)
    {
        mn = new Vector3(-5f, -2f, -5f); mx = new Vector3(5f, 2f, 5f);   // 取得失敗時の代替（AlignmentManager と同じ）
        var rend = splatController != null ? splatController.Renderer : null;
        var asset = rend != null ? rend.asset : null;
        if (asset != null) { mn = asset.boundsMin; mx = asset.boundsMax; }
    }

    private bool TryGetFingerprint(out string hash, out int count)
    {
        hash = ""; count = 0;
        var rend = splatController != null ? splatController.Renderer : null;
        var asset = rend != null ? rend.asset : null;
        if (asset == null) return false;
        hash = asset.dataHash.isValid ? asset.dataHash.ToString() : "";
        count = asset.splatCount;
        return true;
    }

    /// <summary>保存したモデルと今のモデルが同じか。v2 以前の保存（指紋なし）は判定できないので通す。</summary>
    private bool FingerprintMatches(SaveData data)
    {
        if (!TryGetFingerprint(out string hash, out int count)) return true;
        if (!string.IsNullOrEmpty(data.dataHash) && !string.IsNullOrEmpty(hash) && data.dataHash != hash) return false;
        if (data.splatCount > 0 && count > 0 && data.splatCount != count) return false;
        return true;
    }

    private static IEnumerator WaitRealtime(float s)
    {
        float t0 = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - t0 < s) yield return null;
    }

    private static float[] V(Vector3 v) => new[] { v.x, v.y, v.z };
    private static float[] Q(Quaternion q) => new[] { q.x, q.y, q.z, q.w };
    private static Vector3 Vec(float[] a) => (a != null && a.Length == 3) ? new Vector3(a[0], a[1], a[2]) : Vector3.zero;
    private static Quaternion Quat(float[] a) => (a != null && a.Length == 4) ? new Quaternion(a[0], a[1], a[2], a[3]) : Quaternion.identity;
}
