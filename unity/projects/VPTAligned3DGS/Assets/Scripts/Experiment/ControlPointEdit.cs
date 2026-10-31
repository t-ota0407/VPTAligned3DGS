using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

/// <summary>
/// **Virtual CP（3DGS モデル上の CP）を VR の中で置き直す**準備ステップ（HUD 名 **Edit Virtual CP**）。
/// 対になる実物側は [[ControlPointMeasure]]（**Set Real CP**）。用語はあちらの class doc を参照。
///
/// 誤差は |T(local) − tip| で、local（Virtual CP の座標）と tip（Real CP の座標）は
/// どちらもグラウンドトゥルース側の値。tip の品質には既に手を打ってあるが
/// （[[ControllerTipCalibration]] の先端校正・移動量ゲート・[[TrackingOriginWatcher]]）、
/// local は **Editor の Scene ビューでマウス配置するだけ**だった。マウス配置は視線方向の
/// 奥行きが取れず、その誤差はそのまま**系統誤差として報告値に乗る**。先端未校正と同種の問題。
///
/// VR で置き直すほうが奥行きが取れる: 実寸で立ち、両眼視差と頭を動かしたときの運動視差が使え、
/// **先端を実際にその位置へ持っていく**。実物側の測量とまったく同じ所作になる。
/// 位置合わせ前でも、スティックでシーンを空きスペースへ寄せれば手が届く
/// （<see cref="AlignmentManager.PoseControlMode.SticksOnly"/>）。
///
/// **なぜ「位置合わせ済みでは編集させない」か（この設計の核）。**
/// local は T の推定に一切使っていない注釈なので、**モデルだけを見て**決める限り評価は成立する。
/// 崩れるのは 1 つだけ ——
///   オーバーレイが実物に重なって見えている状態で local を動かすと、無意識に「実物の位置」へ
///   寄せてしまい、誤差が 0 側にバイアスする。答えを見ながら定規を合わせるのと同じ。
/// だから <see cref="AlignmentManager.HasAcceptedSim3"/> か Locked の間は編集を**禁止**する。
/// 安全な窓は準備段階（未アライン）だけ。
///
/// **編集したら測量を無効化する。** PC 側の解析は local を `cp_survey` イベントに載った値から
/// 取る（記録した瞬間のスナップショット）。測量後に model 側を動かすと、シーン上の値と PC の値が
/// 黙って食い違う。だから <see cref="ControlPointMeasure.InvalidateSurvey"/> を呼んで測り直させる。
///
/// **結果の戻し先。** 終了時に全点のモデル座標を UDP イベントで PC へ送り、`run.py` が
/// `received/controlpoints_&lt;modelId&gt;.json` を [[ControlPointStore]] と同じ形で書く。
/// Editor の Control Points ウィンドウの「JSON を読み込んで反映」でそのまま取り込めるので、
/// adb pull なしでシーンへ恒久化できる。端末内にも同じ JSON を冗長化として置く。
///
/// **置き直しは 2 段階（仮置き → 確定）。** 右トリガーで球はその場へ動くが、まだ確定していない
/// （→ <see cref="Place"/> / <see cref="AcceptCurrent"/>）。A で確定して初めて記録され、次の点へ進む。
/// 1 回のトリガーで確定していた頃は、**押した瞬間の先端位置がそのまま最終値**だった。
/// トリガーを引く動作自体でコントローラが数 mm 動くうえ、置いた球が本当にモデル上の狙った特徴に
/// 載っているかは**頭を動かして別の角度から**見ないと分からない（奥行きは両眼視差と運動視差で
/// 取るのが本題なのに、その確認の機会が無かった）。仮置きを挟めば、納得するまで右トリガーで
/// 置き直してから確定できる。確定するまでログも `_edited` も測量の無効化も起きない。
///
/// 見えるもの: 球のラベルは `VCP_nn`（実物側の `RCP_nn` と文字で見分ける → [[CpGizmos.VirtualLabel]]）。
/// 色は 現在=黄 / **仮置き=白** / 未編集=シアン / 編集済み=緑 / 仮座標の疑い=赤。
/// 動かした点には**元の位置に灰色のゴースト点**（`VCP_nn (old)`。どれだけ動かしたかが見える）。
/// </summary>
public class ControlPointEdit : MonoBehaviour
{
    [Header("References（未設定なら自動取得）")]
    [SerializeField] private ControlPointMeasure measure;
    [SerializeField] private AlignmentManager align;
    [SerializeField] private ControllerTipCalibration tipCalib;
    [SerializeField] private GaussianSplatController splatController;
    [SerializeField] private ExperimentController experiment;
    [SerializeField] private Transform rightControllerAnchor;

    [Header("Settings")]
    [Tooltip("先端が未校正でも編集を許す（既定 false: 数 cm のオフセットが local に焼き込まれる）")]
    [SerializeField] private bool allowUncalibrated = false;
    [Tooltip("ビルド値からこの距離[m]以上動かしたら警告する（誤爆・取り違えの疑い）")]
    [SerializeField] private float warnMoveM = 1.0f;
    [Tooltip("端末内にも controlpoints_<modelId>.json を書く（PC へ届かなかったときの控え）")]
    [SerializeField] private bool writeDeviceJson = true;

    private ControlPointMarker[] _markers;
    private CpGizmos.Layer _ghost;                 // 編集前の位置に残す灰色の点

    private bool _running;
    private int  _idx;
    private bool _invalidatedSurvey;               // この編集セッションで測量を無効化済みか
    private string _lastError = "";
    private float  _lastErrorT = -99f;

    // ---- 仮置き（右トリガーで置いてから A で確定するまで）----
    // 見た目のために transform は動かすが、**確定するまで _edited にも入れず、ログも出さず、
    // 測量も無効化しない。** 取り消したら _pendingRestore へ戻すので、シーンは押す前の状態に返る。
    private bool    _hasPending;
    private Vector3 _pendingRestore;               // 仮置きする直前の localPosition（取り消しの戻り先）
    private int     _pendingRetries;               // その点を置き直した回数（確定行に残す）

    // ビルド値（＝アプリ起動時のシーン上の値）。Revert の戻り先。
    // **名前で持つ**: RefreshMarkers で配列が作り直されても、編集後の値を「元の値」として
    // 取り込んでしまわないようにするため。
    private readonly Dictionary<string, Vector3> _orig = new Dictionary<string, Vector3>();
    private readonly HashSet<string> _edited = new HashSet<string>();

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---------------- 公開状態 ----------------

    public bool IsRunning  => _running;
    /// <summary>この起動で置き直した点の数（0 なら何も送らない）。</summary>
    public int  EditedCount => _edited.Count;
    public int  MarkerCount => _markers != null ? _markers.Length : 0;
    public int  CurrentIndex => _idx;

    /// <summary>いま列のどこにいるか 0..1（HUD の進捗バー用）。
    /// **測量と違って「済んだ数」ではなく「何点目か」を出す。** 編集は全点を触る作業ではなく、
    /// 直したい点まで送って直す作業なので、進み具合＝列の位置。実機で「置いたのに次へ
    /// 進んだのが分からない」となったのは、ここに何も出していなかったのが原因。</summary>
    public float Progress => MarkerCount > 0 ? (_idx + 1) / (float)MarkerCount : 0f;

    /// <summary>列の最後の点にいるか（HUD が「Y で終了」を出す条件）。</summary>
    public bool AtLastPoint => _markers != null && _markers.Length > 0 && _idx >= _markers.Length - 1;

    /// <summary>右トリガーで置いたが、まだ A で確定していない点を抱えているか。
    /// HUD はこれで指示文と A の意味（確定 / スキップ）を切り替える。</summary>
    public bool HasPending => _running && _hasPending;
    /// <summary>いまの仮置きを何回置き直したか（0 = 一発で決めた）。</summary>
    public int  PendingRetries => _pendingRetries;

    /// <summary>現在の点がこのセッションで置き直されているか。</summary>
    public bool CurrentIsEdited => _markers != null && _markers.Length > 0
                                   && _edited.Contains(_markers[Mathf.Clamp(_idx, 0, _markers.Length - 1)].name);
    /// <summary>いま対象にしている CP の**内部名**（`CP_01`）。ログ・JSON はこちらを使う。</summary>
    public string CurrentName => (_markers != null && _markers.Length > 0)
                                 ? _markers[Mathf.Clamp(_idx, 0, _markers.Length - 1)].name : "-";
    /// <summary>その CP を HUD に出すときの表示名（`VCP_01`）。ワールドの球のラベルと同じ文字に
    /// して、HUD の指示と視界の中の球を突き合わせられるようにする（実物側は `RCP_01`）。</summary>
    public string CurrentLabel => CpGizmos.VirtualLabel(CurrentName);

    /// <summary>現在の点がビルド値からどれだけ動いたか[m]（モデル座標なのでほぼ実寸）。</summary>
    public float CurrentMoveM => MoveOf(_idx);

    public bool   RecentlyRejected => Time.realtimeSinceStartup - _lastErrorT < 2.5f;
    public string RejectReason     => RecentlyRejected ? _lastError : "";

    /// <summary>姿勢が適用済みか（＝オーバーレイが実物に重なって見えている＝編集は危険）。</summary>
    private bool PoseApplied =>
        align != null && (align.HasAcceptedSim3 || align.State == AlignmentManager.AlignState.Locked);

    /// <summary>編集を始められるか。始められないなら理由を返す（空文字なら OK）。</summary>
    public string BlockReason
    {
        get
        {
            if (measure == null || measure.MarkerCount == 0) return "No Virtual CP placed";
            if (measure.IsRunning) return "Finish or cancel Set Real CP first";
            if (experiment != null && experiment.State == ExperimentController.TrialState.Running)
                return "A trial is running";
            // ここが本質的な禁止条件。理由は class doc を参照。
            if (PoseApplied)
                return "Pose is already aligned — editing now would bias the result. Reset the pose first.";
            if (!allowUncalibrated && !(tipCalib != null && tipCalib.IsCalibrated))
                return tipCalib != null && tipCalib.HasStaleCalibration
                       ? "Tip offset is from a previous session. Calibrate now."
                       : "Calibrate the controller tip first";
            if (rightControllerAnchor == null) return "Right controller not detected";
            return "";
        }
    }

    /// <summary>置き直しの結果が怪しいときの警告（HUD 用）。空なら問題なし。</summary>
    public string MoveWarning
    {
        get
        {
            if (_markers == null) return "";
            var far = new List<string>();
            foreach (var m in _markers)
                if (m != null && _edited.Contains(m.name) && MoveOfName(m.name) > warnMoveM)
                    far.Add(CpGizmos.VirtualLabel(m.name));
            return far.Count == 0 ? ""
                   : $"{string.Join(", ", far)} moved > {warnMoveM:F1} m — check you touched the right feature";
        }
    }

    // ---------------- ライフサイクル ----------------

    private void Start()
    {
        if (measure == null)         measure = FindFirstObjectByType<ControlPointMeasure>();
        if (align == null)           align = FindFirstObjectByType<AlignmentManager>();
        if (tipCalib == null)        tipCalib = FindFirstObjectByType<ControllerTipCalibration>();
        if (splatController == null) splatController = FindFirstObjectByType<GaussianSplatController>();
        if (experiment == null)      experiment = FindFirstObjectByType<ExperimentController>();
        var rig = FindFirstObjectByType<OVRCameraRig>();
        if (rightControllerAnchor == null && rig != null) rightControllerAnchor = rig.rightControllerAnchor;
    }

    /// <summary>マーカー配列とビルド値を測量側から借りて揃える。
    /// 球を作るのは <see cref="ControlPointMeasure"/> だけなので、ここでは並びを共有するだけ。</summary>
    private bool Sync()
    {
        if (measure == null) return false;
        _markers = measure.Markers;
        if (_markers == null || _markers.Length == 0) return false;

        // ビルド値は**初めて見た時点の値**を採る。以後は上書きしない（編集後の値を
        // 「元の値」にしてしまうと Revert が効かなくなる）。
        foreach (var m in _markers)
            if (m != null && !_orig.ContainsKey(m.name)) _orig[m.name] = m.transform.localPosition;

        if (_ghost == null && measure.Gizmos != null)
            _ghost = measure.Gizmos.NewLayer("Ghost", CpGizmos.ColGhost, 0.9f);
        return true;
    }

    private void Update()
    {
        if (!_running) return;

        // 編集中に Sim(3) が返ってきて適用された場合は、そこで打ち切る。
        // 以降の編集はオーバーレイを見ながらの調整になり、バイアスの原因になる。
        if (PoseApplied)
        {
            Debug.LogWarning("[ControlPointEdit] 姿勢が適用されたため編集を終了します"
                             + "（重なって見える状態での編集は誤差を 0 側に歪めます）");
            Finish();
            return;
        }

        // 右トリガー = 仮置き（何度でも置き直せる）／ A = 確定して次へ（ExperimentController から）。
        // 左トリガーは「1 手戻す」で統一する: 仮置きがあればそれを捨て、無ければ前の点へ戻る。
        if (OVRInput.GetDown(OVRInput.RawButton.RIndexTrigger)) Place();
        if (OVRInput.GetDown(OVRInput.RawButton.LIndexTrigger))
        {
            if (_hasPending) DiscardPending();
            else             Back();
        }
    }

    // ---------------- 開始・終了 ----------------

    /// <summary>編集モードの開始／終了トグル（HUD ボタン・A 長押し用）。</summary>
    public void ToggleEdit()
    {
        if (_running) Finish();
        else          StartEdit();
    }

    public void StartEdit()
    {
        string block = BlockReason;
        if (!string.IsNullOrEmpty(block))
        {
            Reject(block);
            Debug.LogWarning("[ControlPointEdit] 編集を開始できません: " + block);
            return;
        }
        if (!Sync()) { Reject("No Virtual CP placed"); return; }

        _running = true;
        _idx = 0;
        _invalidatedSurvey = false;
        _hasPending = false; _pendingRetries = 0;
        measure.ExternalGizmoHold = true;              // 測量中でなくても球を出し続けてもらう
        if (align != null) align.SetSplatVisible(true); // 起動直後は非表示なのでここで出す
        Repaint();
        Haptics.Tick(this);                       // A 長押しで入ったときの合図
        ExperimentLogger.LogStatic("cp_model_edit",
            "\"action\":\"start\",\"points\":" + _markers.Length +
            ",\"aligned\":" + (PoseApplied ? "true" : "false"));
        Debug.Log($"[ControlPointEdit] model 側 CP の編集を開始（{_markers.Length} 点）。"
                  + "スティックでシーンを手の届く位置へ寄せ、各 CP に対応するモデル上の特徴へ"
                  + "先端を当てて右トリガーで仮置き → 別の角度から確かめて A で確定。"
                  + "左トリガーで仮置きの取り消し／1 つ戻る。");
    }

    /// <summary>編集を終了し、結果を PC と端末へ書き出す。
    ///
    /// **中断（破棄）は用意しない。** 置き直しは押した瞬間にシーンへ反映されるので、
    /// 「中断」に破棄の意味を持たせると「中断したのにシーンだけ変わっていて PC には
    /// 送られていない」という最悪の食い違いを作る。戻したいときは Revert を使う。</summary>
    public void Finish()
    {
        if (!_running) return;
        // 未確定の仮置きを抱えたまま抜けたら**捨てる**（確定していない＝まだ置き直しの途中）。
        // 抜けるのを拒む選択肢は取れない: 姿勢が適用されたときの強制終了（→ Update）は
        // 断れないので、そこで残ると「送られていないのにシーンだけ動いている」状態になる。
        if (_hasPending)
        {
            DiscardPending();
            ExperimentLogger.LogStatic("cp_model_edit", "\"action\":\"pending_dropped\"" +
                ",\"name\":\"" + CurrentName + "\"");
            Debug.LogWarning("[ControlPointEdit] 未確定の仮置き（" + CurrentName + "）は破棄しました");
        }
        _running = false;
        if (measure != null)
        {
            measure.ExternalGizmoHold = false;
            measure.RepaintSurveyColors();             // 借りていた色を測量側の意味に戻す
        }
        _ghost?.ClearAll();
        Haptics.Tick(this);

        if (_edited.Count == 0)
        {
            ExperimentLogger.LogStatic("cp_model_edit", "\"action\":\"end\",\"edited\":0");
            Debug.Log("[ControlPointEdit] 編集終了（変更なし）");
            return;
        }
        Emit();
    }

    // ---------------- 置き直し ----------------

    /// <summary>右トリガー: 球を先端の位置へ**仮置き**する（まだ確定しない）。
    ///
    /// 何度でも押し直せる。球は動くが `_edited` にも入らずログも出ないので、A を押すまでは
    /// 「まだ何も置いていない」のと同じ状態を保つ。取り消せば <see cref="_pendingRestore"/> へ戻る。</summary>
    private void Place()
    {
        if (!TryGetTip(out Vector3 tip)) return;
        var m = _markers[_idx];
        var parent = m.transform.parent;

        // splatRoot の**負の一様スケール（反射, §6）**は InverseTransformPoint が正しく扱う。
        // ここで軸を手で反転してはいけない（座標規約が二重に効いてしまう）。
        Vector3 local = parent != null ? parent.InverseTransformPoint(tip) : tip;
        Vector3 orig  = Orig(m.name);

        // 戻り先は「仮置きを始める前の値」。既に確定済みの置き直しがある点を置き直しても、
        // 取り消せばその確定値に戻る（ビルド値まで巻き戻さない ── それは B の Revert の役目）。
        if (_hasPending) _pendingRetries++;
        else { _hasPending = true; _pendingRetries = 0; _pendingRestore = m.transform.localPosition; }

        m.transform.localPosition = local;

        Haptics.Tick(this);
        int placed = _idx;
        CpGizmos.Draw(() =>
        {
            // 仮置きの間は球を白にする（黄＝いま触る点／緑＝確定済み と区別する）。
            measure?.Gizmos?.SetColor(placed, CpGizmos.ColPreview);
            // 元の位置のゴーストは仮置きの時点から出す（どれだけ動かしたかを見て判断するため）。
            if (Vector3.Distance(local, orig) > 0.01f)
                _ghost?.SetLocal(placed, parent, orig, CpGizmos.VirtualLabel(m.name) + " (old)");
            else
                _ghost?.Clear(placed);
        });
        Debug.Log($"[ControlPointEdit] {m.name} を仮置き local={local}"
                  + $"（ビルド値から {Vector3.Distance(local, orig) * 100f:F1} cm）"
                  + (_pendingRetries > 0 ? $"（{_pendingRetries} 回目の置き直し）" : "")
                  + " ── A で確定、右トリガーで置き直し、左トリガーで取り消し");
    }

    /// <summary>A: 仮置きがあれば確定して次へ、無ければ置かずに次へ（<see cref="Skip"/>）。
    /// 「A = この点を確定して次へ」という 1 つの意味に揃える（実物側の測量と同じ）。</summary>
    public void AcceptCurrent()
    {
        if (!_running) return;
        if (_hasPending) Commit();
        else             Skip();
    }

    /// <summary>左トリガー: 仮置きを捨てて押す前の位置へ戻す（点は進めない）。</summary>
    private void DiscardPending()
    {
        if (!_hasPending) return;
        int i = _idx;
        var m = _markers[i];
        if (m != null) m.transform.localPosition = _pendingRestore;
        _hasPending = false; _pendingRetries = 0;
        // 確定済みの点を置き直していた場合はゴーストを戻す必要があるので、Repaint の前に描き直す。
        CpGizmos.Draw(() =>
        {
            var parent = m != null ? m.transform.parent : null;
            Vector3 orig = m != null ? Orig(m.name) : Vector3.zero;
            if (m != null && _edited.Contains(m.name) && Vector3.Distance(_pendingRestore, orig) > 0.01f)
                _ghost?.SetLocal(i, parent, orig, CpGizmos.VirtualLabel(m.name) + " (old)");
            else
                _ghost?.Clear(i);
        });
        Repaint();                      // 白 → 黄（いま触る点）へ戻す
        Haptics.RejectRight(this);
        Debug.Log("[ControlPointEdit] 仮置きを取り消しました → " + CurrentName + " を置き直してください");
    }

    /// <summary>仮置きを確定する（<see cref="AcceptCurrent"/> からのみ呼ぶ）。
    /// **ここで初めて** ログ・`_edited`・測量の無効化が起きる。</summary>
    private void Commit()
    {
        var m = _markers[_idx];
        Vector3 local = m.transform.localPosition;
        Vector3 orig  = Orig(m.name);
        int retries = _pendingRetries;
        _hasPending = false; _pendingRetries = 0;

        _edited.Add(m.name);
        InvalidateSurveyOnce();

        ExperimentLogger.LogStatic("cp_model_edit",
            "\"action\":\"place\",\"name\":\"" + m.name + "\"" +
            ",\"local\":[" + F(local.x) + "," + F(local.y) + "," + F(local.z) + "]" +
            ",\"from\":["  + F(orig.x)  + "," + F(orig.y)  + "," + F(orig.z)  + "]" +
            ",\"moved_m\":" + F(Vector3.Distance(local, orig)) +
            // 確定までに何度置き直したか（未知の鍵なので解析側は無視する）。
            ",\"retries\":" + retries +
            ",\"tip_source\":\"" + (tipCalib != null ? tipCalib.State.ToString() : "None") + "\"");
        Debug.Log($"[ControlPointEdit] {m.name} を確定 local={local}"
                  + $"（ビルド値から {Vector3.Distance(local, orig) * 100f:F1} cm）");

        Haptics.Tick(this);

        // **状態遷移を先に済ませ、描画は後ろに置く。** 逆順だった間、実機ではゴーストの
        // マテリアル生成が例外を投げてここへ到達せず、置き直しても次の点へ進まなかった
        // （`cp_model_edit action=place` のログすら出なかった → <see cref="CpGizmos.Draw"/>）。
        // Advance の中の Repaint が、仮置きの白を確定の緑へ塗り替える。
        Advance();
    }

    private void Back()
    {
        if (_idx <= 0) { Reject("Already at the first point"); return; }
        _idx--;
        Repaint();
        Haptics.Tick(this);
    }

    private void Advance()
    {
        if (_idx < _markers.Length - 1) _idx++;
        Repaint();
    }

    /// <summary>置かずに次の点へ（触れない特徴・既に正しい点を飛ばす）。
    /// **末尾では黙って止まらない。** 物理ボタンに載せた以上、押して何も起きないと
    /// 「ボタンが効いていない」と読まれる（実機で編集モードがそう受け取られた）。</summary>
    public void Skip()
    {
        if (!_running) return;
        if (AtLastPoint) { Reject("Last point — press Y to finish editing"); return; }
        Advance();
        Haptics.Tick(this);
    }

    /// <summary>現在の点をビルド値へ戻す。仮置き中なら、まずその仮置きを取り消す
    /// （確定していない置き直しを飛び越してビルド値まで戻すと、何が取り消されたのか見えない）。</summary>
    public void RevertCurrent()
    {
        if (!_running || _markers == null) return;
        if (_hasPending) { DiscardPending(); return; }
        if (!CurrentIsEdited) { Reject($"{CurrentLabel} has not been moved — nothing to revert"); return; }
        Revert(_markers[_idx].name);
        Repaint();
        Haptics.Tick(this);
    }

    /// <summary>この起動で動かした点をすべてビルド値へ戻す。</summary>
    public void RevertAll()
    {
        if (_markers == null) return;
        if (_hasPending) DiscardPending();          // 未確定の置き直しも当然無かったことにする
        if (_edited.Count == 0) { Reject("Nothing to revert"); return; }
        foreach (var name in new List<string>(_edited)) Revert(name);
        Repaint();
        Haptics.Tick(this);
    }

    private void Revert(string name)
    {
        if (!_edited.Contains(name)) return;
        int i = IndexOf(name);
        if (i < 0) return;
        _markers[i].transform.localPosition = Orig(name);
        _edited.Remove(name);
        CpGizmos.Draw(() => _ghost?.Clear(i));
        InvalidateSurveyOnce();
        ExperimentLogger.LogStatic("cp_model_edit_revert", "\"name\":\"" + name + "\"");
        Debug.Log("[ControlPointEdit] " + name + " をビルド値へ戻しました");
    }

    // ---------------- 書き出し ----------------

    /// <summary>全点のモデル座標を PC へ送り、端末にも控えを書く。
    ///
    /// **1 点 1 行**で送る。[[UDPManager]] は 1100 B を超えるイベントを捨てるので、
    /// 全点を 1 行にまとめると点数が増えたときに黙って落ちる（測量の cp_survey と同じ流儀）。
    /// 締めの cp_model_edit_done が届いた時点で PC 側が JSON を組み立てる。</summary>
    private void Emit()
    {
        string modelId = splatController != null ? splatController.ModelId : "default";
        int n = _markers.Length;

        ExperimentLogger.LogStatic("cp_model_edit_done",
            "\"phase\":\"begin\",\"model\":\"" + Esc(modelId) + "\",\"points\":" + n +
            ",\"edited\":" + _edited.Count);

        for (int i = 0; i < n; i++)
        {
            var m = _markers[i];
            if (m == null) continue;
            Vector3 p = m.transform.localPosition;
            ExperimentLogger.LogStatic("cp_model_point",
                "\"model\":\"" + Esc(modelId) + "\",\"i\":" + i + ",\"name\":\"" + m.name + "\"" +
                ",\"p\":[" + F(p.x) + "," + F(p.y) + "," + F(p.z) + "]" +
                ",\"edited\":" + (_edited.Contains(m.name) ? "true" : "false"));
        }

        ExperimentLogger.LogStatic("cp_model_edit_done",
            "\"phase\":\"end\",\"model\":\"" + Esc(modelId) + "\",\"points\":" + n +
            ",\"edited\":" + _edited.Count);

        WriteDeviceJson(modelId);
        Debug.Log($"[ControlPointEdit] 編集終了: {_edited.Count}/{n} 点を変更し PC へ送信しました。"
                  + "PC の received/controlpoints_" + modelId + ".json を Editor の "
                  + "Control Points ウィンドウで取り込んでシーンへ恒久化してください。");
    }

    /// <summary>端末内にも同じ JSON を書く（PC へ届かなかったときの控え。adb pull で回収）。
    /// **起動時に自動復元はしない** ——「ビルドしたシーンが model 側の正」という不変条件を保つ。</summary>
    private void WriteDeviceJson(string modelId)
    {
        if (!writeDeviceJson) return;
        var store = new ControlPointStore { modelId = modelId };
        foreach (var m in _markers)
            if (m != null) store.points.Add(ControlPointStore.Entry.From(m.name, m.transform.localPosition));

        string path = System.IO.Path.Combine(Application.persistentDataPath,
                                             ControlPointStore.FileName(modelId));
        if (ControlPointStore.Save(path, store, out string err))
            Debug.Log("[ControlPointEdit] 端末内に保存: " + path);
        else
            Debug.LogWarning("[ControlPointEdit] 端末内保存に失敗: " + err);
    }

    // ---------------- 補助 ----------------

    /// <summary>コントローラ先端のワールド位置（校正済みオフセットを適用）。</summary>
    private bool TryGetTip(out Vector3 tip)
    {
        if (ControllerTipCalibration.TryGetTipWorld(tipCalib, rightControllerAnchor, allowUncalibrated,
                                                    out tip, out string why))
            return true;
        Reject(why);
        return false;
    }

    /// <summary>編集したら測量を無効化する（PC 側の local はスナップショットなので食い違う）。
    /// 1 編集セッションにつき 1 回だけ。</summary>
    private void InvalidateSurveyOnce()
    {
        if (_invalidatedSurvey || measure == null) return;
        _invalidatedSurvey = true;
        measure.InvalidateSurvey("Virtual CPs were edited — set the Real CPs again");
    }

    /// <summary>編集モードの意味で球を塗る:
    /// 黄=いま触る点 / 緑=置き直し済み / 赤=仮座標の疑い / シアン=未編集。</summary>
    private void Repaint()
    {
        var g = measure != null ? measure.Gizmos : null;
        if (g == null || !g.Built || _markers == null) return;
        CpGizmos.Draw(() =>
        {
            g.SetCurrent(_running ? _idx : -1);    // 「次はここ」の棒を立てる点
            for (int i = 0; i < _markers.Length; i++)
            {
                Color c = i == _idx                          ? CpGizmos.ColCurrent
                        : _edited.Contains(_markers[i].name) ? CpGizmos.ColDone
                        : measure.IsSuspect(i)               ? CpGizmos.ColSuspect
                                                             : CpGizmos.ColPending;
                g.SetColor(i, c);
            }
        });
    }

    private Vector3 Orig(string name) =>
        _orig.TryGetValue(name, out var v) ? v : Vector3.zero;

    private int IndexOf(string name)
    {
        if (_markers == null) return -1;
        for (int i = 0; i < _markers.Length; i++)
            if (_markers[i] != null && _markers[i].name == name) return i;
        return -1;
    }

    private float MoveOf(int i) =>
        (_markers != null && i >= 0 && i < _markers.Length && _markers[i] != null)
        ? MoveOfName(_markers[i].name) : 0f;

    private float MoveOfName(string name)
    {
        int i = IndexOf(name);
        if (i < 0) return 0f;
        return Vector3.Distance(_markers[i].transform.localPosition, Orig(name));
    }

    private void Reject(string why)
    {
        _lastError = why;
        _lastErrorT = Time.realtimeSinceStartup;
        Haptics.RejectRight(this);
    }

    private static string F(float v) => v.ToString("F5", Inv);
    private static string Esc(string s) => (s ?? "").Replace("\\", "/").Replace("\"", "'");
}
