using GaussianSplatting.Runtime;
using UnityEngine;

/// <summary>
/// GaussianSplats の表示制御（最小版）。
/// 旧実験用のフェード/スケール拡大縮小の演出は撤去した。
/// スプラットの位置・回転・スケール（= 位置合わせ結果 T_{m→w}）は
/// <see cref="AlignmentManager"/> がルート Transform に対して設定する。
/// 本コンポーネントは可視化（グローバル不透明度）だけを担当する。
/// </summary>
public class GaussianSplatController : MonoBehaviour
{
    [Header("References")]
    [Tooltip("GaussianSplatRenderer を持つルート GameObject。")]
    [SerializeField] private GameObject gaussianSplatsObject;

    [Tooltip("GaussianSplatRenderer。未設定なら gaussianSplatsObject から自動取得。")]
    [SerializeField] private GaussianSplatRenderer splatRenderer;

    [Header("Identity")]
    [Tooltip("永続化(P4)の保存キー。空なら 3DGS アセット名を自動採用（ハイブリッド）。" +
             "PC 側 cv.json の scene 名と揃えると分かりやすい。")]
    [SerializeField] private string modelId = "";

    // 改変パッケージのシェーダが参照するグローバル不透明度プロパティ。
    private const string GlobalOpacityProp = "_GaussianSplatGlobalOpacity";

    public GaussianSplatRenderer Renderer => splatRenderer;

    // 境界ボックスが取れないとき（アセット未割当）の代わり。部屋 1 つぶんの大きさ
    private static readonly Vector3 FallbackBoundsMin = new Vector3(-5f, -2f, -5f);
    private static readonly Vector3 FallbackBoundsMax = new Vector3(5f, 2f, 5f);

    /// <summary>3DGS アセットの境界ボックス（モデル座標）。取れなければ（ctrl が無い・アセット未割当）
    /// 部屋 1 つぶんの代わりの箱を入れて false を返す。
    /// 2 つの姿勢の「見た目の差」（境界ボックス 8 隅の移動量）を測るのに使う（AlignmentManager・AnchorPersistence）。</summary>
    public static bool GetModelBounds(GaussianSplatController ctrl, out Vector3 mn, out Vector3 mx)
    {
        var asset = ctrl != null && ctrl.splatRenderer != null ? ctrl.splatRenderer.asset : null;
        if (asset != null) { mn = asset.boundsMin; mx = asset.boundsMax; return true; }
        mn = FallbackBoundsMin; mx = FallbackBoundsMax;
        return false;
    }
    public Transform SplatRoot => gaussianSplatsObject != null ? gaussianSplatsObject.transform : null;

    /// <summary>永続化の識別子（P4）。明示指定があればそれ、無ければ 3DGS アセット名、それも無ければ "default"。</summary>
    public string ModelId
    {
        get
        {
            if (!string.IsNullOrEmpty(modelId)) return modelId;
            var asset = splatRenderer != null ? splatRenderer.asset : null;
            return asset != null && !string.IsNullOrEmpty(asset.name) ? asset.name : "default";
        }
    }

    private void Awake()
    {
        if (splatRenderer == null && gaussianSplatsObject != null)
            splatRenderer = gaussianSplatsObject.GetComponentInChildren<GaussianSplatRenderer>();
        if (splatRenderer == null)
            Debug.LogWarning("[GaussianSplatController] GaussianSplatRenderer が見つかりません。");

        // グローバル不透明度の初期値。Play を跨いで 0 のまま残ることがあるので明示する。
        //
        // **Start ではなく Awake で行う。** 起動時の表示/非表示を決めるのは AlignmentManager
        // （startHidden）で、あちらは Start で SetSplatVisible を呼ぶ。Start どうしの実行順は
        // 不定なので、ここが Start だと「非表示にした直後に 1 で上書き」が起こりうる。
        // Awake は必ずすべての Start より前に走るため、この順序問題が消える。
        SetGlobalOpacity(1f);
    }

    public void Show() => SetGlobalOpacity(1f);
    public void Hide() => SetGlobalOpacity(0f);

    /// <summary>グローバル不透明度を中間値で設定する（0..1）。表示のフェードイン用。
    /// 表示状態の管理は AlignmentManager が持つので、直接呼ぶのはそこからだけにする。</summary>
    public void SetOpacity(float value) => SetGlobalOpacity(Mathf.Clamp01(value));

    private static void SetGlobalOpacity(float value) => Shader.SetGlobalFloat(GlobalOpacityProp, value);
}
