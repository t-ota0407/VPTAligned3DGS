using UnityEngine;
using UnityEditor;

/// <summary>
/// UnityEditor 上で GaussianSplats の GlobalOpacity を管理するユーティリティ。
/// ・エディタ起動 / ドメインリロード時 → opacity = 1（見えるようにする）
/// ・プレイモード終了後 → opacity = 1（見えるようにリセット）
/// ビルド時はこのスクリプトは含まれないため、
/// ランタイムの初期値は GaussianSplatController.Start() が担う。
/// </summary>
[InitializeOnLoad]
public static class GaussianSplatEditorOpacity
{
    private const string GlobalOpacityProp = "_GaussianSplatGlobalOpacity";

    static GaussianSplatEditorOpacity()
    {
        // エディタ起動 / スクリプトリロード時に opacity を 1 にする
        Shader.SetGlobalFloat(GlobalOpacityProp, 1f);

        // プレイモードの状態変化を監視
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        // プレイモードを抜けてエディタモードに戻ったら opacity を 1 にリセット
        if (state == PlayModeStateChange.EnteredEditMode)
        {
            Shader.SetGlobalFloat(GlobalOpacityProp, 1f);
            Debug.Log("[GaussianSplatEditorOpacity] Play mode ended: GlobalOpacity reset to 1.");
        }
    }
}
