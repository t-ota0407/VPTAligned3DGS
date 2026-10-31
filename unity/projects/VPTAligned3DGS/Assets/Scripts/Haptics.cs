using System.Collections;
using UnityEngine;

/// <summary>
/// コントローラの振動で「受け付けた／受け付けなかった」を返す。画面を見ていなくても分かるように
/// （CP を記録するときは視線が実物に、デモのスタッフは歩きながら操作している）。
///
/// 振動は止めるところまでがコルーチンなので、呼び出し側の MonoBehaviour（host）で回す。
/// 時間は実時間で数える（Time.timeScale には依らない）。
/// 以前は DemoController / ControlPointMeasure / ControlPointEdit がそれぞれ同じものを持っていた。
/// </summary>
public static class Haptics
{
    public enum Hand { Right, Both }

    /// <summary>右手に短く 1 回: 受け付けた（CP の仮置き・確定など、右手の操作への返事）。</summary>
    public static void Tick(MonoBehaviour host) => Play(host, Hand.Right, 0.3f, 0.6f, 0.06f);

    /// <summary>右手に弱く 2 回: 受け付けなかった（長め・弱め＝拒否の合図。「記録されていない」と分かる）。</summary>
    public static void RejectRight(MonoBehaviour host) => Play(host, Hand.Right, 0.1f, 0.25f, 0.09f, 2, 0.06f);

    /// <summary>両手に短く 1 回: 状態が進んだ合図（デモ。amp で強さを変える）。</summary>
    public static void PulseBoth(MonoBehaviour host, float amp) => Play(host, Hand.Both, 0.5f, amp, 0.08f);

    /// <summary>両手に長め 1 回: その操作はいまできない（デモ）。</summary>
    public static void RejectBoth(MonoBehaviour host) => Play(host, Hand.Both, 0.5f, 1f, 0.25f);

    /// <summary>onS 秒振動して止める、を repeat 回（間に offS 秒あける）。</summary>
    public static void Play(MonoBehaviour host, Hand hand, float frequency, float amplitude, float onS,
                            int repeat = 1, float offS = 0f)
    {
        if (host == null || !host.gameObject.activeInHierarchy) return;   // 非アクティブではコルーチンを始められない
        host.StartCoroutine(Routine(hand, frequency, amplitude, onS, repeat, offS));
    }

    private static IEnumerator Routine(Hand hand, float frequency, float amplitude, float onS, int repeat, float offS)
    {
        for (int i = 0; i < repeat; i++)
        {
            Set(hand, frequency, amplitude);
            yield return new WaitForSecondsRealtime(onS);
            Set(hand, 0f, 0f);
            if (offS > 0f) yield return new WaitForSecondsRealtime(offS);
        }
    }

    private static void Set(Hand hand, float frequency, float amplitude)
    {
        if (hand == Hand.Both) OVRInput.SetControllerVibration(frequency, amplitude, OVRInput.Controller.LTouch);
        OVRInput.SetControllerVibration(frequency, amplitude, OVRInput.Controller.RTouch);
    }
}
