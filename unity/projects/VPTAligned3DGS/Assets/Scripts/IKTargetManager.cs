using UnityEngine;

/// <summary>
/// CameraRig の各アンカーに定数の Offset を加えた値を
/// VRIK用 IKTarget の Transform に毎フレーム適用する。
/// </summary>
public class IKTargetManager : MonoBehaviour
{
    // ----------------------------------------------------------------
    // ソース (CameraRig 内の各アンカー)
    // ----------------------------------------------------------------
    [Header("Source Objects (CameraRig Anchors)")]
    [SerializeField] private Transform headObject;
    [SerializeField] private Transform leftHandObject;
    [SerializeField] private Transform rightHandObject;

    // ----------------------------------------------------------------
    // ターゲット (VRIK に指定する IKTarget)
    // ----------------------------------------------------------------
    [Header("IK Targets (for VRIK)")]
    [SerializeField] private Transform headIKTarget;
    [SerializeField] private Transform leftHandIKTarget;
    [SerializeField] private Transform rightHandIKTarget;

    // ----------------------------------------------------------------
    // 定数 Offset（変更不要のため readonly）
    // ----------------------------------------------------------------
    // Head
    private static readonly Vector3 HeadPositionOffset = Vector3.zero;
    private static readonly Vector3 HeadRotationOffset = Vector3.zero;

    // Left Hand
    private static readonly Vector3 LeftHandPositionOffset = new(-0.05f, -0.07f, -0.15f);
    private static readonly Vector3 LeftHandRotationOffset = new(0f, 90f, 90f);

    // Right Hand
    private static readonly Vector3 RightHandPositionOffset = new(0.05f, -0.07f, -0.15f);
    private static readonly Vector3 RightHandRotationOffset = new(0f, -90f, -90f);

    // ----------------------------------------------------------------
    // Unity ライフサイクル
    // LateUpdate を使用: OVRCameraRig.Update() がアンカー位置を確定させた後に
    // IKターゲットへ書き込むことで、1フレーム遅延を解消する。
    // ----------------------------------------------------------------
    private void LateUpdate()
    {
        ApplyOffset(headObject,      headIKTarget,      HeadPositionOffset,      HeadRotationOffset);
        ApplyOffset(leftHandObject,  leftHandIKTarget,  LeftHandPositionOffset,  LeftHandRotationOffset);
        ApplyOffset(rightHandObject, rightHandIKTarget, RightHandPositionOffset, RightHandRotationOffset);
    }

    // ----------------------------------------------------------------
    // 内部ユーティリティ
    // ----------------------------------------------------------------

    /// <summary>
    /// source の Position/Rotation に定数 Offset を加算して target に適用する。
    /// Position : source のローカル空間で解釈した posOffset を
    ///             ワールド座標に変換して加算。コントローラーの向きに追従する。
    /// Rotation : source の回転に Offset を右から掛ける（ローカル軸回転）。
    /// </summary>
    private static void ApplyOffset(Transform source, Transform target, Vector3 posOffset, Vector3 rotOffset)
    {
        if (source == null || target == null) return;
        // 修正D: source の値はワールド座標系。localRotation/localPosition に入れると
        // target に親がある場合に親の姿勢分ずれるため、ワールド系プロパティへ代入する。
        target.rotation = source.rotation * Quaternion.Euler(rotOffset);
        target.position = source.position + source.rotation * posOffset;
    }
}
