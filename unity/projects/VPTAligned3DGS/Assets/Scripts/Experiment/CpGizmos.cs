using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// コントロールポイントの見た目（球・名前ラベル・付随する点）だけを受け持つ描画ヘルパ。
/// MonoBehaviour ではなく、所有者が <see cref="Build"/> / <see cref="Tick"/> / <see cref="DestroyAll"/>
/// を呼ぶ。
///
/// **なぜ 1 か所にまとめたか。** CP に球とラベルを出す局面は 2 つある
/// （実物側の測量 = <see cref="ControlPointMeasure"/> と、model 側 CP の VR 編集 =
/// <see cref="ControlPointEdit"/>）。それぞれが自前で作ると、同じマーカーの下に球が
/// 2 つぶら下がって z-fight し、色も互いに上書きし合う。作るのは所有者 1 つだけにして、
/// もう一方は色と点の追加だけを借りる。
///
/// **ラベルを splatRoot の子にしない理由**: splatRoot の一様スケールは負（反射・座標規約 §6）。
/// 子にすると文字が鏡像になって読めない。ラベルと付随する点はワールド直下の等倍ルート
/// （"CPGizmos"）にぶら下げ、位置だけ毎フレーム合わせてヨーのみビルボードする。
///
/// 実装は world-space uGUI Canvas ＋ <see cref="Text"/>。TextMesh を使わないのは、
/// 既定フォントマテリアルの旧シェーダが URP で描ける保証が無いため（HUD の方式は実機で実績あり）。
/// </summary>
public class CpGizmos
{
    // ---- 配色（両方の局面で共通の意味を持たせる）----
    /// <summary>まだ触っていない点。</summary>
    public static readonly Color ColPending = Color.cyan;
    /// <summary>いま対象にしている点。</summary>
    public static readonly Color ColCurrent = Color.yellow;
    /// <summary>済んだ点（測量なら実測済み、編集なら置き直し済み）。</summary>
    public static readonly Color ColDone    = Color.green;
    /// <summary>実測した位置（実物側）。model 側の球と混同しないよう橙。</summary>
    public static readonly Color ColTip     = new Color(1f, 0.5f, 0.1f);
    /// <summary>編集前の位置に残す影。動かした量を目で見えるようにする。</summary>
    public static readonly Color ColGhost   = new Color(0.55f, 0.55f, 0.60f);
    /// <summary>仮座標のまま置かれている疑いのある点。</summary>
    public static readonly Color ColSuspect = new Color(1f, 0.25f, 0.25f);
    /// <summary>**まだ確定していない仮置き**（右トリガーで置いた直後 〜 A で確定するまで）。
    /// 確定済み（実測点の橙・置き直し済みの緑）と一目で分かれるよう白にする。</summary>
    public static readonly Color ColPreview = Color.white;

    // ---- 表示名（色だけに頼らず、文字でも区別する）----
    //
    // 1 つの CP（例 CP_01）には座標が 2 つある ── model 上の **Virtual CP**（球）と
    // 実物の **Real CP**（橙の点）。用語は [[ControlPointMeasure]] の class doc。
    // 以前はどちらのラベルも "CP_01" だったので、**見分けが色だけに載っていた**。
    // 測量中は同じ名前の球と点が視界に同時に出るうえ、色の印象はパススルーの背景と
    // 照明で変わる。文字そのものを変えて、色が読めない条件でも読み分けられるようにする。
    //
    // **変えるのはラベルの文字だけ。** GameObject 名・ログ・UDP イベント・JSON の `name` は
    // `CP_01` のまま（過去ログと PC 側の解析スクリプトとの互換 → CLAUDE.md §13）。
    /// <summary>model 上の CP のラベル: `CP_01` → `VCP_01`。</summary>
    public static string VirtualLabel(string cpName) => Tag("V", cpName);

    /// <summary>実物側の CP のラベル: `CP_01` → `RCP_01`。</summary>
    public static string RealLabel(string cpName) => Tag("R", cpName);

    private static string Tag(string kind, string cpName)
    {
        string n = cpName ?? "";
        if (n.Length == 0) return kind;
        // 命名規約どおりの "CP_01" は "VCP_01" に。規約外の名前は元の文字列を潰さないよう
        // 区切って前置するだけにする（"V:foo"）。
        return n.StartsWith("CP", System.StringComparison.OrdinalIgnoreCase) ? kind + n : kind + ":" + n;
    }

    // ラベルの Canvas 解像度。文字高さ = LabelPxH px を labelHeightM[m] に写す。
    private const int LabelPxH = 64;
    private const int LabelPxW = 320;

    // ---- 見かけの大きさ（距離補正）----
    //
    // **なぜ実寸固定をやめたか。** 元は 3cm の球・5cm の文字をワールド実寸で出していた。
    // 先端を当てる距離（〜0.5m）ではちょうどよいが、CP は部屋じゅうに散らばっていて、
    // 実際には 3m 前後離れたところから探すことになる。3m では 3cm の球は **0.5°**
    // （Quest の画素で 10px 前後）にしかならず、5cm の文字は判読できない。
    // スプラットの雑然とした背景に埋もれ、**点が出ていないように見える**。
    // 実機で「CP_01（黄・いま対象）だけ見えて CP_02〜06（シアン）は見えない」と報告されたのが
    // これで、実際には 6 点とも生成・描画されていた（オクルージョンでもないことを確認済み。
    // スプラット表示 ON/OFF で該当画素の色が一致する）。黄だけが背景から拾えていた。
    //
    // 近距離では従来の実寸を保ち（先端を当てるときの精度を落とさない）、遠いぶんだけ
    // 比例して大きくして**見かけの大きさを一定に保つ**。
    /// <summary>この距離[m]までは従来どおりの実寸。これより遠い点は比例して大きくする。</summary>
    private const float RefDistM = 1.0f;
    /// <summary>拡大の上限倍率。部屋の対角より遠くても際限なく大きくしない。</summary>
    private const float MaxGrowth = 6f;
    /// <summary>いま対象にしている点だけ更に大きくする倍率。</summary>
    private const float CurrentBoost = 1.6f;

    private readonly float _radius;
    private readonly float _labelH;

    private Transform _root;                       // ワールド直下・等倍
    private ControlPointMarker[] _markers;
    private Renderer[] _spheres;
    private Transform[] _labels;
    private Transform _head;
    private readonly List<Layer> _layers = new List<Layer>();
    // コードで作ったマテリアルは GameObject を消しても道連れにならないので、自分で片付ける。
    private readonly List<Material> _materials = new List<Material>();

    private int       _current = -1;               // いま対象にしている点（棒を出す点）
    private Transform _stem;                       // その点に立てる縦の棒
    private Renderer  _stemR;

    public CpGizmos(float radiusM, float labelHeightM)
    {
        _radius = radiusM;
        _labelH = labelHeightM;
    }

    public bool  Built  => _spheres != null;
    public int   Count  => _markers != null ? _markers.Length : 0;
    public float Radius => _radius;

    // ---------------- シェーダとマテリアル ----------------
    //
    // **なぜ `Shader.Find` を裸で呼んではいけないか。** プレイヤービルドは「どのシーン／アセットからも
    // 参照されていないシェーダ」をビルドから削除する（stripping）。球のマテリアルは
    // `new Material(Shader.Find(...))` とコードで作るだけなので、ビルド時の参照解析からは見えない。
    // 結果、**Editor では正しく色が付くのに実機だけ `Shader.Find` が null を返し**、
    // シェーダ無しのマテリアル ＝ Unity のエラーシェーダ ＝ **ピンクの球**になる（実機で実際に発生）。
    //
    // 恒久対策は `Project Settings > Graphics > Always Included Shaders` への登録（`Unlit/Color` を追加済み）。
    // ラベルが実機で読めていたのは、uGUI が使う `UI/Default` が既定でこのリストに入っていたためで、
    // 球だけが漏れていた。ここでは登録漏れが再発したときに**黙ってピンクにならない**よう、
    // 候補を順に試し、全滅したら理由と直し方を添えて `LogError` する。
    private static readonly string[] ShaderCandidates =
    {
        "Unlit/Color",                     // 既定。URP でも問題なく描ける単純な不透明シェーダ
        "Universal Render Pipeline/Unlit", // 予備
        "Sprites/Default",                 // 最後の砦（既定で Always Included に入っている）
    };

    private static Shader _shader;
    private static bool   _shaderResolved;

    private static Shader GizmoShader()
    {
        if (_shaderResolved) return _shader;
        _shaderResolved = true;
        foreach (var name in ShaderCandidates)
        {
            _shader = Shader.Find(name);
            if (_shader != null)
            {
                if (name != ShaderCandidates[0])
                    Debug.LogWarning("[CpGizmos] \"" + ShaderCandidates[0] + "\" が見つからないので \""
                                     + name + "\" で代用する（ビルドで剥がされた可能性）");
                return _shader;
            }
        }
        Debug.LogError("[CpGizmos] マーカーの球に使えるシェーダが 1 つも無い。"
                       + "Project Settings > Graphics > Always Included Shaders に \"Unlit/Color\" を"
                       + "追加すること。このままでは球がピンク（エラーシェーダ）になり、"
                       + "「マーカーが出ていない」と誤読される。");
        return null;
    }

    /// <summary>ギズモ用のマテリアルを作る。片付けは <see cref="DestroyAll"/> がまとめて行う。</summary>
    private Material NewMat(Color c)
    {
        var sh = GizmoShader();
        if (sh == null) return null;
        var m = new Material(sh) { color = c };
        _materials.Add(m);
        return m;
    }

    private static bool _drawFailed;

    /// <summary>ドメインリロードを切って Play すると static は前回の値のまま残る。
    /// 「1 回だけ出す」エラーが 2 回目以降の Play で黙り、シェーダの解決結果も古いまま
    /// 使い回されるので、Play のたびに捨てる。</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        _drawFailed = false;
        _shaderResolved = false;
        _shader = null;
    }

    /// <summary>ギズモの描画を、呼び出し側の状態遷移から切り離して実行する。
    ///
    /// **なぜ try/catch が要るか（実機で起きたこと）。** 上の `Unlit/Color` がビルドから剥がされ、
    /// `Shader.Find` が null を返し、`new Material(null)` が ArgumentNullException を投げた
    /// （Editor では再現しない ── Editor には常に全ビルトインシェーダがある）。その呼び出しが
    /// <see cref="ControlPointMeasure.Record"/> の「次の点へ進む」処理より**前**にあったため、
    /// 記録のたびに Update() ごと抜け、**測量が CP_01 から永久に進まなかった**
    /// （7 回記録して 7 回とも CP_01、`cp_survey_done` が出ず HUD は "CP not surveyed" のまま）。
    /// 同じ理由で編集モードも CP_01 から進まなかった。
    ///
    /// シェーダ側は直したが、**「見た目の失敗が測定機能を止める」構造そのものを残さない。**
    /// ここを通せば、描画が落ちても球や点が出ないだけで、測量・編集は最後まで進む。
    /// 握り潰さず 1 回だけ LogError するので、原因は Console に残る。</summary>
    public static void Draw(System.Action draw)
    {
        try { draw(); }
        catch (System.Exception e)
        {
            if (_drawFailed) return;          // 毎フレーム出して他のログを埋めない
            _drawFailed = true;
            Debug.LogError("[CpGizmos] ギズモの描画に失敗した（測量・編集はそのまま続行する）: " + e);
        }
    }

    // ---------------- 球とラベル ----------------

    /// <summary>マーカー配列に対応する球とラベルを作る（既にあれば作り直す）。</summary>
    public void Build(ControlPointMarker[] markers)
    {
        DestroyAll();
        _markers = markers;
        if (_markers == null || _markers.Length == 0) return;

        EnsureRoot();
        _spheres = new Renderer[_markers.Length];
        _labels  = new Transform[_markers.Length];
        for (int i = 0; i < _markers.Length; i++)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "CPSphere_" + _markers[i].name;
            UnityEngine.Object.Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(_markers[i].transform, false);
            go.transform.localPosition = Vector3.zero;
            _spheres[i] = go.GetComponent<Renderer>();
            _spheres[i].material = NewMat(ColPending);

            // ラベルは球の子にしない（splatRoot が負スケール ＝ 文字が鏡像になる）。
            // 球は必ず model 側の点なので、ラベルは常に "VCP_.."（→ <see cref="VirtualLabel"/>）。
            _labels[i] = MakeLabel(VirtualLabel(_markers[i].name), ColPending);
            SetColor(i, ColPending);
        }
    }

    public void DestroyAll()
    {
        foreach (var l in _layers) l.ClearAll();
        if (_spheres != null)
            foreach (var r in _spheres) if (r != null) UnityEngine.Object.Destroy(r.gameObject);
        _spheres = null;
        _labels = null;
        _markers = null;
        if (_root != null) UnityEngine.Object.Destroy(_root.gameObject);
        _root = null;
        _stem = null; _stemR = null;   // _root の子なので一緒に消えている
        // マテリアルは GameObject の破棄では消えない（明示的に消さないと溜まる）
        foreach (var m in _materials) if (m != null) UnityEngine.Object.Destroy(m);
        _materials.Clear();
        // _current は消さない。作り直しの前後で「いま何点目か」は変わらないため。
    }

    /// <summary>いま対象にしている点（少し大きく描き、縦の棒を立てる点）。-1 で無し。
    /// 所有者が列を進めるたびに呼ぶ。**部屋の反対側にあっても列のどこを見ればよいか
    /// 分かるようにするため**で、球の大きさだけでは背景に紛れる。</summary>
    public void SetCurrent(int i) => _current = i;

    public void SetVisible(bool v)
    {
        if (_root != null && _root.gameObject.activeSelf != v) _root.gameObject.SetActive(v);
        if (_spheres == null) return;
        foreach (var r in _spheres) if (r != null) r.enabled = v;
    }

    /// <summary>球とその名前ラベルをまとめて着色する。</summary>
    public void SetColor(int i, Color c)
    {
        if (_spheres == null || i < 0 || i >= _spheres.Length) return;
        // sharedMaterial の null チェックは必須。null のまま .material を触ると
        // シェーダ無しのマテリアルが生えてピンクになる（上の GizmoShader の解説を参照）。
        if (_spheres[i] != null && _spheres[i].sharedMaterial != null) _spheres[i].material.color = c;
        if (_labels != null && _labels[i] != null)
        {
            var txt = _labels[i].GetComponent<Text>();
            if (txt != null) txt.color = c;
        }
    }

    /// <summary>頭からの距離に応じた拡大率（<see cref="RefDistM"/> の解説を参照）。
    /// 頭が取れないときは 1（＝従来どおりの実寸）。</summary>
    private float GrowthAt(Vector3 world) =>
        _head == null ? 1f
        : Mathf.Clamp(Vector3.Distance(world, _head.position) / RefDistM, 1f, MaxGrowth);

    /// <summary>球の中心からラベルまでの高さ。球と一緒に拡大する。</summary>
    private float LabelOffset(float f) => (_radius * 2f + _labelH) * f;

    /// <summary>球の見かけの大きさとラベルの位置を毎フレーム合わせる。
    ///
    /// 球は splatRoot（＝マーカーの親）の子なので、シーンを動かす／スケールが変わると
    /// ワールドでの見かけも変わる。ここで打ち消して、位置合わせの状態に依らず
    /// 常に同じ大きさで見えるようにする。あわせて頭からの距離でも補正し、
    /// **どこから見ても同じ見かけの大きさ**になるようにする（<see cref="RefDistM"/>）。</summary>
    public void Tick(Transform head)
    {
        _head = head;
        if (_markers == null || _spheres == null) return;

        for (int i = 0; i < _markers.Length; i++)
        {
            if (_markers[i] == null) continue;
            var mt = _markers[i].transform;
            float f = GrowthAt(mt.position) * (i == _current ? CurrentBoost : 1f);

            if (_spheres[i] != null)
            {
                float p = Mathf.Abs(mt.lossyScale.x);
                float s = _radius * 2f * f / (p > 1e-4f ? p : 1f);
                _spheres[i].transform.localScale = new Vector3(s, s, s);
            }
            if (_labels[i] != null)
                PlaceLabel(_labels[i], mt.position + Vector3.up * LabelOffset(f), f);
        }

        TickStem();
        foreach (var l in _layers) l.Tick();
    }

    /// <summary>いま対象にしている点に縦の棒を立てる。球とラベルを繋ぐ「ピン」の形にして、
    /// 部屋の反対側からでも「次はここ」が拾えるようにする。</summary>
    private void TickStem()
    {
        bool has = _current >= 0 && _markers != null && _current < _markers.Length
                   && _markers[_current] != null && _spheres[_current] != null;
        if (!has)
        {
            if (_stem != null && _stem.gameObject.activeSelf) _stem.gameObject.SetActive(false);
            return;
        }

        EnsureStem();
        if (!_stem.gameObject.activeSelf) _stem.gameObject.SetActive(true);

        Vector3 pos = _markers[_current].transform.position;
        float f = GrowthAt(pos) * CurrentBoost;
        float h = LabelOffset(f);                 // 球の中心からラベルまで
        _stem.position   = pos + Vector3.up * (h * 0.5f);
        _stem.rotation   = Quaternion.identity;
        // Unity の円柱は高さ 2・直径 1。棒は球より十分細く。
        _stem.localScale = new Vector3(_radius * 0.45f * f, h * 0.5f, _radius * 0.45f * f);
        if (_stemR.sharedMaterial != null && _spheres[_current].sharedMaterial != null)
            _stemR.material.color = _spheres[_current].material.color;
    }

    private void EnsureStem()
    {
        if (_stem != null) return;
        EnsureRoot();                             // ワールド直下・等倍（負スケールの影響を受けない）
        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = "CPStem";
        UnityEngine.Object.Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(_root, false);
        _stem  = go.transform;
        _stemR = go.GetComponent<Renderer>();
        _stemR.material = NewMat(ColCurrent);
    }

    /// <summary>ラベルを指定位置に置き、頭のほうへ向ける（ヨーのみのビルボード）。
    /// 大きさも距離補正する（<paramref name="f"/> は <see cref="GrowthAt"/> の倍率）。</summary>
    private void PlaceLabel(Transform t, Vector3 pos, float f)
    {
        t.position = pos;
        // 親（_root とその子）は等倍なので localScale がそのままワールドの実寸になる
        t.localScale = Vector3.one * (_labelH * f / LabelPxH);
        if (_head == null) return;
        Vector3 away = pos - _head.position;
        away.y = 0f;                                  // 傾けない。首を傾げても水平に読める
        if (away.sqrMagnitude > 1e-6f) t.rotation = Quaternion.LookRotation(away, Vector3.up);
    }

    // ---------------- 付随する点（実測点・ゴースト）----------------

    /// <summary>球とは別に出す点の一群を作る（実測点・編集前のゴーストなど）。
    /// 所有者が 1 回だけ作り、以後は同じ Layer を使い回す。</summary>
    public Layer NewLayer(string name, Color color, float radiusMul)
    {
        var l = new Layer(this, name, color, radiusMul);
        _layers.Add(l);
        return l;
    }

    /// <summary>CP と同数のスロットを持つ点の集合。スロットはワールド固定にも、
    /// model ローカル固定（＝シーンを動かすと一緒に動く）にもできる。</summary>
    public class Layer
    {
        private readonly CpGizmos _g;
        private readonly string   _name;
        private readonly Color    _color;
        private readonly float    _mul;

        private GameObject[] _go;
        private Transform[]  _dot;       // 点の実体（大きさを毎フレーム距離補正する）
        private Transform[]  _label;
        private Transform[]  _anchor;    // 非 null なら model ローカル固定
        private Vector3[]    _pos;       // _anchor があればローカル座標、無ければワールド座標

        internal Layer(CpGizmos g, string name, Color color, float radiusMul)
        {
            _g = g; _name = name; _color = color; _mul = radiusMul;
        }

        private bool EnsureSize()
        {
            int n = _g.Count;
            if (n == 0) return false;
            if (_go != null && _go.Length == n) return true;
            ClearAll();
            _go = new GameObject[n]; _label = new Transform[n]; _dot = new Transform[n];
            _anchor = new Transform[n]; _pos = new Vector3[n];
            return true;
        }

        /// <summary>ワールド座標に固定した点を置く（実測値のようにワールドで意味を持つ値）。
        /// <paramref name="text"/> はラベルに出す文字。CP 名をそのまま渡さず
        /// <see cref="RealLabel"/> / <see cref="VirtualLabel"/> を通すこと（球と同名になると
        /// 見分けが色だけに戻る）。</summary>
        public void SetWorld(int i, Vector3 world, string text) => Set(i, null, world, text);

        /// <summary>model ローカル座標に固定した点を置く（シーンを動かすと一緒に動く）。
        /// <paramref name="text"/> は <see cref="SetWorld"/> と同じ。</summary>
        public void SetLocal(int i, Transform parent, Vector3 local, string text) => Set(i, parent, local, text);

        private void Set(int i, Transform parent, Vector3 p, string text)
        {
            if (!EnsureSize() || i < 0 || i >= _go.Length) return;
            Clear(i);
            _g.EnsureRoot();

            var root = new GameObject("CP" + _name + "_" + text);
            root.transform.SetParent(_g._root, false);

            var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            dot.name = "Dot";
            UnityEngine.Object.Destroy(dot.GetComponent<Collider>());
            dot.transform.SetParent(root.transform, false);
            dot.transform.localScale = Vector3.one * (_g._radius * _mul);
            dot.GetComponent<Renderer>().material = _g.NewMat(_color);

            var lbl = _g.MakeLabel(text, _color);
            lbl.SetParent(root.transform, true);

            _go[i] = root; _dot[i] = dot.transform; _label[i] = lbl;
            _anchor[i] = parent; _pos[i] = p;
            root.transform.position = WorldOf(i);
        }

        public void Clear(int i)
        {
            if (_go == null || i < 0 || i >= _go.Length) return;
            if (_go[i] != null) UnityEngine.Object.Destroy(_go[i]);   // 点とラベルは子なので一緒に消える
            _go[i] = null; _dot[i] = null; _label[i] = null; _anchor[i] = null;
        }

        public void ClearAll()
        {
            if (_go == null) return;
            for (int i = 0; i < _go.Length; i++) Clear(i);
        }

        public bool Has(int i) => _go != null && i >= 0 && i < _go.Length && _go[i] != null;

        private Vector3 WorldOf(int i) =>
            _anchor[i] != null ? _anchor[i].TransformPoint(_pos[i]) : _pos[i];

        internal void Tick()
        {
            if (_go == null) return;
            for (int i = 0; i < _go.Length; i++)
            {
                if (_go[i] == null) continue;
                Vector3 w = WorldOf(i);
                _go[i].transform.position = w;
                // 球と同じ理由で距離補正する（実寸固定だと数 m 先で埋もれる）
                float f = _g.GrowthAt(w);
                if (_dot[i] != null) _dot[i].localScale = Vector3.one * (_g._radius * _mul * f);
                if (_label[i] != null)
                    _g.PlaceLabel(_label[i], w + Vector3.up * ((_g._radius + _g._labelH) * f), f);
            }
        }
    }

    // ---------------- ラベルの実体 ----------------

    private void EnsureRoot()
    {
        if (_root != null) return;
        // **ワールド直下・等倍**に置く。splatRoot の子にすると負スケール（反射）で文字が裏返る。
        _root = new GameObject("CPGizmos").transform;
    }

    /// <summary>ワールドに浮かぶ 1 行ラベルを作る。</summary>
    private Transform MakeLabel(string text, Color color)
    {
        EnsureRoot();
        var go = new GameObject("CPLabel_" + text);
        go.transform.SetParent(_root, false);

        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var rt = canvas.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(LabelPxW, LabelPxH);
        // 文字高さ（= LabelPxH px）が labelHeightM になるよう 1px の実寸を決める
        go.transform.localScale = Vector3.one * (_labelH / LabelPxH);

        var t = go.AddComponent<Text>();
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = LabelPxH;
        t.color = color;
        t.alignment = TextAnchor.MiddleCenter;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.text = text;
        return go.transform;
    }
}
