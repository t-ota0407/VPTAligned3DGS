using UnityEngine;

/// <summary>
/// 評価 E1 用のコントロールポイント・マーカー。
/// splatRoot の子として配置し、Editor の Scene ビューでスプラット形状の特徴点
/// （机の角など、実物で同定できる点）にぴったり合わせる。
/// 実行時は ControlPointMeasure が球を表示し、コントローラ先端との距離＝オーバーレイ誤差を記録する。
/// 命名規約: CP_01, CP_02, ...（名前順に計測する）。
/// </summary>
public class ControlPointMarker : MonoBehaviour
{
#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        Gizmos.color = Color.magenta;
        Gizmos.DrawWireSphere(transform.position, 0.02f);
        UnityEditor.Handles.Label(transform.position + Vector3.up * 0.03f, name);
    }
#endif
}
