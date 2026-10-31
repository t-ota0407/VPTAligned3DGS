using System;
using UnityEngine;
using UnityEngine.UI;

// 実験（試行・CP の測量と編集・先端校正）の表示とボタン（AlignmentHud の一部。全体の方針は AlignmentHud.cs の class doc）。
public partial class AlignmentHud
{
    /// <summary>実験の表示（試行・準備・振り返り）。局面は ExperimentController.Phase で決まる。</summary>
    private void RefreshExperiment()
    {
        SetHeader("TRIAL", $"{exp.TrialIndex} / {exp.PlannedTrials}");

        // 破棄はまず伝える。原因（部屋の座標系が動いた）を知らないと、実験者は
        // 「位置合わせが失敗した」と受け取って同じ条件でやり直してしまう。
        if (!string.IsNullOrEmpty(exp.VoidNotice) && exp.VoidNoticeAgeS < 25f
            && exp.State == ExperimentController.TrialState.Idle)
        {
            SetBanner(ColBad, "Trial discarded",
                      "Tracking moved — keep the headset on, stay in this space, press "
                      + Btn("B") + " to redo");
            SetProgress(-1f, "");
            SetMetrics(Amber("The room coordinates shifted (" + exp.VoidNotice
                             + "), so this trial cannot be used."));
            SetCheck();
            SetDiag();
            return;
        }

        switch (exp.Phase)
        {
            case ExperimentController.UiPhase.NeedsSetup:
                SetBanner(ColSetup, "Setup required", FirstUnmetReason());
                SetProgress(-1f, "");
                // 実験の準備（CP・先端校正）をせずにデモを見せたいだけの人向けに、入口をここで案内する
                SetMetrics(exp.DemoSwitchProgress > 0f
                           ? $"Switching to the demo… {exp.DemoSwitchProgress * exp.DemoSwitchHoldS:F1} / {exp.DemoSwitchHoldS:F0} s"
                           : "Complete the checks below to start\nDemo without CPs: hold " + Btn("both sticks")
                             + $" for {exp.DemoSwitchHoldS:F0} s");
                break;

            case ExperimentController.UiPhase.Ready:
                SetBanner(ColReady, "Ready to start",
                          "Press " + Btn("B") + ", then walk a curved path while looking around");
                SetProgress(-1f, "");
                // 未配置の CP を抱えたまま測ると誤差が数メートルになり、位置合わせが悪いのか
                // CP が置かれていないのか区別がつかなくなる。始める前に気づけるようにする。
                SetMetrics(cpMeasure != null && !string.IsNullOrEmpty(cpMeasure.SetupWarning)
                           ? Amber(cpMeasure.SetupWarning) : LastTrialSummary());
                break;

            case ExperimentController.UiPhase.Running:
                SetBanner(ColRunning, "Walk around the room",
                          "Cover the space. Do not stand still or walk in a straight line.");
                SetProgress(exp.CollectProgress, $"{exp.Elapsed:F0} / {exp.CollectSeconds:F0} s");
                SetMetrics(RunningMetrics);
                break;

            case ExperimentController.UiPhase.Surveying:
                RefreshSurveying();
                break;

            case ExperimentController.UiPhase.EditingCPs:
                RefreshEditingCPs();
                break;

            case ExperimentController.UiPhase.Review:
                SetBanner(ColOk, "Collection complete",
                          exp.AllTrialsDone ? "All trials done. Run experiment_report.py on the PC."
                                            : "Check the overlay, then press " + Btn("B")
                                              + " for the next trial");
                SetProgress(1f, "");
                SetMetrics(UploadSummary());
                break;

            default:  // AllDone
                // **データが PC に揃ったかをここで断言する。** 実験者がヘッドセットを外して
                // よいかどうかの判断がこれで決まる。未送達が残っているのに終わったと思われると、
                // 測り直しの効かない CP 実測値が端末の中に取り残される。
                bool delivered = udp != null && udp.PendingEventCount == 0;
                SetBanner(delivered ? ColOk : ColWarn, "All trials complete",
                          delivered ? "Data is on the PC — run experiment_report.py"
                                    : "Waiting for the last records to reach the PC…");
                SetProgress(1f, "");
                SetMetrics(UploadSummary());
                break;
        }

        SetCheck();
        SetDiag();
    }

    /// <summary>校正中の暫定残差がこれ[mm]を超えたら「先端がずれている」と警告する。</summary>
    private const float SlipWarnMm = 8f;

    private void RefreshCalibrating()
    {
        float spread = tipCalib.CurrentSpreadDeg, need = tipCalib.RequiredSpreadDeg;
        bool ok = spread >= need && tipCalib.SampleCount >= tipCalib.RequiredSamples;

        SetHeader("TIP CALIBRATION", "");
        // 割当は HUD 側で添える（ColKey で目立たせるため。→ ControllerTipCalibration.GuidanceSub）。
        // 校正中の Y は「終了」に割り当たっている（ExperimentController の Y の分岐）。
        SetBanner(ok ? ColOk : ColMeasure, tipCalib.GuidanceTitle,
                  tipCalib.GuidanceSub + "  ·  " + Btn("Y") + ": finish");
        SetProgress(tipCalib.Progress,
                    $"{tipCalib.SampleCount}/{tipCalib.RequiredSamples} samples · "
                    + $"{spread:F0}°/{need:F0}° swing");

        // 暫定残差。先端が点から滑ると跳ね上がるので、その場で当て直せる。
        // 8mm は接点が約 5mm ずれた状態に相当し、そこからオフセット誤差が 2mm を超え始める。
        float lr = tipCalib.LiveResidualMm;
        SetMetrics(lr < 0f ? "Measuring residual…"
                   : lr > SlipWarnMm ? Amber($"Residual {lr:F1} mm — tip is slipping, reseat it")
                                     : $"Residual {lr:F1} mm");
        SetCheck();
        SetDiag();
    }

    /// <summary>Real CP の測定中の表示。実験の前に 1 回だけ通る局面。</summary>
    private void RefreshSurveying()
    {
        SetHeader("SET REAL CONTROL POINTS", $"{cpMeasure.DoneCount} / {cpMeasure.MarkerCount}");

        // **受け付けられない状態で「触ってください」と出さない。**
        // 指示どおり押しても無反応だと、実験者は故障を疑うしかなくなる。
        string block = cpMeasure.BlockReason;
        if (!string.IsNullOrEmpty(block))
        {
            SetBanner(ColSetup, "Cannot set Real CPs yet", block);
            SetProgress(-1f, ""); SetMetrics(""); SetCheck(); SetDiag();
            return;
        }

        // スティックは残す。位置合わせ前の球は壁の向こうや 20m 先に出ることがあり、
        // シーンごと引き寄せられないと「次はどの点か」の目印すら見えない。
        // ただし用途は**見えるところへ持ってくる**ことだけで、実物に重ねる作業ではない
        // （重ねると球に釣られて先端が実物からずれる）。X の案内はメトリクス行に置く。
        // 指示に出す名前は**ワールドのラベルと同じ文字**（`RCP_01`）にする。HUD が `CP_01`、
        // 視界の球が `VCP_01`、記録した点が `RCP_01` では、どれを探せばよいのか対応が取れない。
        // R が「実物側」を表すので "the real" は重ねない（意味は下の 2 行で押さえる）。
        //
        // **記録は 2 段階**なので、指示も 2 段階で出す。「置いた」と「記録した」を同じ文言に
        // すると、確定し忘れた点をそのまま置き去りにしてしまう（測量は 1 回きりで測り直しが
        // 効かない）。仮置き中はタイトルから変えて、確定待ちであることを最初に読ませる。
        if (cpMeasure.HasPending)
            SetBanner(ColSetup, $"Confirm {cpMeasure.CurrentLabel}?",
                      "Look at the white dot from another angle. It is <b>not recorded yet</b>."
                      + "  ·  " + Btn("A") + ": confirm & next"
                      + "  ·  " + Btn("right trigger") + ": place again"
                      + "  ·  " + Btn("left trigger") + ": discard");
        else
            SetBanner(ColMeasure, $"Walk to {cpMeasure.CurrentLabel}",
                      "Touch the real feature with the tip, then pull the " + Btn("right trigger")
                      + " to place it  ·  " + Btn("left trigger") + ": redo previous"
                      + "  ·  " + Btn("sticks") + ": bring the markers into view");
        SetProgress(cpMeasure.Progress, $"{cpMeasure.DoneCount} / {cpMeasure.MarkerCount} points");

        if (cpMeasure.RecentlyRejected)
        {
            // 理由が分かるものは理由を出す。「押したのに何も起きない」が最も困る。
            string why = cpMeasure.RejectReason;
            SetMetrics(Amber(string.IsNullOrEmpty(why) ? "Not recorded — see the reason above" : why));
        }
        else if (cpMeasure.HasPending)
        {
            // 確定するまでは何も記録されていない、を数字の側からも押さえる。
            SetMetrics(Amber("White RCP dot = placed but NOT recorded  ·  " + Btn("A") + " records it"
                             + (cpMeasure.PendingRetries > 0
                                ? $"  ·  placed {cpMeasure.PendingRetries + 1}×" : "")));
        }
        else if (!cpMeasure.CanGuideVisually)
        {
            // 球は出ているが、位置合わせ前なので実物の上には載っていない。
            // **「球のところへ行けばいい」と読ませない。** 球へ寄せると先端が実物からずれ、
            // それがそのまま Real CP の誤差になる（グラウンドトゥルースが汚れる）。
            SetMetrics(Amber("The yellow VCP sphere only says which point — it is NOT on the real object.\n"
                             + "Touch the real thing  ·  " + Btn("X") + ": peek at the splats"));
        }
        else
        {
            // V/R の意味はここで一度だけ教える（ワールドのラベルが VCP_nn / RCP_nn）。
            SetMetrics("Yellow VCP sphere = which point now  ·  orange RCP dot = recorded"
                       + "  ·  " + Btn("X") + ": peek at the splats");
        }
        SetCheck();
        SetDiag();
    }

    /// <summary>Virtual CP の VR 編集中の表示。位置合わせ前の準備でしか通らない局面。</summary>
    private void RefreshEditingCPs()
    {
        SetHeader("EDIT VIRTUAL CONTROL POINTS", $"{cpEdit.EditedCount} / {cpEdit.MarkerCount} moved");

        // 名前はワールドの球のラベルと同じ文字（`VCP_01`）で出す（→ RefreshSurveying の注記）。
        // 置き直しも 2 段階（右トリガー＝仮置き / A＝確定して次へ）。仮置き中はタイトルを変える。
        if (cpEdit.HasPending)
            SetBanner(ColSetup, $"Confirm {cpEdit.CurrentLabel}?",
                      "Move your head and check the white sphere sits on the feature."
                      + "  ·  " + Btn("A") + ": confirm & next"
                      + "  ·  " + Btn("right trigger") + ": place again"
                      + "  ·  " + Btn("left trigger") + ": discard");
        else
            SetBanner(ColEdit, $"Place {cpEdit.CurrentLabel} on the splats",
                      // **3 行に収まる長さで書く**（副見出しの枠 = SubH。溢れた行は縦 Truncate で
                      // 黙って切り落とされる。実際にここは枠が 2 行だった頃に「sticks: move the scene」が
                      // まるごと消えていた）。`Skip`（A）は下のメトリクス行で案内する。
                      "Touch the matching feature <b>on the splats</b> with the tip, then "
                      + Btn("right trigger")
                      + "  ·  " + Btn("left trigger") + ": previous"
                      + "  ·  " + Btn("sticks") + ": move the scene");
        // **列のどこにいるかを必ず出す。** 右トリガーで置くと自動で次の点へ進むが、以前ここが
        // 空（SetProgress(-1f)）で、進んだことが画面から読み取れなかった。球の色（黄→緑）だけが
        // 手掛かりで、モデルが視界の外にあると何も変わらないように見える。実機で「CP_01 の次へ
        // 進めない」と報告されたのはこれが原因で、動作そのものは正しかった。
        SetProgress(cpEdit.Progress,
                    $"{cpEdit.CurrentLabel}  ·  {cpEdit.CurrentIndex + 1} / {cpEdit.MarkerCount}"
                    + $"  ·  {cpEdit.EditedCount} moved");

        if (cpEdit.RecentlyRejected)
        {
            SetMetrics(Amber(cpEdit.RejectReason));
        }
        else if (cpEdit.HasPending)
        {
            SetMetrics(Amber("White VCP sphere = placed but NOT committed  ·  " + Btn("A") + " commits it"
                             + (cpEdit.PendingRetries > 0
                                ? $"  ·  placed {cpEdit.PendingRetries + 1}×" : "")
                             + $"\nGrey dot = original  ·  moved {cpEdit.CurrentMoveM * 100f:F0} cm"));
        }
        else
        {
            // 実物ではなくモデルに触る、という取り違えが最も起きやすい。毎回念を押す。
            string warn = cpEdit.MoveWarning;
            string moved = cpEdit.CurrentMoveM > 0.005f
                           ? $"moved {cpEdit.CurrentMoveM * 100f:F0} cm  ·  " : "";
            // 2 行目は「次に進む手」を常に出す。全点を触る作業ではないので、直す気の無い点を
            // 飛ばせないと詰まる（末尾では飛ばす先が無いので終わり方を出す）。
            // **枠は 2 行ぶん（42px）しかなく、溢れた行は縦 Truncate で黙って消える。**
            // 各行を折り返さない長さ（幅 476 / fontSize 16）に収めること。
            string next = cpEdit.AtLastPoint
                          ? "last point  ·  " + Btn("Y") + ": finish"
                          : Btn("A") + ": skip this point";
            SetMetrics(!string.IsNullOrEmpty(warn) ? Amber(warn)
                       : "Touch the virtual feature on the splats, not the real object"
                         + "\nGrey dot = original  ·  " + moved + next);
        }
        SetCheck();
        SetDiag();
    }

    /// <summary>実験コンポーネントが無いシーン用（位置合わせだけの簡易表示）。</summary>
    private void RefreshWithoutExperiment()
    {
        bool locked = align != null && align.State == AlignmentManager.AlignState.Locked;
        SetHeader("ALIGNMENT", "");
        SetBanner(locked ? ColOk : ColReady,
                  locked ? "Aligned" : "Aligning",
                  uploader != null && uploader.AutoCaptureOn
                      ? "Auto capture on" : Btn("Left grip") + " to send a keyframe");
        SetProgress(-1f, "");
        SetMetrics($"Sent {(uploader != null ? uploader.SentCount : 0)} · "
                   + $"accepted {Accepted} · rejected {Rejected}");
        SetCheck();
        SetDiag();
    }

    private int CpTotal => cpMeasure != null ? cpMeasure.MarkerCount : 0;

    private string FirstUnmetReason()
    {
        if (exp == null) return "";
        if (!exp.TrackingSteady)
            return "Tracking just moved — wait a few seconds for it to settle";
        if (!exp.ServerReady)
            return udp != null && udp.HasTarget
                ? "Server not responding — is run.py running?"
                : "Server not found — check run.py and the network";
        if (udp != null && udp.CvBusy)
            return $"CV is processing {udp.CvBusyCount} keyframes — wait for it to finish";
        if (udp != null && udp.CvUnknown)
            return "Waiting for CV status — if this persists, check the run.py window";
        if (!exp.CvReady)     return "CV worker not running — check the [cv] lines in the run.py window";
        if (!exp.PointsReady) return "No Virtual CP — place them in the Editor";
        if (!exp.SurveyReady)
        {
            // A を押したのに始まらなかったなら、まずその理由。押して無反応だと、
            // ボタンが効いていないのか条件が足りないのかを実機で区別できない。
            if (cpMeasure != null && cpMeasure.RecentlyRejected && !cpMeasure.IsRunning
                && !string.IsNullOrEmpty(cpMeasure.RejectReason))
                return cpMeasure.RejectReason;

            // Real CP の測定は実験の前に 1 回。無効化されたなら、その理由をそのまま出す。
            string why = cpMeasure != null ? cpMeasure.InvalidReason : "";
            return string.IsNullOrEmpty(why)
                   ? "Set the Real CPs once — " + Btn("A")
                   : why + " — set them again with " + Btn("A");
        }
        if (tipCalib != null && tipCalib.HasStaleCalibration)
            return $"Tip offset is from a previous session ({tipCalib.StoredAgeHours:F0} h ago)"
                   + " — recalibrate with " + Btn("Y");
        if (!exp.TipReady)    return "Calibrate the controller tip first — " + Btn("Y");
        return "";
    }

    /// <summary>PC 側にデータが届いているか。CP 実測値は測り直しが効かないので、
    /// 「端末に残っているが PC には無い」状態をはっきり見せる。</summary>
    private string UploadSummary()
    {
        if (udp == null) return "";
        int pending = udp.PendingEventCount;
        if (pending == 0) return $"Saved on PC: {udp.ServerEventCount} records";
        return Amber($"{pending} records not yet on the PC — keep the app running");
    }

    private string LastTrialSummary()
    {
        if (exp == null || exp.TrialIndex == 0) return "No trials recorded yet";
        float t = exp.TimeToLock;
        return t >= 0f ? $"Last trial: aligned after {t:F1} s, {exp.KfAtLock} keyframes"
                       : "Last trial: no confirmed alignment";
    }

    /// <summary>収集中の状況。詰まっている場所が一目で分かるようにする。
    ///
    /// ロックしないときの原因は 3 段階のどこかにある:
    ///   (1) キーフレームが PC に届いていない  → 送信数と PC 採用数を比べる
    ///   (2) PC で解が棄却されている            → 棄却理由をそのまま出す
    ///   (3) 解は来ているが一致しない            → 一致回数とずれを出す
    /// 分けて表示しないと、実機側からは全部「進まない」に見えてしまう。</summary>
    private string RunningMetrics => AlignProgressMetrics(exp != null ? exp.KfSent : 0);

    /// <summary>準備チェック。●=OK / ▲=未検証 / ✕=未達 を 1 行に並べる。</summary>
    private void SetCheck()
    {
        if (_checkText == null) return;
        if (exp == null) { _checkText.text = ""; return; }

        string srv = exp.ServerReady && udp != null && udp.RttMs >= 0f
                     ? $"Server {udp.RttMs:F0}ms" : "Server";

        // CV は 3 状態。hloc の 1 バッチは数秒かかりその間ハートビートが止まるので、
        // 「処理中」を「停止」と同じ ✕ で出すと誤解を招く。
        string cv;
        if (udp != null && udp.CvBusy)          cv = Dot(1, $"CV {udp.CvBusyCount}");
        else if (udp != null && udp.CvUnknown)  cv = Dot(1, "CV ?");
        else                                     cv = Dot(exp.CvReady ? 2 : 0, "CV");

        // 先端も 3 状態。前回セッションの値が残っているだけの状態を緑にすると、
        // 別の物理構成で取った値をそのまま使ってしまう。
        string tip = tipCalib != null && tipCalib.HasStaleCalibration
                     ? Dot(1, "Tip old")
                     : Dot(exp.TipReady ? 2 : 0,
                           tipCalib != null && tipCalib.IsCalibrated
                           ? $"Tip {tipCalib.ResidualMm:F1}mm" : "Tip");

        // トラッキングは正常なのが当たり前なので、**異常なときだけ**出す。
        // 常に並べると 1 行に収まらず、他の項目が Truncate で消えてしまう。
        string trk = exp.TrackingSteady ? "" : "   " + Dot(0, "Tracking");
        // 未送達がある間だけ出す。0 が常態なので、出ていること自体が注意喚起になる。
        int pending = udp != null ? udp.PendingEventCount : 0;
        string up = pending > 0 ? "   " + Dot(1, $"Upload {pending}") : "";

        // CP は 2 段構え。Virtual CP（Editor か VR で配置）と Real CP（実測）の両方が要る。
        // 片方だけ緑にすると、実測を忘れたまま試行を始めてしまう。
        string cp = exp.PointsReady
                    ? Dot(exp.SurveyReady ? 2 : 0,
                          exp.SurveyReady ? $"Real CP {CpTotal}" : "Real CP not set")
                    : Dot(0, "No Virtual CP");

        // Virtual CP を VR で動かしたセッションであることを残す。シーンに焼かれていない
        // 変更を抱えたまま実験を終えると、PC の JSON を取り込み忘れて次回に消える。
        int edited = cpEdit != null ? cpEdit.EditedCount : 0;
        string ed = edited > 0 ? "   " + Dot(1, $"Virtual CP edited {edited}") : "";

        _checkText.text = string.Join("   ",
            Dot(exp.ServerReady ? 2 : 0, srv), cv, tip, cp) + trk + up + ed;
    }

    /// <summary>実験パネルの下半分（準備チェック・主ボタン・副ボタン）。BuildUi から呼ぶ。</summary>
    private void BuildExperimentControls(RectTransform crt, float y, float inner)
    {
        y -= RowGap;
        AddRule(crt, y, inner);
        y -= RowGap;

        // --- 準備チェック（**2 行ぶんの高さを取る**）---
        // 常態（Server / CV / Tip / Real CP の 4 つ）は 1 行に収まる（実測 319px < 476px）。
        // 収まらなくなるのは Tracking・Upload・Virtual CP edited が足された異常時で、
        // 全部出ると 650px＝パネル幅の 1.4 倍になり、以前は文字が枠の外へはみ出していた。
        // best fit だけに任せると 10px まで縮んで読めなくなるので、**2 行めを許して**
        // 15px のまま折り返させる（650/476 → 2 行 = 36px ≤ 40px）。
        // 一番読ませたい警告が出ている局面なので、小さくするより 1 行ぶん背を高くする。
        const float CheckH = 40f;
        y -= CheckH / 2f;
        _checkText = AddText(NewUi("Check", crt, new Vector2(0, y), new Vector2(inner, CheckH)),
                             15, TextAnchor.MiddleLeft, ColText, true);
        y -= CheckH / 2f;
        y -= RowGap;

        // --- 主ボタン ---
        const float MainH = 52f;
        y -= MainH / 2f;
        float modeW = 118f, gap = 10f;
        float mainW = inner - modeW - gap;
        AddButtonAt(crt, new Vector2(-inner / 2f + mainW / 2f, y), new Vector2(mainW, MainH),
                    null, MainButtonAction, 21,
                    () => exp != null && exp.State == ExperimentController.TrialState.Running
                          ? "Abort" + Key("Y") : "Start trial" + Key("B"),
                    // **準備が揃うまで始めさせない**（CanStartTrial に測量済みが含まれる）
                    () => exp != null && (exp.State == ExperimentController.TrialState.Running
                                          || exp.CanStartTrial),
                    new Color(0.16f, 0.34f, 0.55f, 0.92f));
        AddButtonAt(crt, new Vector2(inner / 2f - modeW / 2f, y), new Vector2(modeW, MainH),
                    null, null, 15,
                    () => exp == null ? "" : exp.AllTrialsDone ? "done"
                          : $"{Mathf.Max(0, exp.PlannedTrials - exp.CompletedTrials)} left",
                    () => false,                       // 表示専用
                    new Color(1f, 1f, 1f, 0.06f));
        y -= MainH / 2f + 10f;

        // --- 副ボタン ---
        // **実験の流れに必要なものだけを置く。**
        // 送信は試行が自動で制御するので手動の送信ボタン（Auto / Send one）は要らず、
        // 押せば進行中の試行を壊すだけだった。手動位置合わせ（Manual / Unlock / Recenter）も
        // 自動位置合わせを評価する実験では使う場面が無く、誤爆すると測定が無効になる。
        // 残すのは「準備に要るもの（先端校正・CP の配置と保存）」と「見比べる（Splats）」だけ。
        const float SubH2 = 40f, SGap = 8f;
        float sbW = (inner - SGap * 2f) / 3f;
        float[] xs = { -(sbW + SGap), 0f, (sbW + SGap) };
        Color exBtn = new Color(1f, 1f, 1f, 0.10f);
        Color alBtn = new Color(1f, 1f, 1f, 0.06f);

        // 物理ボタンの割当は **文脈で変わる**（Y は 4 役、A/B は短長押しで 2 役ずつ）。
        // 以前はラベル側で割当タグを出し分けていたが、**押せない局面ではボタンごと無効にする**
        // ほうが正しい。出し分けだけだと、たとえば編集中に「Set Real CP」を Poke できてしまい、
        // 測定と編集が同時に走って右トリガーが両方に効く（記録と配置が同時に起きる）。
        // 無効にしてしまえば、ラベルの割当は「有効なときの割当」で常に正しい。
        Func<bool> running   = () => exp != null && exp.State == ExperimentController.TrialState.Running;
        Func<bool> surveying = () => cpMeasure != null && cpMeasure.IsRunning;
        Func<bool> editing   = () => cpEdit != null && cpEdit.IsRunning;

        y -= SubH2 / 2f;
        AddButtonAt(crt, new Vector2(xs[0], y), new Vector2(sbW, SubH2), null,
                    () => tipCalib?.ToggleCalibration(), 15,
                    () => (tipCalib != null && tipCalib.IsRunning ? "Finish calib" : "Calibrate tip")
                          + Key("Y"),
                    // Y が校正に割り当たるのは「試行中でも測量中でも編集中でもない」ときだけ
                    // （→ ExperimentController.HandleExperimentInput）。校正中は終了に要るので通す。
                    () => tipCalib != null
                          && (tipCalib.IsRunning || (!running() && !surveying() && !editing())),
                    exBtn);
        // Real CP の測定は **実験の前に 1 回だけ**。Virtual CP（モデル上）も Real CP（部屋）も
        // 試行では変わらないので、試行ごとに測り直す必要は無い。
        // **やり直しを "Re-set" とは書かない。** このプロジェクトの RESET は試行リセット
        // （UDP type=4）を指すので、押すと試行が飛ぶと読まれる。"Redo" にする。
        // **測定中の割当タグは A ではなく Y。** A 短押しは「仮置きを確定して次へ」に取られており、
        // 測定中に A を押しても中断しない（余分な 1 押しで 6 点の測量が消えるのを避けるため
        // → ExperimentController.HandleAButton）。中断は Y だけ。
        AddButtonAt(crt, new Vector2((xs[1] + xs[2]) / 2f, y), new Vector2(sbW * 2f + SGap, SubH2), null,
                    () => cpMeasure?.ToggleSurvey(), 15,
                    () => cpMeasure != null && cpMeasure.IsRunning
                          ? "Cancel" + Key("Y")
                          : (cpMeasure != null && cpMeasure.IsSurveyed ? "Redo Real CP" : "Set Real CP")
                            + Key("A tap"),
                    () => cpMeasure != null && !running() && !editing(), exBtn);
        y -= SubH2 + SGap;

        // --- model 側 CP の VR 編集（位置合わせ前の準備。任意）---
        // Editor のマウス配置は視線方向の奥行きが取れず、その誤差は系統誤差として報告値に乗る。
        // **位置合わせ済みでは押せない**: オーバーレイが重なって見える状態で動かすと、
        // 無意識に実物へ寄せてしまい誤差が 0 側にバイアスする（答えを見て定規を合わせるのと同じ）。
        //
        // **2 行に割る。** 物理ボタンの割当をラベルに添えると 1 行 4 分割では文字が隣へはみ出す
        // （ボタンの Text は Overflow なのでクリップされず、隣のラベルに重なって両方読めなくなる）。
        // 割当を出さないより、パネルが 1 行ぶん高くなるほうがましと判断した。
        // **`Skip` が要る**のは、直したいのが CP_04 だけという場合に、前へ進む手段が「置く」しか
        // 無いと CP_01..03 まで置き直す羽目になるため（合っている点を触れば精度が下がる）。
        //
        // **編集中の 3 つ（Skip / Revert this / Revert all）は物理ボタンに載せてある。**
        // 以前は Poke（空中のボタンを手で突く）専用だったが、40px の板を 3cm 以内まで突く操作は
        // 実機で狙えず、「機能が無い」と受け取られた。編集中は A 短押し・B 短押し・B 長押しが
        // どれも他の役を持たない（測量トグルと試行開始は編集中ブロック）ので、そこへ載せた。
        float eEdit = 288f;
        float eSkip = inner - eEdit - SGap;
        AddButtonAt(crt, new Vector2(-inner / 2f + eEdit / 2f, y), new Vector2(eEdit, SubH2), null,
                    () => cpEdit?.ToggleEdit(), 15,
                    // 開始は A 長押しのみ。終了は Y でもできる（測量の「中断」と同じ指）
                    () => cpEdit != null && cpEdit.IsRunning
                          ? "Done editing" + Key("Y / A hold")
                          : "Edit Virtual CP" + Key("A hold"),
                    () => cpEdit != null && (cpEdit.IsRunning || string.IsNullOrEmpty(cpEdit.BlockReason)),
                    exBtn);
        // A 短押しは 1 つの意味（「この点を確定して次へ」）だが、**仮置きの有無でラベルが変わる**。
        // 測定中も同じボタンが確定を担うので、ここは編集専用にせず両モードから使う。
        AddButtonAt(crt, new Vector2(inner / 2f - eSkip / 2f, y), new Vector2(eSkip, SubH2), null,
                    () => { if (cpMeasure != null && cpMeasure.IsRunning) cpMeasure.ConfirmPoint();
                            else cpEdit?.AcceptCurrent(); }, 15,
                    () => ((cpMeasure != null && cpMeasure.HasPending) || (cpEdit != null && cpEdit.HasPending)
                           ? "Confirm" : "Skip") + Key("A"),
                    // 仮置きがあるなら常に押せる。無いときの Skip は編集中だけの機能で、
                    // 末尾では「次」が無いので落とす（押しても Reject が出るだけ）。
                    () => (cpMeasure != null && cpMeasure.HasPending)
                          || (cpEdit != null && cpEdit.IsRunning
                              && (cpEdit.HasPending || !cpEdit.AtLastPoint)), exBtn);
        y -= SubH2 + SGap;

        float eRev = (inner - SGap) / 2f;
        AddButtonAt(crt, new Vector2(-inner / 2f + eRev / 2f, y), new Vector2(eRev, SubH2), null,
                    () => cpEdit?.RevertCurrent(), 15,
                    () => "Revert this" + Key("B"),
                    () => cpEdit != null && cpEdit.IsRunning && cpEdit.CurrentIsEdited, exBtn);
        AddButtonAt(crt, new Vector2(inner / 2f - eRev / 2f, y), new Vector2(eRev, SubH2), null,
                    () => cpEdit?.RevertAll(), 15,
                    () => (cpEdit != null && cpEdit.EditedCount > 0
                           ? $"Revert all ({cpEdit.EditedCount})" : "Revert all") + Key("B hold"),
                    () => cpEdit != null && cpEdit.IsRunning && cpEdit.EditedCount > 0, exBtn);
        y -= SubH2 + SGap;

        // 3DGS の表示トグルだけは残す。パススルーと重ねて見比べるのが位置合わせの確認そのもの。
        // **いまどちらなのかをラベルに出す。** 起動直後は非表示で始まる（startHidden）ので、
        // 「Show / hide」だと消えているのが仕様なのか故障なのか分からない。
        // 隣にデモ展示モードへの入口を置く（CP も先端校正も要らないので、最初の画面からそのまま入れる。
        // 行を足さずに半分に割ったので、パネルの高さは変えていない）。
        float half = (inner - SGap) / 2f;
        AddButtonAt(crt, new Vector2(-inner / 2f + half / 2f, y), new Vector2(half, SubH2), null,
                    () => align?.ToggleSplatVisible(), 15,
                    () => (align != null && align.SplatVisible ? "Hide splats" : "Show splats") + Key("X"),
                    null, alBtn);
        AddButtonAt(crt, new Vector2(inner / 2f - half / 2f, y), new Vector2(half, SubH2), null,
                    () => exp?.SwitchToDemo(), 15,
                    () => "Demo mode" + Key("both sticks hold"),
                    () => exp != null && exp.CanSwitchToDemo, alBtn);
    }

    private void MainButtonAction()
    {
        if (exp == null) return;
        if (exp.State == ExperimentController.TrialState.Running) exp.AbortTrial();
        else exp.StartTrial();
    }

    /// <summary>実験コンポーネントが無いシーン（位置合わせ単体の動作確認）用のボタン群。
    /// **2 列 3 行**。物理ボタンの割当をラベルに添えるので 3 列だと文字が隣へはみ出す
    /// （ボタンの Text は Overflow でクリップされない）。この局面では姿勢操作が全部生きている
    /// （PoseControls = Full）ので、割当は文脈で変わらず常に出してよい。</summary>
    private void BuildAlignmentOnlyButtons(RectTransform crt, float y, float inner)
    {
        const float H = 40f, G = 8f;
        float w = (inner - G) / 2f;
        float xl = -inner / 2f + w / 2f, xr = inner / 2f - w / 2f;
        Color c = new Color(1f, 1f, 1f, 0.06f);
        y -= H / 2f;
        AddButtonAt(crt, new Vector2(xl, y), new Vector2(w, H), null, () => align?.ToggleSplatVisible(), 15,
                    () => (align != null && align.SplatVisible ? "Splats ✓" : "Splats") + Key("X"), null, c);
        AddButtonAt(crt, new Vector2(xr, y), new Vector2(w, H), null, () => uploader?.ToggleAuto(), 15,
                    () => (uploader != null && uploader.AutoCaptureOn ? "Auto ✓" : "Auto") + Key("L grip hold"), null, c);
        y -= H + G;
        AddButtonAt(crt, new Vector2(xl, y), new Vector2(w, H), null, () => uploader?.SendOne(), 15,
                    () => "Send one" + Key("L grip tap"), null, c);
        AddButtonAt(crt, new Vector2(xr, y), new Vector2(w, H), null, () => align?.Unlock(), 15,
                    () => "Unlock" + Key("Y"), null, c);
        y -= H + G;
        AddButtonAt(crt, new Vector2(xl, y), new Vector2(w, H), null, () => align?.RecenterInFront(), 15,
                    () => "Recenter" + Key("R stick"), null, c);
        AddButtonAt(crt, new Vector2(xr, y), new Vector2(w, H), null, () => align?.AutoLevelAndFloorSnap(), 15,
                    () => "Level + floor" + Key("L stick"), null, c);
    }
}
