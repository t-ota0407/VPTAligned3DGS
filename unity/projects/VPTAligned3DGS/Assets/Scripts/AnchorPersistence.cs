using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// 位置合わせ結果の永続化 v2（M1.5 / Stage F・P4）。
///
/// P4 での変更点:
/// - 保存先を PlayerPrefs 固定キーから「シーン別 JSON ファイル」
///   (Application.persistentDataPath/vptalign_&lt;modelId&gt;.json) へ。複数モデルが干渉しない。
/// - アンカーを複数（既定2個）作って冗長化。各アンカーから見た splatRoot の相対姿勢 T_rel を保存。
/// - 復元時は再ローカライズできたアンカーから splatRoot 姿勢を再構成し、外れを捨てて平均。
///   片方が失敗しても復元できる。全滅時のみ未復元（手動/自動合わせへ）。
///
/// modelId は [[GaussianSplatController]].ModelId（空ならアセット名を自動採用）。
/// スケール（符号付き＝反射）はアンカーに乗らないので JSON に別途保存する。
/// 保存頻度は P2-c のスロットルで抑制（連続自動更新でアンカー再作成が暴れるのを防ぐ）。
///
/// 依存: Meta XR SDK（OVRSpatialAnchor）。OculusProjectConfig の anchorSupport が有効であること。
/// </summary>
[RequireComponent(typeof(AlignmentManager))]
public class AnchorPersistence : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Transform splatRoot;
    [SerializeField] private GaussianSplatController splatController;

    [Header("Settings")]
    [SerializeField] private float timeoutSeconds = 10f;
    [SerializeField] private bool restoreOnStart = true;
    [Tooltip("作成するアンカー数（冗長化。2以上推奨）。")]
    [SerializeField] private int anchorCount = 2;
    [Tooltip("アンカー同士を離す距離[m]（再ローカライズの当たりを分散）。")]
    [SerializeField] private float anchorSeparationM = 1.5f;
    [Tooltip("復元時、各アンカーからの再構成姿勢がこの距離[m]を超えて食い違うものは外れとして破棄。")]
    [SerializeField] private float restoreOutlierM = 0.05f;

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
    private readonly List<OVRSpatialAnchor> _anchors = new List<OVRSpatialAnchor>();

    // 保存抑制の基準（直近に実際に保存/復元した姿勢）
    private bool       _restoring;   // 修正3: RestoreRoutine 実行中フラグ（保存との相互排他）
    private bool       _dirty;       // 修正2: 未保存の確定姿勢が保留中か
    private Vector3    _pendingPos;  // 修正2: 保留中の確定姿勢
    private Quaternion _pendingRot = Quaternion.identity;
    private float      _pendingScale = 1f;
    private bool       _saving;      // 修正B: SaveRoutine 実行中フラグ（多重起動防止）
    private bool       _hasSaved;
    private Vector3    _lastSavedPos;
    private Quaternion _lastSavedRot = Quaternion.identity;
    private float      _lastSavedScale = 1f;
    private float      _lastSaveTime = -9999f;

    [Serializable] private class AnchorRecord { public string uuid; public float[] pos; public float[] rot; }
    [Serializable] private class SaveData { public string modelId; public string savedUtc; public float scale; public AnchorRecord[] anchors; }

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
            Debug.Log("[AnchorPersistence] experimentMode=true → 復元も保存も行いません（コールドスタート測定のため）");
            return;
        }
        if (restoreOnStart) TryRestore();
    }

    /// <summary>実験モードの実行時切替（ExperimentController から呼ぶ）。</summary>
    public bool ExperimentMode
    {
        get => experimentMode;
        set { experimentMode = value; Debug.Log("[AnchorPersistence] experimentMode = " + value); }
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

    private string SavePath => Path.Combine(Application.persistentDataPath, $"vptalign_{ModelId}.json");

    // ================= 保存（確定時） =================

    private void OnAlignmentLocked(Vector3 pos, Quaternion rot, Vector3 scale)
    {
        if (experimentMode) return;   // 試行間で状態を持ち越さない
        // 修正2: 最新の確定姿勢を常に保留(dirty)し、条件が整い次第保存する。
        // 旧実装はスロットルで「保存しない=捨てる」だったため、直近保存から30秒以内の大きな手動修正が失われた。
        _pendingPos = pos; _pendingRot = rot; _pendingScale = scale.x;
        _dirty = true;
        TrySavePending();
    }

    private void Update()
    {
        if (_dirty) TrySavePending();   // 修正2: 間引かれた保存要求のリトライ
    }

    private void TrySavePending()
    {
        if (!_dirty || _saving || _restoring) return;
        if (_hasSaved)
        {
            float dPos   = Vector3.Distance(_pendingPos, _lastSavedPos);
            float dDeg   = Quaternion.Angle(_pendingRot, _lastSavedRot);
            float dScale = Mathf.Abs(_pendingScale - _lastSavedScale) / Mathf.Max(1e-4f, Mathf.Abs(_lastSavedScale));
            bool tooSmall = dPos < minSavePosDeltaM && dDeg < minSaveRotDeg && dScale < minSaveScaleFrac;
            if (tooSmall) { _dirty = false; return; }   // 保存済みとほぼ同じ → 保存不要（捨ててよいのはここだけ）
            if (Time.realtimeSinceStartup - _lastSaveTime < minSaveIntervalS) return; // 間隔未達 → dirty のまま待って保存
        }
        _dirty = false;
        StartCoroutine(SaveRoutine(_pendingPos, _pendingRot, _pendingScale));
    }

    private IEnumerator SaveRoutine(Vector3 splatPos, Quaternion splatRot, float uniformScale)
    {
        // 修正B: 例外や早期 yield break でもフラグが確実に戻るよう try/finally でラップ
        _saving = true;
        try { yield return SaveRoutineInner(splatPos, splatRot, uniformScale); }
        finally { _saving = false; }
    }

    private IEnumerator SaveRoutineInner(Vector3 splatPos, Quaternion splatRot, float uniformScale)
    {
        if (splatRoot != null) splatRoot.SetParent(null, true);   // 一旦アンカーから外す
        yield return EraseAllAnchors();                            // 旧アンカーを片付け

        // アンカー姿勢（splat 原点＋頭寄りの点。互いに anchorSeparationM 以上離す）
        var poses = ComputeAnchorPoses(splatPos);
        var created = new List<OVRSpatialAnchor>();
        foreach (var p in poses)
        {
            var go = new GameObject("VPTAlignAnchor");
            go.transform.SetPositionAndRotation(p.position, p.rotation);
            created.Add(go.AddComponent<OVRSpatialAnchor>());
        }

        // 作成完了待ち（タイムアウト付き）
        float t0 = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - t0 < timeoutSeconds)
        {
            bool all = true;
            foreach (var a in created) if (!a.Created) { all = false; break; }
            if (all) break;
            yield return null;
        }

        // 保存＆レコード生成（作成できたものだけ）。T_rel = anchor^-1 * splat。
        var records = new List<AnchorRecord>();
        int pending = 0;
        foreach (var a in created)
        {
            if (!a.Created) { Debug.LogWarning("[AnchorPersistence] anchor 作成失敗（timeout）"); Destroy(a.gameObject); continue; }
            Vector3 aPos = a.transform.position; Quaternion aRot = a.transform.rotation;
            var rec = new AnchorRecord
            {
                pos = V(Quaternion.Inverse(aRot) * (splatPos - aPos)),
                rot = Q(Quaternion.Inverse(aRot) * splatRot),
            };
            _anchors.Add(a);
            pending++;
            a.Save((anchor, ok) =>
            {
                pending--;
                if (ok) { rec.uuid = anchor.Uuid.ToString(); records.Add(rec); }
                else Debug.LogWarning("[AnchorPersistence] Save 失敗");
            });
        }
        t0 = Time.realtimeSinceStartup;
        while (pending > 0 && Time.realtimeSinceStartup - t0 < timeoutSeconds) yield return null;

        if (records.Count == 0) { Debug.LogWarning("[AnchorPersistence] 保存できたアンカーが0 → 永続化中止"); yield break; }

        var data = new SaveData
        {
            modelId = ModelId, savedUtc = DateTime.UtcNow.ToString("o"),
            scale = uniformScale, anchors = records.ToArray(),
        };
        try { File.WriteAllText(SavePath, JsonUtility.ToJson(data, true)); }
        catch (Exception e) { Debug.LogWarning("[AnchorPersistence] JSON 書き出し失敗: " + e.Message); }
        Debug.Log($"[AnchorPersistence] saved {records.Count} anchors → {SavePath} (scale={uniformScale:F4})");

        // 追従（最も信頼＝先頭のアンカー）。姿勢は保持したまま親子付け。
        if (_anchors.Count > 0 && splatRoot != null) splatRoot.SetParent(_anchors[0].transform, true);

        _hasSaved = true; _lastSavedPos = splatPos; _lastSavedRot = splatRot;
        _lastSavedScale = uniformScale; _lastSaveTime = Time.realtimeSinceStartup;
    }

    private List<Pose> ComputeAnchorPoses(Vector3 splatPos)
    {
        var list = new List<Pose> { new Pose(splatPos, Quaternion.identity) };   // splat 原点
        Vector3 dir = Vector3.right;
        if (_head != null) { var d = _head.position - splatPos; d.y = 0f; if (d.sqrMagnitude > 0.04f) dir = d.normalized; }
        for (int i = 1; i < Mathf.Max(2, anchorCount); i++)
        {
            Quaternion spin = Quaternion.AngleAxis(90f * (i - 1), Vector3.up);
            list.Add(new Pose(splatPos + (spin * dir) * anchorSeparationM, Quaternion.identity));
        }
        return list;
    }

    // ================= 復元（起動時） =================

    public void TryRestore()
    {
        if (!File.Exists(SavePath)) { Debug.Log("[AnchorPersistence] 保存無し（初回）: " + SavePath); return; }
        SaveData data;
        try { data = JsonUtility.FromJson<SaveData>(File.ReadAllText(SavePath)); }
        catch (Exception e) { Debug.LogWarning("[AnchorPersistence] JSON 読込失敗: " + e.Message); return; }
        if (data == null || data.anchors == null || data.anchors.Length == 0)
        { Debug.LogWarning("[AnchorPersistence] 保存データが空"); return; }
        _restoring = true;                     // 修正3: 復元中は保存を保留（TrySavePending 側で待つ）
        StartCoroutine(RestoreWrapper(data));
    }

    private IEnumerator RestoreWrapper(SaveData data)
    {
        // 修正3: 復元と保存の相互排他。失敗・途中離脱でも必ず解除されるよう try/finally。
        try { yield return RestoreRoutine(data); }
        finally { _restoring = false; }
    }

    private IEnumerator RestoreRoutine(SaveData data)
    {
        var relByUuid = new Dictionary<Guid, (Vector3 pos, Quaternion rot)>();
        var uuids = new List<Guid>();
        foreach (var rec in data.anchors)
            if (Guid.TryParse(rec.uuid, out var g)) { relByUuid[g] = (Vec(rec.pos), Quat(rec.rot)); uuids.Add(g); }
        if (uuids.Count == 0) yield break;

        var opts = new OVRSpatialAnchor.LoadOptions
        {
            StorageLocation = OVRSpace.StorageLocation.Local,
            Timeout = timeoutSeconds,
            Uuids = uuids,
        };

        OVRSpatialAnchor.UnboundAnchor[] unbound = null;
        bool loadDone = false;
        OVRSpatialAnchor.LoadUnboundAnchors(opts, res => { unbound = res; loadDone = true; });
        float t0 = Time.realtimeSinceStartup;
        while (!loadDone && Time.realtimeSinceStartup - t0 < timeoutSeconds) yield return null;
        if (unbound == null || unbound.Length == 0) { Debug.LogWarning("[AnchorPersistence] アンカー読込0件 → 未復元"); yield break; }

        // 各アンカーをローカライズ → 候補姿勢を集める
        var candidates = new List<(Vector3 pos, Quaternion rot, OVRSpatialAnchor anchor)>();
        int pending = 0;
        foreach (var ub in unbound)
        {
            pending++;
            ub.Localize((anchorUb, ok) =>
            {
                pending--;
                if (!ok) { Debug.LogWarning("[AnchorPersistence] localize 失敗 " + anchorUb.Uuid); return; }
                if (!relByUuid.TryGetValue(anchorUb.Uuid, out var rel)) return;
                var go = new GameObject("VPTAlignAnchor");
                var anchor = go.AddComponent<OVRSpatialAnchor>();
                anchorUb.BindTo(anchor);
                _anchors.Add(anchor);
                Vector3 aPos = anchor.transform.position; Quaternion aRot = anchor.transform.rotation;
                candidates.Add((aPos + aRot * rel.pos, aRot * rel.rot, anchor));
            }, timeoutSeconds);
        }
        t0 = Time.realtimeSinceStartup;
        while (pending > 0 && Time.realtimeSinceStartup - t0 < timeoutSeconds) yield return null;

        if (candidates.Count == 0) { Debug.LogWarning("[AnchorPersistence] 復元候補0 → 未復元（手動/自動合わせへ）"); yield break; }

        // 外れ除去（最大クラスタ）＋平均
        var keep = LargestCluster(candidates, restoreOutlierM);
        Vector3 pos = Vector3.zero; foreach (var c in keep) pos += c.pos; pos /= keep.Count;
        Quaternion rot = AverageRotation(keep);
        if (splatRoot != null)
        {
            splatRoot.position = pos; splatRoot.rotation = rot;
            splatRoot.localScale = new Vector3(data.scale, data.scale, data.scale);
            splatRoot.SetParent(keep[0].anchor.transform, true);   // 先頭（信頼）に追従
        }
        Debug.Log($"[AnchorPersistence] restored from {keep.Count}/{candidates.Count} anchors (scale={data.scale:F4})");
        _hasSaved = true; _lastSavedPos = pos; _lastSavedRot = rot; _lastSavedScale = data.scale; _lastSaveTime = Time.realtimeSinceStartup;
    }

    // ================= helpers =================

    private static float[] V(Vector3 v) => new[] { v.x, v.y, v.z };
    private static float[] Q(Quaternion q) => new[] { q.x, q.y, q.z, q.w };
    private static Vector3 Vec(float[] a) => (a != null && a.Length == 3) ? new Vector3(a[0], a[1], a[2]) : Vector3.zero;
    private static Quaternion Quat(float[] a) => (a != null && a.Length == 4) ? new Quaternion(a[0], a[1], a[2], a[3]) : Quaternion.identity;

    // 位置が最も凝集するクラスタを残す（少数決）。全一致なら全採用、割れたら多数側。
    private List<(Vector3 pos, Quaternion rot, OVRSpatialAnchor anchor)> LargestCluster(
        List<(Vector3 pos, Quaternion rot, OVRSpatialAnchor anchor)> cands, float thresh)
    {
        if (cands.Count <= 1) return cands;
        int bestI = 0, bestCount = -1;
        for (int i = 0; i < cands.Count; i++)
        {
            int cnt = 0;
            for (int j = 0; j < cands.Count; j++)
                if (Vector3.Distance(cands[i].pos, cands[j].pos) <= thresh) cnt++;
            if (cnt > bestCount) { bestCount = cnt; bestI = i; }
        }
        var keep = new List<(Vector3, Quaternion, OVRSpatialAnchor)>();
        foreach (var c in cands)
            if (Vector3.Distance(cands[bestI].pos, c.pos) <= thresh) keep.Add(c);
        if (keep.Count < cands.Count)
            Debug.LogWarning($"[AnchorPersistence] 外れアンカー {cands.Count - keep.Count} 個を破棄");
        return keep;
    }

    private static Quaternion AverageRotation(List<(Vector3 pos, Quaternion rot, OVRSpatialAnchor anchor)> items)
    {
        if (items.Count == 1) return items[0].rot;
        Quaternion refq = items[0].rot;
        Vector4 sum = Vector4.zero;
        foreach (var it in items)
        {
            Quaternion q = it.rot;
            if (Quaternion.Dot(q, refq) < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);   // 半球を揃える
            sum += new Vector4(q.x, q.y, q.z, q.w);
        }
        sum.Normalize();
        return new Quaternion(sum.x, sum.y, sum.z, sum.w);
    }

    private IEnumerator EraseAllAnchors()
    {
        // 修正A: splatRoot がアンカーの子のままだと Destroy(anchor.gameObject) で 3DGS ごと破棄されるため、先に切り離す
        if (splatRoot != null && splatRoot.parent != null)
            foreach (var anc in _anchors)
                if (anc != null && splatRoot.IsChildOf(anc.transform)) { splatRoot.SetParent(null, true); break; }

        int pending = 0;
        foreach (var a in _anchors)
        {
            if (a == null) continue;
            pending++;
            a.Erase((anchor, ok) => { pending--; });
        }
        float t0 = Time.realtimeSinceStartup;
        while (pending > 0 && Time.realtimeSinceStartup - t0 < timeoutSeconds) yield return null;
        foreach (var a in _anchors) if (a != null) Destroy(a.gameObject);
        _anchors.Clear();
    }

    /// <summary>保存データとアンカーを消す（デバッグ/やり直し用・実験の試行リセット）。
    /// 保留中の保存要求も落とす。残っていると次の試行で前試行の姿勢が保存されてしまう。</summary>
    public void ClearSaved()
    {
        try { if (File.Exists(SavePath)) File.Delete(SavePath); } catch { /* ignore */ }
        StartCoroutine(EraseAllAnchors());
        _hasSaved = false;
        _dirty = false;                 // 未保存の確定姿勢を持ち越さない
        _lastSaveTime = -9999f;
        Debug.Log("[AnchorPersistence] cleared saved data: " + SavePath);
    }
}
