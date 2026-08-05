using UnityEngine;

/// <summary>
/// アバター（VRIK で身体に重畳表示される Y_Bot）の表示/非表示スイッチ。
///
/// Inspector の <b>Show Avatar</b> チェックボックスで切り替える。Editor では
/// チェックを操作した瞬間に反映され（Play 中・Edit 中どちらでも）、その状態はシーンに保存される。
/// 実行時は <see cref="SetVisible"/> / <see cref="Toggle"/> から切り替えられる。
///
/// 評価実験との関係:
///   E1 では実物のコントロールポイントをコントローラ先端で触る。このとき仮想のアバターの腕が
///   実際の腕に重なって描画されると、接触点そのものが隠れて狙いが甘くなる。測定値に効くので、
///   E1 の計測中はアバターを消しておくのが無難。
///
/// 非表示の方法（<see cref="hideMode"/>）:
///   - RenderersOnly    : Renderer だけ切る。IK は回り続けるので再表示が即座で姿勢も崩れない（既定）。
///   - DeactivateObject : GameObject ごと切る。IK/Animator も止まるので負荷は下がるが、
///                        再表示直後の 1 フレームは姿勢が落ち着かないことがある。
/// </summary>
[DisallowMultipleComponent]
public class AvatarVisibility : MonoBehaviour
{
    public enum HideMode
    {
        RenderersOnly,      // Renderer のみ無効化（IK は動き続ける）
        DeactivateObject,   // GameObject ごと非アクティブ化
    }

    [Header("Target")]
    [Tooltip("対象のアバター ルート。未設定ならこのコンポーネントが付いた GameObject。")]
    [SerializeField] private GameObject avatarRoot;

    [Header("Switch")]
    [Tooltip("アバターを表示するか。ここのチェックで切り替える（Editor では即時反映・シーンに保存される）。")]
    [SerializeField] private bool showAvatar = true;

    [Tooltip("非表示にする方法。\n" +
             "RenderersOnly: Renderer だけ切る（IK は回り続ける。再表示が即座・既定）\n" +
             "DeactivateObject: GameObject ごと切る（負荷は下がるが再表示直後に姿勢が落ち着かないことがある）")]
    [SerializeField] private HideMode hideMode = HideMode.RenderersOnly;

    /// <summary>現在アバターが表示されているか。</summary>
    public bool IsVisible => showAvatar;

    private void Awake()  => Apply();
    private void OnEnable() => Apply();

    /// <summary>表示/非表示を設定する（実行時 API）。</summary>
    public void SetVisible(bool visible)
    {
        showAvatar = visible;
        Apply();
    }

    /// <summary>表示/非表示を反転する（HUD ボタン等から）。</summary>
    [ContextMenu("Toggle Avatar")]
    public void Toggle() => SetVisible(!showAvatar);

    private GameObject Root => avatarRoot != null ? avatarRoot : gameObject;

    private void Apply()
    {
        var root = Root;
        if (root == null) return;

        if (hideMode == HideMode.DeactivateObject)
        {
            // Renderer 側は触った形跡を残さない（モードを切り替えても破綻しないように戻す）
            SetRenderers(root, true);
            if (root.activeSelf != showAvatar) root.SetActive(showAvatar);
        }
        else
        {
            if (!root.activeSelf) root.SetActive(true);   // 前のモードで切られていた場合に戻す
            SetRenderers(root, showAvatar);
        }
    }

    private static void SetRenderers(GameObject root, bool enabled)
    {
        // 非アクティブな子も含めて取得する。取りこぼすと部分的に出たままになる。
        var renderers = root.GetComponentsInChildren<Renderer>(true);
        foreach (var r in renderers)
        {
            if (r == null) continue;
            if (r.enabled != enabled) r.enabled = enabled;
        }
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        // OnValidate の最中に SetActive などを呼ぶと Unity が
        // 「SendMessage cannot be called during OnValidate」と警告するため、1 フレーム遅らせる。
        UnityEditor.EditorApplication.delayCall += () =>
        {
            if (this == null) return;   // 遅延中に破棄された場合
            Apply();
        };
    }

    private void Reset()
    {
        // コンポーネント追加時に、自分か親からアバターらしいルートを推測しておく
        if (avatarRoot == null) avatarRoot = gameObject;
    }
#endif
}
