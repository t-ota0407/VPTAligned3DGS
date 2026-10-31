using System;
using UnityEngine;
using UnityEngine.UI;

// デモ展示（スタッフパネル・来場者向けの案内）の表示とボタン（AlignmentHud の一部。全体の方針は AlignmentHud.cs の class doc）。
public partial class AlignmentHud
{
    // ---------------- デモ展示モード（スタッフパネル） ----------------

    /// <summary>デモのスタッフパネル。**いま何が起きていて、次に何を押せばよいか**を 1 枚で出す。
    /// 来場者向けの案内は別の小さな板（<see cref="UpdateVisitorHint"/>）。</summary>
    private void RefreshDemo()
    {
        var an = demo.Anchors;
        string problem = an != null ? an.ProblemText : "AnchorPersistence is missing";
        string next = (demo.PcReady ? Btn("A") + ": re-align with the PC" : demo.RealignBlockReason)
                      + "  ·  " + Btn("Y") + ": manual";
        SetHeader("DEMO · STAFF", demo.State.ToString());
        SetProgress(-1f, "");

        switch (demo.State)
        {
            case DemoController.DemoState.Locating:
                SetBanner(ColSetup, "Finding saved alignment",
                          (string.IsNullOrEmpty(problem) ? "Look around the room" : problem)
                          + $"  ·  {demo.StateElapsedS:F0} s");
                break;
            case DemoController.DemoState.Relocating:
                SetBanner(ColWarn, "Re-checking position",
                          "Tracking moved or the headset slept. Look around the room."
                          + (demo.LastSaveFailed ? "\nThe last save failed — re-align." : "")
                          + (demo.StateElapsedS > 30f ? "\n" + next : ""));
                break;
            case DemoController.DemoState.NeedsAlignment:
                // 直前の再位置合わせを諦めたなら、その理由を先に出す（次に何を変えればよいかが分かる）
                SetBanner(ColBad, "Alignment needed",
                          (!string.IsNullOrEmpty(demo.LastAlignFailure) ? "Could not align: " + demo.LastAlignFailure
                           : string.IsNullOrEmpty(problem) ? "No usable saved alignment" : problem)
                          + "\n" + next);
                break;
            case DemoController.DemoState.Aligning:
                // 集める → 待つ → 足す（Specification.md §15.6b）。段階ごとに「いま何をすればよいか」を出す
                if (!demo.AlignActive)
                {
                    SetBanner(ColRunning, "Starting re-alignment", demo.AlignGuidance + "  ·  " + Btn("A") + ": cancel");
                }
                else if (demo.CurrentAlignPhase == RealignMonitor.Phase.Collect)
                {
                    SetBanner(ColRunning,
                              demo.TopUpCount == 0 ? "Walk around the room"
                                                   : $"Walk a bit more ({demo.TopUpCount}/{demo.MaxTopUps})",
                              demo.AlignGuidance + "  ·  " + Btn("A") + ": cancel");
                    // 時間は「実際に送信していた時間」（PC 待ちの一時停止は数えない）。一時停止中はそう書く
                    SetProgress(demo.CollectProgress,
                                $"Keyframes {demo.CollectAccepted} / {demo.CollectTarget}"
                                + $"  ·  {demo.CollectActiveS:F0} / {demo.PhaseLimitS:F0} s"
                                + (demo.CapturePaused ? $"  ·  paused, PC queue {demo.PcBacklog}"
                                   : demo.PcBacklog > 0 ? $"  ·  PC queue {demo.PcBacklog}" : ""));
                }
                else
                {
                    SetBanner(ColRunning, "Computing the alignment",
                              demo.AlignGuidance + "  ·  you can stop walking  ·  " + Btn("A") + ": cancel");
                    // 待機に上限時間は無い（PC が遅くても処理し終えるまで待つ）ので、進み具合は
                    // 「PC が受け取った分のうち何枚を処理し終えたか」で出す。経過秒も添える
                    int kf = udp != null ? udp.ServerKeyframeCount : 0;
                    int done = udp != null ? Mathf.Min(udp.CvDoneCount, kf) : 0;
                    string elapsed = $"  ·  {demo.PhaseElapsedS:F0} s";
                    SetProgress(demo.WaitingForCv || kf <= 0 ? -1f : done / (float)kf,
                                demo.WaitingForCv ? "Waiting for the PC to finish earlier work…" + elapsed
                                : udp != null && udp.CvBusy ? $"PC processed {done} / {kf} (processing {udp.CvBusyCount})…" + elapsed
                                : demo.PcBacklog > 0 ? $"PC processed {done} / {kf}…" + elapsed
                                : "Waiting for the result…" + elapsed);
                }
                break;
            case DemoController.DemoState.Saving:
                SetBanner(ColRunning, "Saving alignment", "Creating anchors — keep looking around");
                break;
            case DemoController.DemoState.Showing:
                bool forced = an != null && an.IsForced;
                SetBanner(forced || demo.LastSaveFailed ? ColWarn : ColOk,
                          forced ? "Showing (unverified)" : "Showing",
                          (demo.LastSaveFailed ? "Not saved — will be lost after a restart"
                           : forced ? "Anchors did not cross-check — check the overlay by eye"
                           : SavedSummary(an))
                          + "  ·  " + Btn("Y+B") + " hold: close");
                break;
            case DemoController.DemoState.Manual:
                SetBanner(ColEdit, "Manual adjust",
                          Btn("sticks") + ": move  ·  " + Btn("triggers") + ": scale  ·  "
                          + Btn("A") + ": lock & save  ·  " + Btn("Y") + ": cancel");
                break;
        }

        string notice = demo.NoticeAgeS < 20f ? demo.Notice : "";
        string metrics = demo.State == DemoController.DemoState.Aligning
                         ? AlignProgressMetrics(uploader != null ? uploader.SentCount : 0)
                         : AnchorMetrics(an);
        SetMetrics(string.IsNullOrEmpty(notice) ? metrics : Amber(notice));
        SetDemoCheck(an);
        SetDiag();
    }

    /// <summary>アンカーの見つかり具合。どこで詰まっているか（端末に無い／空間で見つからない／食い違う）を分けて出す。</summary>
    private static string AnchorMetrics(AnchorPersistence an)
    {
        if (an == null || !an.HasSave) return "No saved anchors";
        string s = $"Anchors: found {an.LocalizedCount}/{an.SavedAnchorCount} · tracked {an.TrackedCount}"
                   + $" · agree {an.AgreeingCount}/{an.RequiredAgreeing}";
        if (an.WorstDisagreementM >= 0f && an.TrackedCount >= 2)
            s += $" · spread {an.WorstDisagreementM * 100f:F0} cm";
        return s;
    }

    private static string SavedSummary(AnchorPersistence an)
    {
        if (an == null || !an.HasSave) return "Not saved";
        string src = string.IsNullOrEmpty(an.SavedSource) ? "" : $" · {an.SavedSource}";
        return $"Saved {Age(an.SavedUtc)} ago{src} · {an.SavedAnchorCount} anchors";
    }

    /// <summary>ISO 8601 の保存時刻から経過時間を短く（"12 min" / "3 h" / "2 d"）。</summary>
    private static string Age(string isoUtc)
    {
        if (!DateTime.TryParse(isoUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)) return "?";
        TimeSpan d = DateTime.UtcNow - t.ToUniversalTime();
        if (d.TotalMinutes < 1) return "<1 min";
        if (d.TotalHours < 1) return $"{d.TotalMinutes:F0} min";
        if (d.TotalDays < 1) return $"{d.TotalHours:F0} h";
        return $"{d.TotalDays:F0} d";
    }

    /// <summary>デモの準備チェック。PC はデモでは必須ではない（保存があれば要らない）ので、止まっていても琥珀にする。</summary>
    private void SetDemoCheck(AnchorPersistence an)
    {
        if (_checkText == null) return;
        string anc = an == null || !an.HasSave ? Dot(0, "No anchors")
                   : an.HasConsensus ? Dot(an.IsForced ? 1 : 2, $"Anchors {an.AgreeingCount}/{an.SavedAnchorCount}")
                   : an.TrackedCount > 0 ? Dot(1, $"Anchors {an.TrackedCount}/{an.SavedAnchorCount}")
                   : Dot(0, $"Anchors 0/{an.SavedAnchorCount}");
        string saved = an != null && an.HasSave ? Dot(demo.LastSaveFailed ? 1 : 2, "Saved " + Age(an.SavedUtc))
                                                : Dot(0, "Not saved");
        string srv = udp != null && udp.ServerAlive ? Dot(2, $"PC {udp.RttMs:F0}ms") : Dot(1, "PC off");
        string cv  = udp != null && udp.CvAlive ? Dot(2, "CV") : Dot(1, "CV off");
        _checkText.text = string.Join("   ", anc, saved, srv, cv);
    }

    // ---------------- 来場者向けの案内（デモ） ----------------

    private Canvas _hintCanvas;
    private Text   _hintTitle, _hintSub, _hintStaff;
    private bool   _hintShow;
    private float  _hintNextRefresh;
    private const float HintW = 560f, HintH = 124f;
    [Tooltip("（デモ）来場者向けの案内を置く距離[m]。")]
    [SerializeField] private float hintDistanceM = 1.0f;
    [Tooltip("（デモ）来場者向けの案内を視線からこの高さ[m]だけ下げる。")]
    [SerializeField] private float hintDownOffsetM = 0.12f;

    /// <summary>来場者向けの案内（`Look around the room` など）。見回してもらうのが目的なので、
    /// 主パネルのような「置いてある」追従ではなく、視線に緩く付いてきて常に視界に入るようにする。
    ///
    /// **3 行目にスタッフパネルの開き方（Y＋B 長押し）を出す**（スタッフも忘れるので UI に書く）。
    /// 出すのはスタッフが要る場面だけ（<see cref="DemoController.ShowStaffHint"/>）。
    /// Y＋B を押している間は案内を差し替えて残り時間を出す（3 秒は無反応だと効いているか分からない）。
    /// 文字列の組み立ては 10Hz（主パネルと同じ理由: 毎フレームだと GC で 72fps を削る）、追従は毎フレーム。</summary>
    private void UpdateVisitorHint()
    {
        if (_hintCanvas == null || _head == null) return;
        if (Time.unscaledTime >= _hintNextRefresh)
        {
            _hintNextRefresh = Time.unscaledTime + RefreshIntervalS;
            RefreshVisitorHint();
        }
        if (_hintCanvas.gameObject.activeSelf != _hintShow)
        {
            _hintCanvas.gameObject.SetActive(_hintShow);
            if (_hintShow) PlaceHint(1f);
        }
        if (_hintShow) PlaceHint(1f - Mathf.Exp(-5f * Time.deltaTime));   // フレームレート非依存の減衰
    }

    private void RefreshVisitorHint()
    {
        if (demo.StaffMode) { _hintShow = false; return; }

        string title, sub, staff;
        float p = demo.StaffComboProgress;
        if (p > 0f)
        {
            // Y＋B を押している最中。離すと 0 からやり直しになることも伝える
            title = "Opening the staff panel…";
            sub   = $"Keep holding {Btn("Y")} + {Btn("B")}  ·  {p * demo.StaffComboHoldS:F1} / {demo.StaffComboHoldS:F0} s";
            staff = "";
        }
        else
        {
            title = demo.VisitorHint;
            sub   = demo.VisitorHintSub;
            staff = demo.ShowStaffHint
                    ? $"Staff: hold {Btn("Y")} + {Btn("B")} together for {demo.StaffComboHoldS:F0} s to open the staff panel"
                    : "";
            // 来場者向けの文言が無い局面（デモに入った直後・表示中）でもスタッフ向けの行は出す
            if (string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(staff)) title = "Demo mode";
        }
        _hintShow = !string.IsNullOrEmpty(title);
        if (!_hintShow) return;
        if (_hintTitle.text != title) _hintTitle.text = title;
        if (_hintSub.text != sub)     _hintSub.text = sub;
        if (_hintStaff.text != staff) _hintStaff.text = staff;
    }

    private void PlaceHint(float k)
    {
        Vector3 target = _head.position + _head.forward * hintDistanceM - _head.up * hintDownOffsetM;
        Transform t = _hintCanvas.transform;
        t.position = Vector3.Lerp(t.position, target, k);
        Vector3 look = t.position - _head.position;
        if (look.sqrMagnitude > 1e-6f)
            t.rotation = Quaternion.Slerp(t.rotation, Quaternion.LookRotation(look.normalized, Vector3.up), k);
    }

    private void BuildVisitorHint()
    {
        var go = new GameObject("VisitorHintCanvas");
        go.transform.SetParent(transform, false);
        _hintCanvas = go.AddComponent<Canvas>();
        _hintCanvas.renderMode = RenderMode.WorldSpace;
        var rt = _hintCanvas.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(HintW, HintH);
        go.transform.localScale = Vector3.one * 0.001f;   // 1px = 1mm
        NewUi("HintBg", rt, Vector2.zero, new Vector2(HintW, HintH)).AddComponent<Image>().color = ColBg;
        // 上から 案内（28px）／補足（17px）／スタッフ向け（14px・淡色。割当の文字だけ山吹色）
        _hintTitle = AddText(NewUi("HintTitle", rt, new Vector2(0f, 30f), new Vector2(HintW - 40f, 44f)),
                             28, TextAnchor.MiddleCenter, ColText, false);
        _hintSub = AddText(NewUi("HintSub", rt, new Vector2(0f, -4f), new Vector2(HintW - 40f, 28f)),
                           17, TextAnchor.MiddleCenter, ColMuted, false);
        _hintStaff = AddText(NewUi("HintStaff", rt, new Vector2(0f, -38f), new Vector2(HintW - 40f, 22f)),
                             14, TextAnchor.MiddleCenter, ColFaint, false);
        go.SetActive(false);
    }

    /// <summary>デモのスタッフパネルの下半分（準備チェック・ボタン）と、来場者向けの案内の板。BuildUi から呼ぶ。</summary>
    private void BuildDemoControls(RectTransform crt, float y, float inner)
    {
        y -= RowGap;
        AddRule(crt, y, inner);
        y -= RowGap;
        const float DemoCheckH = 40f;
        y -= DemoCheckH / 2f;
        _checkText = AddText(NewUi("Check", crt, new Vector2(0, y), new Vector2(inner, DemoCheckH)),
                             15, TextAnchor.MiddleLeft, ColText, true);
        y -= DemoCheckH / 2f;
        BuildDemoButtons(crt, y - RowGap, inner);
        BuildVisitorHint();
    }

    /// <summary>デモのスタッフ用ボタン。**2 列 3 行＋全幅 1 行**（割当タグを添えるので 3 列だと文字が隣へはみ出す）。
    /// 手動調整中は A が AlignmentManager の Lock（保存）に取られるので、ラベルもそれに合わせて変える。</summary>
    private void BuildDemoButtons(RectTransform crt, float y, float inner)
    {
        const float H = 40f, G = 8f;
        float w = (inner - G) / 2f;
        float xl = -inner / 2f + w / 2f, xr = inner / 2f - w / 2f;
        Color c = new Color(1f, 1f, 1f, 0.10f);
        Color alBtn = new Color(1f, 1f, 1f, 0.06f);
        y -= H / 2f;
        AddButtonAt(crt, new Vector2(xl, y), new Vector2(w, H), null,
                    () => { if (demo.InManual) align?.Lock();
                            else if (demo.CanCancelAlign) demo.CancelAlign();
                            else demo.Realign(); }, 15,
                    () => (demo.InManual ? "Lock & save" : demo.CanCancelAlign ? "Cancel re-align" : "Re-align") + Key("A"),
                    () => demo.InManual || demo.CanCancelAlign || demo.CanRealign, c);
        // 手動調整中の B は無効（AlignmentManager.ResetButtonEnabled=false）なので、ここも押せなくなる
        AddButtonAt(crt, new Vector2(xr, y), new Vector2(w, H), null,
                    () => demo.ShowAnyway(), 15,
                    () => "Show anyway" + Key("B"),
                    () => demo.CanShowAnyway, c);
        y -= H + G;
        AddButtonAt(crt, new Vector2(xl, y), new Vector2(w, H), null,
                    () => demo.ToggleManual(), 15,
                    () => (demo.InManual ? "Cancel manual" : "Manual adjust") + Key("Y"),
                    () => demo.InManual || demo.CanManual, c);
        // 消去は元に戻せないので、Poke では消さずに長押しを案内するだけにする（実行は A 長押しのみ）
        AddButtonAt(crt, new Vector2(xr, y), new Vector2(w, H), null,
                    () => demo.HintClearHold(), 15,
                    () => "Clear saved" + Key("A hold"),
                    () => demo.CanClearSaved, c);
        y -= H + G;
        AddButtonAt(crt, new Vector2(xl, y), new Vector2(w, H), null,
                    () => align?.ToggleSplatVisible(), 15,
                    () => (align != null && align.SplatVisible ? "Hide splats" : "Show splats") + Key("X"),
                    null, alBtn);
        AddButtonAt(crt, new Vector2(xr, y), new Vector2(w, H), null,
                    () => demo.SetStaffMode(false), 15,
                    () => "Close" + Key("Y+B hold"),
                    () => !demo.InManual, alBtn);
        y -= H + G;
        // 実験へ戻る（実験の最初の画面からデモへ入る操作と同じ割当。保存した位置合わせは残る）
        AddButtonAt(crt, new Vector2(0f, y), new Vector2(inner, H), null,
                    () => demo.SwitchToExperiment(), 15,
                    () => "Back to experiment mode" + Key("both sticks hold"),
                    () => demo.CanSwitchToExperiment, alBtn);
    }
}
