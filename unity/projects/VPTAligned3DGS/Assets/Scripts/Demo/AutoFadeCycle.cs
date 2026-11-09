/// <summary>
/// デモの自動フェード（3DGS をゆっくり出し入れし続ける。Specification.md §15.4 / ImplementationSpec.md §14.5b）の判定。
///
/// 重ねる前と後の見比べがデモの見せ場で、来場者は X で切り替えられる。ただし X を知らない・押さない来場者も多いので、
/// スタッフが ON にしておけば、表示中は「出す → フェードアウト → 消す → フェードイン → …」を勝手に繰り返す。
///
/// 入力は「いま回してよいか」と経過時間だけで、不透明度を触る副作用は持たない。<see cref="Tick"/> が
/// 「次にすること」（<see cref="Command"/>）を返し、[[DemoController]] が AlignmentManager に渡す（RealignMonitor と同じ分け方）。
///
/// - **回すのは表示してよい局面だけ**（DemoController が canRun で渡す: Showing で、確かめ直しの途中でない）。
///   合っている確信の無い姿勢を見せない原則（§15.4-1）はそのまま: 回せない局面に入ったら止まり、非表示は DemoController が即時で行う。
/// - **来場者が X を押したら、その人の間は止める**（<see cref="UserToggled"/>）。押した結果（出す／消す）をそのまま残す。
///   回し続けると数秒で上書きされ、X が壊れたように見える。次の人がかぶったら（<see cref="NewWearer"/>）再開する。
/// - 回り始めは「出している」段から（直前まで表示中なので）。止めたとき消している段だったら出し直す（消えたまま止めない）。
/// </summary>
public sealed class AutoFadeCycle
{
    /// <summary><see cref="Tick"/> が返す「次にすること」。</summary>
    public enum Command
    {
        None,
        FadeOut,   // 消す段へ（フェードアウト）
        FadeIn,    // 出す段へ（フェードイン。既に出ていれば何もしなくてよい）
    }

    /// <summary>スタッフが自動フェードを ON にしているか。</summary>
    public bool Enabled { get; private set; }
    /// <summary>来場者（または HUD）が X で切り替えたので、次の装着まで止めているか。</summary>
    public bool HeldByUser { get; private set; }
    /// <summary>いま回しているか（Enabled でも、表示してよい局面でなければ回らない）。</summary>
    public bool Running { get; private set; }
    /// <summary>回している間、出している段か（false = 消している段）。</summary>
    public bool InShowPhase { get; private set; } = true;

    private float _t;   // いまの段に入ってからの秒

    /// <summary>ON/OFF。ON にするときは X による一時停止も解く（スタッフがいま回したいので）。</summary>
    public void SetEnabled(bool on)
    {
        Enabled = on;
        HeldByUser = false;
    }

    /// <summary>X（または HUD の表示ボタン）で人が表示を切り替えた。回している最中なら次の装着まで止める。
    /// 回っていない局面（再位置合わせ中に X で覗く など）では止めない（止めると、表示に戻っても回らない）。</summary>
    public void UserToggled()
    {
        if (!Enabled || !Running) return;
        // その場で止める（次の Tick を待たない）。押した結果を残すので出し直しはしない
        HeldByUser = true;
        Running = false;
        InShowPhase = true;
    }

    /// <summary>ヘッドセットがかぶり直された（次の来場者）。X による一時停止を解く。</summary>
    public void NewWearer()
    {
        HeldByUser = false;
    }

    /// <summary>毎フレーム呼ぶ。canRun = 表示してよい局面か。shownS / hiddenS = 出す段・消す段の長さ[s]（フェードの時間を含む）。</summary>
    public Command Tick(bool canRun, float dt, float shownS, float hiddenS)
    {
        if (!Enabled || HeldByUser || !canRun)
        {
            bool wasRunning = Running;
            bool wasHidden = !InShowPhase;
            Running = false;
            InShowPhase = true;
            // スタッフが OFF にした・表示中のまま止まった: 消している段なら出し直す。
            // 局面が変わった（canRun=false）なら表示は DemoController が決める。X で止めたなら押した結果を残す
            return wasRunning && wasHidden && canRun && !HeldByUser ? Command.FadeIn : Command.None;
        }
        if (!Running)
        {
            Running = true;
            InShowPhase = true;
            _t = 0f;
            return Command.FadeIn;   // 直前まで表示中のはずだが、X で消されたまま ON にされたときに出す
        }
        _t += dt;
        if (InShowPhase && _t >= shownS)  { InShowPhase = false; _t = 0f; return Command.FadeOut; }
        if (!InShowPhase && _t >= hiddenS) { InShowPhase = true;  _t = 0f; return Command.FadeIn; }
        return Command.None;
    }
}
