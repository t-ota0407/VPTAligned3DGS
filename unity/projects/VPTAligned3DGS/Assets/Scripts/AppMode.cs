using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 実験とデモの切り替え（→ Specification.md §15.1 / ImplementationSpec.md §14.1）。
///
/// 両者は**同じ APK の別シーン**（`GSTestScene` = 実験・起動時、`DemoScene` = デモ）。
/// アプリは実験シーンで起動し、最初の画面で**左右のスティックを同時に押し込み続ける**とデモへ移る
/// （CP の設定も先端校正も要らない）。デモからはスタッフパネルを開いて同じ操作で実験へ戻る。
///
/// 保存の分離: 同じアプリなので persistentDataPath は共通。デモの保存は別のファイル名
/// （<see cref="DemoFileKey"/>）にして、実験の試行開始（`AnchorPersistence.ClearSaved`）が
/// デモの保存を消さないようにする。実験は永続化を使わない（`ExperimentController.Awake` で OFF）。
/// </summary>
public static class AppMode
{
    public const string ExperimentScene = "GSTestScene";
    public const string DemoScene       = "DemoScene";
    /// <summary>デモの保存ファイル名の接頭辞（`vptalign_demo_&lt;modelId&gt;.json`）。実験は既定の `vptalign`。</summary>
    public const string DemoFileKey     = "vptalign_demo";

    public static void LoadDemo()       => Load(DemoScene);
    public static void LoadExperiment() => Load(ExperimentScene);

    private static void Load(string scene)
    {
        if (!Application.CanStreamedLevelBeLoaded(scene))
        {
            Debug.LogError($"[AppMode] {scene} がビルドに含まれていません（File > Build Profiles の Scene List に追加する）");
            return;
        }
        Debug.Log($"[AppMode] シーンを切り替えます → {scene}");
        SceneManager.LoadScene(scene, LoadSceneMode.Single);
    }

    /// <summary>左右のスティックを同時に押し込み続けた時間を測る（切り替えの物理割当）。
    /// どちらかを離すと 0 に戻る。発火は 1 回だけ（離して押し直すまで再発火しない）。</summary>
    public class SticksHold
    {
        private float _t0 = -1f;
        private bool  _fired;

        /// <summary>いま押し続けている割合 0..1（押していなければ 0）。HUD の表示用。</summary>
        public float Progress(float holdS) =>
            _t0 < 0f || holdS <= 0f ? 0f : Mathf.Clamp01((Time.unscaledTime - _t0) / holdS);

        /// <summary>毎フレーム呼ぶ。holdS 秒押し続けた瞬間だけ true。</summary>
        public bool Update(float holdS)
        {
            bool both = OVRInput.Get(OVRInput.RawButton.LThumbstick) && OVRInput.Get(OVRInput.RawButton.RThumbstick);
            if (!both) { _t0 = -1f; _fired = false; return false; }
            if (_t0 < 0f) _t0 = Time.unscaledTime;
            if (_fired || Time.unscaledTime - _t0 < holdS) return false;
            _fired = true;
            return true;
        }
    }
}
