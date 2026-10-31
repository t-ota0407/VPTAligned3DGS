using UnityEngine;

/// <summary>
/// 物理ボタン 1 つ（または同時押しの組み合わせ）の押下を追い、短押し・長押しを判定する。
/// 毎フレーム <see cref="Update"/> に「いま押されているか」を渡す。どのボタンを読むか
/// （<c>Button.One</c> か <c>RawButton.A</c> か）は呼び出し側が決める。
///
///   - 長押し（<see cref="LongPress"/>）は**離す前に**発火する。押している間に反応が無いと、
///     どれだけ押し続ければよいのか分からない（触覚が出た時点で指を離せる）。
///   - 長押しを発火した押下と、<see cref="Taint"/> した押下（同時押しの一部だった・その局面では
///     受け付けない）は、離しても短押し（<see cref="Tapped"/>）にしない。
///
/// 以前は ExperimentController（A / B）・DemoController（A / B / Y・Y＋B）・AppMode（両スティック）が
/// それぞれ別の書き方で持っていた。時間は実時間（Time.unscaledTime）で数える。
/// </summary>
public sealed class PressTracker
{
    private readonly float _tapMaxS;
    private float _t0 = -1f;          // 押し始めた時刻（押していなければ負）
    private bool  _longFired;
    private bool  _tainted;

    /// <param name="tapMaxS">これより長く押してから離したものは短押しにしない（既定: 上限なし）。</param>
    public PressTracker(float tapMaxS = float.PositiveInfinity) { _tapMaxS = tapMaxS; }

    /// <summary>いま押されているか。</summary>
    public bool Down => _t0 >= 0f;
    /// <summary>このフレームで押された（押した瞬間に発火させたい操作用）。</summary>
    public bool Pressed { get; private set; }
    /// <summary>このフレームで離され、短押しだった。</summary>
    public bool Tapped { get; private set; }
    /// <summary>いまの押下で長押しを発火済みか。</summary>
    public bool LongFired => Down && _longFired;

    /// <summary>毎フレーム 1 回呼ぶ。</summary>
    public void Update(bool pressed)
    {
        float now = Time.unscaledTime;
        Pressed = Tapped = false;
        if (pressed && _t0 < 0f)
        {
            _t0 = now; _longFired = false; _tainted = false;
            Pressed = true;
        }
        else if (!pressed && _t0 >= 0f)
        {
            Tapped = !_tainted && !_longFired && now - _t0 <= _tapMaxS;
            _t0 = -1f;
        }
    }

    /// <summary>holdS 秒押し続けた瞬間に 1 回だけ true（Taint した押下では発火しない）。</summary>
    public bool LongPress(float holdS)
    {
        if (_t0 < 0f || _longFired || _tainted || Time.unscaledTime - _t0 < holdS) return false;
        _longFired = true;
        return true;
    }

    /// <summary>押し続けている割合 0..1（押していなければ 0）。HUD に「あと何秒」を出す用。</summary>
    public float Progress(float holdS) =>
        _t0 < 0f || holdS <= 0f ? 0f : Mathf.Clamp01((Time.unscaledTime - _t0) / holdS);

    /// <summary>いまの押下を短押し・長押しとして扱わない（離しても何も起きない）。</summary>
    public void Taint()
    {
        if (_t0 >= 0f) _tainted = true;
    }
}
