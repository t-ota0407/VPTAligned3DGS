using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 評価 E1 用コントロールポイントの配置ツール（Tools &gt; VPTAligned3DGS &gt; Control Points）。
///
/// やること:
///   - splatRoot 配下に CP_01.. を生成・削除・連番振り直し（名前順が測量順になる）
///   - Scene ビューでスプラットを見ながらドラッグして特徴点に合わせる（通常の Transform 操作）
///   - 現在のシーンの CP を JSON に書き出し／読み込み（バックアップ・シーン間の持ち回り用）
///
/// **ビルドするシーンが model 側の正**。実機側は、ここで置いた点に対応する実物を
/// 実験前に 1 回だけ測量する（<see cref="ControlPointMeasure"/>）。
///
/// ただしマウス配置は視線方向の奥行きが取れないので、実機の VR 内で置き直せるようにしてある
/// （<see cref="ControlPointEdit"/>・**位置合わせ前のみ**）。その結果は PC の
/// `server/received/controlpoints_&lt;modelId&gt;.json` に届くので、下の
/// 「JSON を読み込んで反映」で取り込んでシーンへ恒久化する。実機が勝手にシーンを
/// 書き換えることはない（取り込みは必ずこの明示操作を通す）。
///
/// 座標は必ず splatRoot のローカル（= 3DGS モデル座標）で扱う。ワールドで持つと位置合わせを
/// やり直すたびに基準点が動き、評価が成立しないため（ControlPointStore のコメント参照）。
/// </summary>
public class ControlPointToolWindow : EditorWindow
{
    private Transform _splatRoot;
    private Transform _container;
    private string _jsonPath = "";
    private Vector2 _scroll;

    private const string ContainerName = "ControlPoints";

    [MenuItem("Tools/VPTAligned3DGS/Control Points")]
    public static void Open()
    {
        var w = GetWindow<ControlPointToolWindow>("Control Points");
        w.minSize = new Vector2(380, 420);
        w.AutoFind();
    }

    private void OnEnable() => AutoFind();

    private void AutoFind()
    {
        if (_splatRoot == null)
        {
            var ctrl = Object.FindFirstObjectByType<GaussianSplatController>();
            if (ctrl != null) _splatRoot = ctrl.SplatRoot;
        }
        if (_splatRoot != null && _container == null)
            _container = _splatRoot.Find(ContainerName);
    }

    private void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        EditorGUILayout.HelpBox(
            "評価 E1 の基準点を 3DGS 上に置きます。\n" +
            "机の角・棚の縁など、実物側でも一意に指させる特徴を選び、部屋全体に散らしてください。\n" +
            "実機での測量順は名前順（CP_01, CP_02, ...）です。", MessageType.Info);

        _splatRoot = (Transform)EditorGUILayout.ObjectField("splatRoot", _splatRoot, typeof(Transform), true);
        if (_splatRoot == null)
        {
            EditorGUILayout.HelpBox("splatRoot が見つかりません。GaussianSplatController のあるシーンを開いてください。",
                                    MessageType.Warning);
            EditorGUILayout.EndScrollView();
            return;
        }

        var markers = CurrentMarkers();
        EditorGUILayout.Space();
        EditorGUILayout.LabelField($"現在の CP: {markers.Length} 点", EditorStyles.boldLabel);

        // 危険な状態を先に知らせる: ダミー座標のまま実験すると測定が無意味になる。
        // **1 点でも残っていれば警告する。** 実機側の測量は実験前の 1 回きりなので、
        // 仮座標が混ざったまま測ると、その点の誤差だけが全試行で無意味な値になり、
        // しかも「1 点だけ誤差が大きい」という見分けのつきにくい形で出てくる。
        var placeholders = markers.Where(LooksLikePlaceholder).Select(m => m.name).ToArray();
        if (placeholders.Length > 0)
        {
            EditorGUILayout.HelpBox(
                $"{string.Join(", ", placeholders)} が仮座標（原点付近・0.1 刻み）のままに見えます。\n" +
                "実物の特徴点に合わせてください。合っていない点は測量しても意味のある誤差になりません。",
                MessageType.Error);
        }

        foreach (var m in markers)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(m.name, GUILayout.Width(70));
            Vector3 lp = m.transform.localPosition;
            EditorGUILayout.LabelField($"({lp.x:F3}, {lp.y:F3}, {lp.z:F3})");
            if (GUILayout.Button("選択", GUILayout.Width(50)))
            {
                Selection.activeGameObject = m.gameObject;
                SceneView.lastActiveSceneView?.FrameSelected();
            }
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("配置", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("CP を 1 点追加")) AddMarker();
        if (GUILayout.Button("6 点まで作る")) { while (CurrentMarkers().Length < 6) AddMarker(); }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("連番を振り直す")) Renumber();
        if (GUILayout.Button("選択中の CP を削除")) DeleteSelected();
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.HelpBox(
            "追加した CP は Scene ビューで移動ツール（W）を使い、スプラットの特徴点にぴったり合わせてください。" +
            "細かく合わせるには Scene ビューで対象を Frame（F）して寄るのが確実です。", MessageType.None);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("JSON の書き出し / 読み込み", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        _jsonPath = EditorGUILayout.TextField("JSON", _jsonPath);
        if (GUILayout.Button("…", GUILayout.Width(28)))
        {
            string p = EditorUtility.OpenFilePanel("controlpoints json", "", "json");
            if (!string.IsNullOrEmpty(p)) _jsonPath = p;
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("JSON を読み込んで反映")) ImportJson();
        if (GUILayout.Button("JSON へ書き出し")) ExportJson();
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.HelpBox(
            "読み込むと、名前が一致する CP の位置が JSON の値（splatRoot ローカル）で置き換わります。\n" +
            "実機の VR 内で置き直した結果もここから取り込めます（位置合わせ前のみ編集可）:\n" +
            "  server/received/controlpoints_<modelId>.json",
            MessageType.None);

        EditorGUILayout.EndScrollView();
    }

    // ---------------- 操作 ----------------

    private ControlPointMarker[] CurrentMarkers()
    {
        if (_splatRoot == null) return new ControlPointMarker[0];
        return _splatRoot.GetComponentsInChildren<ControlPointMarker>(true)
                         .OrderBy(m => m.name).ToArray();
    }

    private Transform EnsureContainer()
    {
        if (_container != null) return _container;
        _container = _splatRoot.Find(ContainerName);
        if (_container == null)
        {
            var go = new GameObject(ContainerName);
            Undo.RegisterCreatedObjectUndo(go, "Create ControlPoints");
            go.transform.SetParent(_splatRoot, false);
            _container = go.transform;
        }
        return _container;
    }

    private void AddMarker()
    {
        var parent = EnsureContainer();
        int n = CurrentMarkers().Length + 1;
        var go = new GameObject($"CP_{n:D2}");
        Undo.RegisterCreatedObjectUndo(go, "Add Control Point");
        go.transform.SetParent(parent, false);
        go.AddComponent<ControlPointMarker>();

        // Scene ビューのカメラ前方に置く。原点に積み重なると掴めないため。
        var sv = SceneView.lastActiveSceneView;
        if (sv != null && sv.camera != null)
            go.transform.position = sv.camera.transform.position + sv.camera.transform.forward * 1.5f;

        Selection.activeGameObject = go;
        EditorUtility.SetDirty(go);
    }

    private void Renumber()
    {
        var markers = CurrentMarkers();
        Undo.RecordObjects(markers.Select(m => (Object)m.gameObject).ToArray(), "Renumber Control Points");
        for (int i = 0; i < markers.Length; i++)
        {
            markers[i].name = $"CP_{i + 1:D2}";
            EditorUtility.SetDirty(markers[i].gameObject);
        }
        Debug.Log($"[ControlPointTool] {markers.Length} 点を CP_01.. に振り直しました");
    }

    private void DeleteSelected()
    {
        foreach (var go in Selection.gameObjects)
            if (go.GetComponent<ControlPointMarker>() != null)
                Undo.DestroyObjectImmediate(go);
    }

    private void ImportJson()
    {
        if (string.IsNullOrEmpty(_jsonPath)) { Debug.LogWarning("[ControlPointTool] JSON パスが未指定です"); return; }
        var store = ControlPointStore.Load(_jsonPath, out string err);
        if (store == null) { Debug.LogError("[ControlPointTool] 読み込み失敗: " + err); return; }

        var parent = EnsureContainer();
        var byName = CurrentMarkers().ToDictionary(m => m.name, m => m);
        int updated = 0, created = 0;

        foreach (var e in store.points)
        {
            if (string.IsNullOrEmpty(e.name)) continue;
            if (byName.TryGetValue(e.name, out var m))
            {
                Undo.RecordObject(m.transform, "Import Control Points");
                m.transform.localPosition = e.Position;
                EditorUtility.SetDirty(m.transform);
                updated++;
            }
            else
            {
                var go = new GameObject(e.name);
                Undo.RegisterCreatedObjectUndo(go, "Import Control Points");
                go.transform.SetParent(parent, false);
                go.AddComponent<ControlPointMarker>();
                go.transform.localPosition = e.Position;
                created++;
            }
        }
        Debug.Log($"[ControlPointTool] 取り込み完了: 更新 {updated} / 新規 {created}（modelId={store.modelId}）");
    }

    private void ExportJson()
    {
        var markers = CurrentMarkers();
        if (markers.Length == 0) { Debug.LogWarning("[ControlPointTool] 書き出す CP がありません"); return; }

        var ctrl = Object.FindFirstObjectByType<GaussianSplatController>();
        string modelId = ctrl != null ? ctrl.ModelId : "default";

        string path = _jsonPath;
        if (string.IsNullOrEmpty(path))
        {
            path = EditorUtility.SaveFilePanel("controlpoints を書き出し", "",
                                               ControlPointStore.FileName(modelId), "json");
            if (string.IsNullOrEmpty(path)) return;
            _jsonPath = path;
        }

        var store = new ControlPointStore { modelId = modelId };
        foreach (var m in markers)
            store.points.Add(ControlPointStore.Entry.From(m.name, m.transform.localPosition));

        if (ControlPointStore.Save(path, store, out string err))
            Debug.Log($"[ControlPointTool] 書き出し完了: {path}（{store.points.Count} 点）");
        else
            Debug.LogError("[ControlPointTool] 書き出し失敗: " + err);
    }

    /// <summary>初期状態のダミー座標（±0.3 等のきりの良い仮値）っぽいかの簡易判定。</summary>
    private static bool LooksLikePlaceholder(ControlPointMarker m)
    {
        // 仮値は座標成分が 0.1 刻みの小さな値ばかりで、原点から 1m 以内に固まる。
        // 実際の部屋（数 m 規模）の特徴点に合わせた CP はこの条件を満たさない。
        Vector3 p = m.transform.localPosition;
        if (p.magnitude > 1.0f) return false;
        foreach (float v in new[] { p.x, p.y, p.z })
            if (Mathf.Abs(v * 10f - Mathf.Round(v * 10f)) > 1e-3f) return false;
        return true;
    }
}
