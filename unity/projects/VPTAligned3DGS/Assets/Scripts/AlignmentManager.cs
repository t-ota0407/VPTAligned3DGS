using UnityEngine;

/// <summary>
/// VPT ⇄ 3DGS 位置合わせマネージャ。3DGS ルート Transform（GaussianSplats）の姿勢
/// （相似変換 T_{m→w}: 位置・回転・符号付き等方スケール）は、すべてここを通して書き換える。
///
/// 姿勢が変わる経路は 3 つ:
///   - 自動位置合わせ … PC の Sim(3)（<see cref="UDPManager.OnSim3Result"/>）を健全性と改悪防止で採否し、
///                       連続一致で確定する（→ ImplementationSpec.md §5）。確定で <see cref="OnAutoAligned"/>。
///   - アンカー追従   … [[AnchorPersistence]] が保存済みアンカーから再構成した姿勢（<see cref="IsAnchored"/> の間だけ）。
///   - 手動操作       … Touch コントローラ（下表。Specification.md の Stage E / 手動フォールバック）。
///
/// 手動操作（Touch コントローラ / OVRInput）:
///   右スティック        : 頭の向き基準で水平移動（前後・左右）
///   左スティック Y       : 垂直移動（上下）
///   左スティック X       : ヨー回転（頭の位置を軸に world を回す）
///   右トリガー           : 拡大 / 左トリガー: 縮小（頭の位置を Pivot に等方スケール）
///   A(One)               : 確定（Lock）
///   B(Two)               : 初期姿勢にリセット
///   X(Three)             : 3DGS 表示/非表示トグル
///   Y(Four)              : 再調整（Unlock）
///   右スティック押込      : 目の前に呼び寄せ（Recenter）
///   左スティック押込      : 水平化＋床スナップ（Auto-level / M2）
///
/// 受付範囲は <see cref="PoseControls"/> で切り替わる（実験・デモが局面に応じて毎フレーム設定する）。
/// CP 測量・編集中は SticksOnly になり、スティック操作（移動・ヨー・呼び寄せ・水平化）だけが残る。
/// 表示トグル X は常に効く。
/// </summary>
public class AlignmentManager : MonoBehaviour
{
    public enum AlignState { Adjusting, Locked }

    [Header("References")]
    [Tooltip("位置合わせ対象（GaussianSplats ルート Transform）。未設定なら GaussianSplatController から取得。")]
    [SerializeField] private Transform splatRoot;

    [Tooltip("頭（CenterEyeAnchor）。未設定なら Camera.main を使用。移動の基準・回転/スケールの Pivot。")]
    [SerializeField] private Transform headAnchor;

    [Tooltip("PC から届く Sim(3)（自動位置合わせの結果）の受信元。未設定なら自動取得。")]
    [SerializeField] private UDPManager udpManager;

    [Tooltip("3DGS の表示/非表示トグルに使う。未設定なら自動取得。")]
    [SerializeField] private GaussianSplatController splatController;

    [Tooltip("起動時はスプラットを非表示で始める（既定 on）。\n" +
             "位置合わせ前のスプラットは実世界と無関係な場所に浮いていて視界を塞ぐだけで、" +
             "しかも「もう合っている」と誤解させる。必要な局面（CP 測量・姿勢適用後）で" +
             "自動的に表示され、X ボタンでいつでも切り替えられる。")]
    [SerializeField] private bool startHidden = true;

    [Header("Speeds")]
    [Tooltip("水平・垂直移動速度 [m/s]")]
    [SerializeField] private float moveSpeed = 0.6f;
    [Tooltip("ヨー回転速度 [deg/s]")]
    [SerializeField] private float yawSpeed = 45f;
    [Tooltip("スケール変化率 [/s]（トリガー全押しで 1 秒あたりこの割合だけ拡大/縮小）")]
    [SerializeField] private float scaleRate = 0.5f;
    [Tooltip("スティックのデッドゾーン")]
    [SerializeField] private float stickDeadzone = 0.15f;

    [Header("Auto (M2)")]
    [Tooltip("実床の高さ[m]。Quest は床基準トラッキングなので通常 0（=world y=0 が実床）。")]
    [SerializeField] private float floorY = 0f;

    [Header("Auto-align gates (P1-b)")]
    // 品質のしきい値（残差・インライア率・スケール妥当域）は **PC 側 cv.json が単一の真実**。
    // ここに同じものを複製しない。
    //
    // 以前は Quest 側にも maxRmseM=0.05 / minInlierRatio=0.5 / |scale|∈[0.5,2.0] を持っていたが、
    // いずれも PC 側（0.08 / 0.40 / 健全性のみ）より厳しく、PC が承認した解を Quest が黙って
    // 握り潰していた。しかも Quest 側はシーンにシリアライズされるため、再ビルドしないと直せない。
    // 実際 bld2 で 2 系統とも踏んだ:
    //   - |scale| 4.52（このシーンの COLMAP 単位）が [0.5,2.0] 外で全棄却
    //   - rmse 0.0506 / 0.0519 が 0.05 をわずかに超えて棄却（PC 側 0.08 は通していた）
    // Quest が担うのは「PC が出せない判断」だけにする:
    //   (1) 下の健全性チェック（NaN/Inf/0 スケールなど、壊れた値を触らせない）
    //   (2) 改悪防止（PC は無状態なので「前回より悪くなった」を知らない）
    //   (3) 連続一致による確定（正しさの判定本体）
    [Tooltip("改悪防止: 適用済みの解がある場合、rmse がこの倍率×前回採用rmse を超えたら棄却。\n" +
             "PC 側は毎回無状態で解き直すため、「良い状態を保持する」判断は Quest 側にしか出せない。")]
    [SerializeField] private float rmseWorsenFactor = 1.5f;

    [Header("Auto-align confirmation（連続一致で確定）")]
    [Tooltip("独立に解かれた解が連続してこの回数一致したら「確定」とする。1 で従来どおり 1 件目で確定。\n" +
             "残差が小さいことは解が正しいことを意味しない（悪条件でも残差は小さくなる）。" +
             "キーフレームが増えても同じ答えに落ち着くことのほうが、ずっと強い証拠になる。")]
    [SerializeField] private int minConsecutiveAgreements = 2;
    [Tooltip("2 つの解が「一致」とみなせる最大のシーン移動量[m]。\n" +
             "スプラットの境界ボックス 8 隅が両解でどれだけ動くかで測る" +
             "（位置・回転・スケールを別々に閾値にするより、見た目の差に直結する）。\n\n" +
             "境界ボックスの隅は原点から 6m 以上離れるため、ここは小さすぎると成立しない。" +
             "0.08 では実機の雑音下でほとんど一致せず、60 秒以内にロックしなくなった。" +
             "運用模擬では 0.15 で、十分に歩いた場合のロック率 94〜100%・誤差中央 10〜16cm。")]
    [SerializeField] private float agreeMaxSceneShiftM = 0.15f;

    // 連続一致の状態
    private bool       _hasPrevSolution;
    private float      _prevSigma;
    private Quaternion _prevRot = Quaternion.identity;
    private Vector3    _prevPos;
    private int        _agreementCount;
    private bool       _autoConfirmed;

    /// <summary>現在の連続一致回数（HUD 表示用）。</summary>
    public int AgreementCount => _agreementCount;
    /// <summary>確定に必要な連続一致回数。</summary>
    public int RequiredAgreements => Mathf.Max(1, minConsecutiveAgreements);
    /// <summary>自動位置合わせが確定済みか（＝ OnAutoAligned を発火済み）。</summary>
    public bool IsAutoConfirmed => _autoConfirmed;
    /// <summary>直近 2 解のシーン移動量[m]（未計算なら負）。</summary>
    public float LastSceneShiftM { get; private set; } = -1f;

    [Header("Auto-align smoothing (P2-c)")]
    [Tooltip("現在との差がこの位置[m]・角度[deg]未満なら、即時スナップせず補間して適用（跳ね防止）。")]
    [SerializeField] private float smoothMaxPosM = 0.3f;
    [SerializeField] private float smoothMaxDeg = 10f;
    [Tooltip("補間の所要時間[s]。0 で常に即時適用。")]
    [SerializeField] private float smoothDurationS = 0.5f;
    private Coroutine _smoothCo;

    // 直近の自動位置合わせ採否（HUD/P3 と改悪防止のための状態）
    private bool  _hasAcceptedSim3;
    private float _lastAcceptedRmse;
    public bool   LastAutoAccepted { get; private set; }
    public string LastAutoReason   { get; private set; } = "(未受信)";
    /// <summary>この試行で採用した Sim(3) の数（HUD 表示用。ResetForNewTrial で 0 に戻る）。</summary>
    public int    AcceptedCount { get; private set; }
    /// <summary>この試行で棄却した Sim(3) の数（同上）。</summary>
    public int    RejectedCount { get; private set; }
    /// <summary>直近に採用した解の world 残差 RMSE[m]（未採用なら負）。</summary>
    public float  LastAcceptedRmse => _hasAcceptedSim3 ? _lastAcceptedRmse : -1f;

    /// <summary>手動の姿勢操作をどこまで受け付けるか。</summary>
    public enum PoseControlMode
    {
        /// <summary>全部使える（通常の手動位置合わせ）。</summary>
        Full,
        /// <summary>スティックだけ（移動・ヨー・呼び寄せ・水平化）。A/B/Y とトリガーは渡さない。
        /// CP 測量中に使う: 測量は右/左トリガーを記録・やり直しに、A/B/Y を実験操作に使っているが、
        /// スプラットは動かせないと困る（位置合わせ前の CP は歩いて行けない場所に出るため）。</summary>
        SticksOnly,
        /// <summary>一切受け付けない（表示トグル X だけ残る）。</summary>
        Locked,
    }

    /// <summary>手動の姿勢操作の受付範囲。表示トグル（X）はどのモードでも残る。
    ///
    /// 評価実験中は、うっかり B（初期姿勢リセット）などを押すとその試行の測定が無効になる。
    /// また ExperimentController が同じボタンを試行操作に使うため、二重発火も防ぐ必要がある。
    /// ExperimentController が局面に応じて毎フレーム設定する（<c>ApplyInputMode</c>）。</summary>
    public PoseControlMode PoseControls { get; set; } = PoseControlMode.Full;

    [Header("State (debug)")]
    [SerializeField] private AlignState state = AlignState.Adjusting;
    public AlignState State => state;

    // 初期姿勢（リセット用）
    private Vector3    _initialPos;
    private Quaternion _initialRot;
    private Vector3    _initialScale;

    /// <summary>確定時に呼ばれる（後続: アンカー保存・追従開始のフック）。手動 Lock でも発火する。</summary>
    public System.Action<Vector3, Quaternion, Vector3> OnLocked;

    /// <summary>PC の Sim(3) がゲートを通って**自動で**適用されたときだけ発火する。
    /// OnLocked は手動 Lock やアンカー復元でも鳴るため、評価 E2（コールドスタート収束）の
    /// 「自動位置合わせが成立した瞬間」を取るにはこちらを使う。</summary>
    public System.Action<Sim3Result> OnAutoAligned;

    /// <summary>これまでに 1 度でも自動 Sim(3) を採用したか（HUD / 実験制御用）。</summary>
    public bool HasAcceptedSim3 => _hasAcceptedSim3;

    // ---- 空間アンカー追従（永続化・デモ。→ Specification.md §15.5） ----
    // 姿勢が「保存済みアンカーから再構成したもの」かどうか。true の間だけ AnchorPersistence の
    // 追従が姿勢を書き換えられる。手動操作・Sim(3)・リセットなど**他の経路で姿勢を変えたら false**
    // に落とし、次の保存が終わるまで追従させない。落とさないと、古いアンカーから再構成した姿勢が
    // 新しく合わせた姿勢を毎フレーム上書きしてしまう。
    private bool _anchored;

    /// <summary>姿勢がアンカー追従の管理下にあるか（Locked かつアンカー由来）。</summary>
    public bool IsAnchored => _anchored && state == AlignState.Locked;

    /// <summary>保存済みアンカーから姿勢を復元したときだけ発火する。
    /// <see cref="OnLocked"/> は鳴らさない（鳴らすと保存が走り、いま復元したアンカーを作り直してしまう）。
    /// <see cref="OnAutoAligned"/> も鳴らさない（実験の「自動位置合わせが収束した瞬間」の定義を守る）。</summary>
    public System.Action OnRestored;

    /// <summary>PC から届いた Sim(3) を受け付けるか（既定 true）。
    /// デモは確定した時点で false にする（来場者の目の前でシーンが跳ねるのを避ける。Specification.md §15.7）。
    /// 実験はこれに触らない（制限時間まで集め続け、届いた解はすべて評価する）。</summary>
    public bool AcceptAutoResults { get; set; } = true;

    /// <summary>B（初期姿勢へ戻す）を受け付けるか（既定 true）。
    /// デモは false にする: 初期姿勢は Editor 上の置き場所で展示では意味が無く、スタッフパネルの
    /// Y＋B 長押しを手動調整中に試すと、押した瞬間に調整中の姿勢が飛んでしまうため。</summary>
    public bool ResetButtonEnabled { get; set; } = true;

    private void Awake()
    {
        if (splatRoot == null)
        {
            var ctrl = FindFirstObjectByType<GaussianSplatController>();
            if (ctrl != null) splatRoot = ctrl.SplatRoot;
        }
        if (headAnchor == null && Camera.main != null) headAnchor = Camera.main.transform;
        if (splatController == null) splatController = FindFirstObjectByType<GaussianSplatController>();

        if (splatRoot == null) Debug.LogError("[AlignmentManager] splatRoot が未設定です。");
        if (headAnchor == null) Debug.LogWarning("[AlignmentManager] headAnchor 未設定（Camera.main も無し）。");
    }

    private void Start()
    {
        if (splatRoot != null)
        {
            _initialPos   = splatRoot.position;
            _initialRot   = splatRoot.rotation;
            _initialScale = splatRoot.localScale;
        }
        if (udpManager == null) udpManager = FindFirstObjectByType<UDPManager>();
        if (udpManager != null) udpManager.OnSim3Result += OnSim3Received;   // 自動位置合わせ結果(PC)を受けてゲート→適用

        // 初期表示。GaussianSplatController が Awake でグローバル不透明度を 1 に初期化するので、
        // Start でここから上書きすれば実行順に依らず確実に効く（Start どうしの順序は不定）。
        if (startHidden) SetSplatVisible(false);

        Debug.Log("[AlignmentManager] ready. state=" + state
                  + " visible=" + _splatVisible);
    }

    private void OnDestroy()
    {
        if (udpManager != null) udpManager.OnSim3Result -= OnSim3Received;
    }

    /// <summary>PC の Sim(3) 結果を受け、品質ゲート（P1-b）を通過したものだけ適用する。
    /// PC 側は無状態で毎回解き直すため、「良い状態を保持する」責務は Quest 側が持つ。</summary>
    public void OnSim3Received(Sim3Result r)
    {
        if (splatRoot == null) { Debug.LogWarning("[AlignmentManager] OnSim3Received: splatRoot 未設定"); return; }
        if (!AcceptAutoResults)
        {
            Debug.Log($"[AlignmentManager] Sim(3) を無視（受付停止中。デモの確定後など） scale={r.sigma:F3} rmse={r.rmse:F4}");
            return;
        }

        float ratio = r.n > 0 ? r.inliers / (float)r.n : 0f;
        float absScale = Mathf.Abs(r.sigma);

        // 健全性チェックのみ（品質のしきい値は PC 側 cv.json が持つ。上の Header コメント参照）。
        // ここで見るのは「Transform に入れたら壊れる値か」だけで、シーン固有の量は一切見ない。
        // 特に |scale| に妥当域を置かないこと — COLMAP の 1 unit が何 m かは再構成ごとに違い、
        // 妥当域を置くと別シーンで正しい解が丸ごと落ちる（bld2 で全 16 セッション棄却）。
        bool finite = !float.IsNaN(r.sigma)  && !float.IsInfinity(r.sigma)
                   && !float.IsNaN(r.rmse)   && !float.IsInfinity(r.rmse);
        if (!finite || absScale <= 0f) { RejectSim3($"異常な解 scale={r.sigma} rmse={r.rmse}"); return; }
        if (r.inliers < 3)             { RejectSim3($"inliers={r.inliers}/{r.n}(<3)"); return; }
        // 改悪防止: 既に良い解を持っているなら、明確に悪化した解では上書きしない
        if (_hasAcceptedSim3 && r.rmse > _lastAcceptedRmse * rmseWorsenFactor)
        { RejectSim3($"改悪 rmse={r.rmse:F4}>{_lastAcceptedRmse:F4}×{rmseWorsenFactor}"); return; }

        _hasAcceptedSim3  = true;
        _lastAcceptedRmse = r.rmse;
        LastAutoAccepted  = true;
        AcceptedCount++;

        // 連続一致による確定判定。
        // 残差が小さいことは解が正しいことを意味しない（共面・共線などの悪条件でも残差は小さい）。
        // 独立に解かれた解が同じ答えに落ち着くことのほうが、はるかに強い証拠になる。
        UpdateAgreement(r);
        bool confirmed = _agreementCount >= RequiredAgreements;

        LastAutoReason = confirmed
            ? $"確定 inliers={r.inliers}/{r.n}({ratio:F2}) rmse={r.rmse:F4} scale={r.sigma:F3}"
            : $"収束中 一致{_agreementCount}/{RequiredAgreements} " +
              (LastSceneShiftM >= 0f ? $"ずれ{LastSceneShiftM * 100f:F0}cm " : "") +
              $"scale={r.sigma:F3}";
        Debug.Log("[AlignmentManager] Sim(3) " + LastAutoReason);

        ApplySim3(r.sigma, r.rot, r.pos, confirmed);

        if (confirmed && !_autoConfirmed)
        {
            _autoConfirmed = true;
            OnAutoAligned?.Invoke(r);   // E2: 自動位置合わせが確定した瞬間（手動 Lock と区別する）
        }
    }

    /// <summary>今回の解が前回の解と一致するかを見て、連続一致回数を更新する。</summary>
    private void UpdateAgreement(Sim3Result r)
    {
        if (_hasPrevSolution)
        {
            LastSceneShiftM = SceneShiftM(_prevSigma, _prevRot, _prevPos, r.sigma, r.rot, r.pos);
            _agreementCount = LastSceneShiftM <= agreeMaxSceneShiftM ? _agreementCount + 1 : 1;
        }
        else
        {
            LastSceneShiftM = -1f;
            _agreementCount = 1;      // 1 件目は「一致 1 回分」として数える
        }
        _hasPrevSolution = true;
        _prevSigma = r.sigma; _prevRot = r.rot; _prevPos = r.pos;
    }

    /// <summary>2 つの Sim(3) を適用したときに、シーンが最大どれだけ動くか[m]。
    ///
    /// 位置・回転・スケールに別々の閾値を置くより、こちらのほうが「見た目がどれだけ違うか」に
    /// 直結する（遠方では小さな回転差が大きなずれになるため、角度の閾値は距離依存になってしまう）。
    /// スプラットの境界ボックス 8 隅で評価する。</summary>
    private float SceneShiftM(float s1, Quaternion r1, Vector3 p1,
                              float s2, Quaternion r2, Vector3 p2)
    {
        Vector3 mn = new Vector3(-5f, -2f, -5f), mx = new Vector3(5f, 2f, 5f);   // 取得失敗時の代替
        var rend  = splatController != null ? splatController.Renderer : null;
        var asset = rend != null ? rend.asset : null;
        if (asset != null) { mn = asset.boundsMin; mx = asset.boundsMax; }

        float worst = 0f;
        for (int i = 0; i < 8; i++)
        {
            Vector3 c = new Vector3((i & 1) == 0 ? mn.x : mx.x,
                                    (i & 2) == 0 ? mn.y : mx.y,
                                    (i & 4) == 0 ? mn.z : mx.z);
            Vector3 a = p1 + r1 * (s1 * c);
            Vector3 b = p2 + r2 * (s2 * c);
            float d = Vector3.Distance(a, b);
            if (d > worst) worst = d;
        }
        return worst;
    }

    /// <summary>実験の新しい試行のために状態を初期化する（評価 E1 の独立試行 / E2 のコールドスタート）。
    ///
    /// 「良い解を保持する」ための状態（_hasAcceptedSim3・_lastAcceptedRmse）を消さないと、
    /// 前の試行の解より悪い解が改悪防止ゲートで弾かれ、試行が独立にならない。
    /// splatRoot も初期姿勢へ戻し、状態を Adjusting にしてコールドスタートを再現する。
    /// PC 側の蓄積リセットは ExperimentController が UDP RESET で行う（本メソッドは Quest 側のみ）。</summary>
    public void ResetForNewTrial()
    {
        if (_smoothCo != null) { StopCoroutine(_smoothCo); _smoothCo = null; }
        _hasAcceptedSim3  = false;
        _lastAcceptedRmse = 0f;
        LastAutoAccepted  = false;
        LastAutoReason    = "(未受信)";
        AcceptedCount     = 0;
        RejectedCount     = 0;
        _hasPrevSolution  = false;      // 前試行の解と一致してしまわないように捨てる
        _agreementCount   = 0;
        _autoConfirmed    = false;
        LastSceneShiftM   = -1f;
        _anchored         = false;
        state = AlignState.Adjusting;
        if (splatRoot != null)
        {
            splatRoot.position   = _initialPos;
            splatRoot.rotation   = _initialRot;
            splatRoot.localScale = _initialScale;
        }
        Debug.Log("[AlignmentManager] ResetForNewTrial: 自動位置合わせ状態と姿勢を初期化 → Adjusting");
    }

    private void RejectSim3(string why)
    {
        LastAutoAccepted = false;
        LastAutoReason   = "棄却 " + why;
        RejectedCount++;
        Debug.Log("[AlignmentManager] Sim(3) 棄却: " + why);
    }

    /// <summary>PC が解いた Sim(3)（M3 自動位置合わせ）を splatRoot に適用する。
    /// scale は符号付き（反射＝負の一様スケール）。適用後は Locked にして永続化フックを発火。
    /// 既に Locked で差が小さい更新は 0.5s 補間して跳ねを防ぐ（P2-c）。大きい差・初回は即時。</summary>
    /// <param name="confirmed">この解を「確定」として扱うか。false のときは姿勢だけ更新し、
    /// 状態は Locked にせず永続化フックも撃たない（収束の途中経過を見せるため適用はする）。</param>
    public void ApplySim3(float sigma, Quaternion rot, Vector3 pos, bool confirmed = true)
    {
        if (splatRoot == null) { Debug.LogWarning("[AlignmentManager] ApplySim3: splatRoot 未設定"); return; }
        if (_smoothCo != null) { StopCoroutine(_smoothCo); _smoothCo = null; }
        _anchored = false;   // 新しい解が入った。保存し直すまでアンカー追従に上書きさせない

        float dPos = Vector3.Distance(splatRoot.position, pos);
        float dDeg = Quaternion.Angle(splatRoot.rotation, rot);
        bool smooth = state == AlignState.Locked && smoothDurationS > 0f
                      && dPos < smoothMaxPosM && dDeg < smoothMaxDeg;
        if (smooth) { _smoothCo = StartCoroutine(SmoothApply(sigma, rot, pos, confirmed)); return; }

        SetSplatPose(sigma, rot, pos);
        FinishApply(sigma, rot, pos, false, confirmed);
    }

    private void SetSplatPose(float sigma, Quaternion rot, Vector3 pos)
    {
        splatRoot.position   = pos;
        splatRoot.rotation   = rot;
        splatRoot.localScale = new Vector3(sigma, sigma, sigma);   // 負値=鏡像（aras-p verbatim の補正）
    }

    // ================= アンカー追従（AnchorPersistence から呼ばれる経路） =================

    /// <summary>保存済みアンカーから再構成した姿勢を採用する（起動時の復元・取り消し後の再採用）。
    /// 状態を Locked にし、以後は <see cref="SetAnchoredPose"/> による追従を受け付ける。
    /// <see cref="OnLocked"/> は鳴らさない（→ <see cref="OnRestored"/> の注記）。</summary>
    /// <param name="restored">起動時の復元なら true（<see cref="OnRestored"/> を発火する）。</param>
    public void AdoptAnchoredPose(Vector3 pos, Quaternion rot, float scale, bool restored)
    {
        if (splatRoot == null) return;
        if (_smoothCo != null) { StopCoroutine(_smoothCo); _smoothCo = null; }
        SetSplatPose(scale, rot, pos);
        state = AlignState.Locked;
        _anchored = true;
        Debug.Log($"[AlignmentManager] アンカー由来の姿勢を採用（{(restored ? "復元" : "再採用")}）: " +
                  $"pos={pos} rotY={rot.eulerAngles.y:F1} scale={scale:F4} → Locked");
        if (restored) OnRestored?.Invoke();
    }

    /// <summary>アンカー追従による毎フレームの更新。<see cref="IsAnchored"/> の間だけ効く
    /// （他の経路で姿勢が変わった後に古いアンカーで上書きしないため）。</summary>
    public void SetAnchoredPose(Vector3 pos, Quaternion rot, float scale)
    {
        if (splatRoot == null || !IsAnchored || _smoothCo != null) return;
        SetSplatPose(scale, rot, pos);
    }

    /// <summary>いまの姿勢を保存し終えたので、アンカー追従の管理下に戻す（AnchorPersistence が保存完了時に呼ぶ）。</summary>
    public void MarkAnchored()
    {
        if (state == AlignState.Locked) _anchored = true;
    }

    private System.Collections.IEnumerator SmoothApply(float sigma, Quaternion rot, Vector3 pos,
                                                       bool confirmed = true)
    {
        Vector3 p0 = splatRoot.position; Quaternion r0 = splatRoot.rotation; float s0 = splatRoot.localScale.x;
        float t = 0f;
        while (t < smoothDurationS)
        {
            t += Time.deltaTime;
            float u = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / smoothDurationS));
            splatRoot.position   = Vector3.Lerp(p0, pos, u);
            splatRoot.rotation   = Quaternion.Slerp(r0, rot, u);
            float s = Mathf.Lerp(s0, sigma, u);
            splatRoot.localScale = new Vector3(s, s, s);
            yield return null;
        }
        SetSplatPose(sigma, rot, pos);
        _smoothCo = null;
        FinishApply(sigma, rot, pos, true, confirmed);
    }

    private void FinishApply(float sigma, Quaternion rot, Vector3 pos, bool smoothed, bool confirmed)
    {
        string tag = smoothed ? "(補間)" : "";
        if (!confirmed)
        {
            // 途中経過。姿勢は見せるが、確定扱いにはしない（永続化もしない）。
            Debug.Log($"[AlignmentManager] ApplySim3{tag}(未確定): scale={sigma:F4} " +
                      $"一致{_agreementCount}/{RequiredAgreements}");
            return;
        }
        state = AlignState.Locked;
        Debug.Log($"[AlignmentManager] ApplySim3{tag}: pos={pos} rotY={rot.eulerAngles.y:F1} scale={sigma:F4} → Locked");
        OnLocked?.Invoke(splatRoot.position, splatRoot.rotation, splatRoot.localScale); // アンカー永続化(M1.5)
    }

    private void Update()
    {
        if (splatRoot == null) return;

        // 表示トグルだけは実験中も使う（3DGS とパススルーを見比べるため）
        if (OVRInput.GetDown(OVRInput.Button.Three)) ToggleSplatVisible();    // X: 3DGS表示/非表示

        // 姿勢を変える操作は実験中は封じる。誤爆でその試行の測定が無効になるのを防ぎ、
        // 同じボタンを ExperimentController が試行操作に使えるようにする。
        PoseControlMode mode = PoseControls;
        if (mode == PoseControlMode.Locked) return;
        bool full = mode == PoseControlMode.Full;

        // 状態遷移（どの状態でも受け付ける）。A/B/Y は SticksOnly では渡さない
        // （測量中・実験中は ExperimentController が同じボタンを使っている）。
        if (full)
        {
            if (OVRInput.GetDown(OVRInput.Button.One))   Lock();              // A: 確定
            if (OVRInput.GetDown(OVRInput.Button.Two) && ResetButtonEnabled) ResetPose();   // B: 初期姿勢にリセット
            if (OVRInput.GetDown(OVRInput.Button.Four))  Unlock();            // Y: 再調整
        }
        if (OVRInput.GetDown(OVRInput.RawButton.RThumbstick)) RecenterInFront();        // 右スティック押込: 目の前に呼び寄せ
        if (OVRInput.GetDown(OVRInput.RawButton.LThumbstick)) AutoLevelAndFloorSnap(); // 左スティック押込: 水平化＋床スナップ

        // Locked のときに勝手に動かさないのは Full のときだけの約束。SticksOnly（＝ CP 測量中）は
        // 「CP の実物まで歩いて行けるようにシーンを寄せる」ための一時的な移動で、そのとき何を
        // 測っているかとは無関係（測るのは実物の位置だけ）。試行開始時に ResetForNewTrial が
        // 初期姿勢へ戻すので、ここで動かした結果が試行に持ち込まれることもない。
        if (full && state != AlignState.Adjusting) return;

        float dt = Time.deltaTime;
        Vector3 pivot = headAnchor != null ? headAnchor.position : splatRoot.position;

        // --- 移動 ---
        Vector2 r = Deadzone(OVRInput.Get(OVRInput.RawAxis2D.RThumbstick));   // 右: 水平
        Vector2 l = Deadzone(OVRInput.Get(OVRInput.RawAxis2D.LThumbstick)); // 左: 上下＋ヨー

        // 頭のヨーだけ取り出した水平基底（前後・左右）
        Vector3 fwd = Vector3.ProjectOnPlane(headAnchor != null ? headAnchor.forward : Vector3.forward, Vector3.up).normalized;
        Vector3 right = Vector3.ProjectOnPlane(headAnchor != null ? headAnchor.right : Vector3.right, Vector3.up).normalized;
        Vector3 move = (fwd * r.y + right * r.x) * moveSpeed * dt;
        move += Vector3.up * (l.y * moveSpeed * dt);
        if (move.sqrMagnitude > 0f) { splatRoot.position += move; _anchored = false; }

        // --- ヨー回転（頭位置を軸に） ---
        if (Mathf.Abs(l.x) > 0f)
        {
            splatRoot.RotateAround(pivot, Vector3.up, l.x * yawSpeed * dt);
            _anchored = false;
        }

        // --- スケール（頭位置 Pivot で等方） ---
        // SticksOnly では触らない。CP 測量中は両トリガーが「記録」「1 つ戻る」に割り当たっており、
        // 記録するたびにスプラットが伸び縮みしてしまう。
        if (!full) return;
        float up   = OVRInput.Get(OVRInput.RawAxis1D.RIndexTrigger);   // 右トリガー: 拡大
        float down = OVRInput.Get(OVRInput.RawAxis1D.LIndexTrigger); // 左トリガー: 縮小
        float scaleDelta = (up - down) * scaleRate * dt;                  // 例: +0.5*dt
        if (Mathf.Abs(scaleDelta) > 1e-6f)
        {
            float factor = 1f + scaleDelta;
            ScaleAround(pivot, factor);
        }
    }

    // 頭位置などを Pivot に等方スケール（Pivot のワールド位置を固定）
    private void ScaleAround(Vector3 pivot, float factor)
    {
        _anchored = false;
        splatRoot.localScale *= factor;
        splatRoot.position = pivot + (splatRoot.position - pivot) * factor;
    }

    public void Lock()
    {
        if (state == AlignState.Locked) return;
        state = AlignState.Locked;
        Debug.Log($"[AlignmentManager] LOCK  pos={splatRoot.position} rotY={splatRoot.eulerAngles.y:F1} scale={splatRoot.localScale.x:F3}");
        OnLocked?.Invoke(splatRoot.position, splatRoot.rotation, splatRoot.localScale);
    }

    public void Unlock()
    {
        if (state == AlignState.Adjusting) return;
        state = AlignState.Adjusting;
        _anchored = false;
        Debug.Log("[AlignmentManager] UNLOCK (re-adjust)");
    }

    public void ResetPose()
    {
        if (state == AlignState.Locked) Unlock();   // 修正6: 姿勢を動かす操作は必ず Adjusting で行う
        splatRoot.position   = _initialPos;
        splatRoot.rotation   = _initialRot;
        splatRoot.localScale = _initialScale;
        Debug.Log("[AlignmentManager] RESET to initial pose");
    }

    // M2: 自動「水平化＋床スナップ」。
    // 傾き（ロール/ピッチ）をヨーのみに補正し、スプラットの最下面を実床(floorY)へ合わせる。
    // 手動で詰めにくい「重力方向」と「床の高さ」の2自由度を自動化する。
    public void AutoLevelAndFloorSnap()
    {
        if (splatRoot == null) return;
        _anchored = false;   // Locked のままでも呼べる（スティック押込）。姿勢が変わるのでアンカー追従から外す

        // 1) 水平化: ヨーだけ残してロール/ピッチを除去
        float yaw = splatRoot.eulerAngles.y;
        splatRoot.rotation = Quaternion.Euler(0f, yaw, 0f);

        // 2) 床スナップ: アセット bounds の8隅をワールド変換し最下 Y を求めて floorY に合わせる
        var rend = splatController != null ? splatController.Renderer : null;
        var asset = rend != null ? rend.asset : null;
        if (asset == null) { Debug.LogWarning("[AlignmentManager] AutoLevel: GaussianSplatAsset 取得不可。水平化のみ実施。"); return; }

        Vector3 mn = asset.boundsMin, mx = asset.boundsMax;
        Matrix4x4 l2w = splatRoot.localToWorldMatrix;
        float minY = float.MaxValue;
        for (int i = 0; i < 8; i++)
        {
            Vector3 c = new Vector3((i & 1) == 0 ? mn.x : mx.x,
                                    (i & 2) == 0 ? mn.y : mx.y,
                                    (i & 4) == 0 ? mn.z : mx.z);
            float wy = l2w.MultiplyPoint3x4(c).y;
            if (wy < minY) minY = wy;
        }
        float delta = floorY - minY;
        splatRoot.position += new Vector3(0f, delta, 0f);
        Debug.Log($"[AlignmentManager] AutoLevelAndFloorSnap: yaw={yaw:F1} bottomY={minY:F3} -> floorY={floorY:F3} (Δ={delta:F3})");
    }

    // 目の前（頭の前方 2m・頭の高さ）にスプラットを呼び寄せる。位置合わせの初期化に便利。
    // 回転・スケールは保持（必要なら微調整）。
    public void RecenterInFront(float distance = 2f)
    {
        if (splatRoot == null || headAnchor == null) return;
        if (state == AlignState.Locked) Unlock();   // 修正6: 姿勢を動かす操作は必ず Adjusting で行う
        _anchored = false;
        Vector3 fwd = Vector3.ProjectOnPlane(headAnchor.forward, Vector3.up).normalized;
        if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
        splatRoot.position = headAnchor.position + fwd * distance;
        Debug.Log("[AlignmentManager] RecenterInFront pos=" + splatRoot.position);
    }

    // 3DGS シーンの表示/非表示（= パススルーのみ ⇄ 3DGS 重畳 の切替）
    private bool _splatVisible = true;

    /// <summary>3DGS がいま表示されているか。</summary>
    public bool SplatVisible => _splatVisible;

    public void ToggleSplatVisible() => SetSplatVisible(!_splatVisible);

    /// <summary>3DGS の表示を明示的に切り替える（実験の局面に応じた自動制御に使う）。
    /// 既に同じ状態なら何もしないので、毎フレーム呼んでも安全。</summary>
    public void SetSplatVisible(bool visible)
    {
        if (_splatVisible == visible) return;
        if (_fadeCo != null) { StopCoroutine(_fadeCo); _fadeCo = null; }   // フェード中の切替は即時で確定させる
        _splatVisible = visible;
        if (splatController != null) { if (_splatVisible) splatController.Show(); else splatController.Hide(); }
        else if (splatRoot != null) splatRoot.gameObject.SetActive(_splatVisible); // フォールバック
        Debug.Log("[AlignmentManager] 3DGS visible = " + _splatVisible);
    }

    private Coroutine _fadeCo;

    /// <summary>3DGS を不透明度 0 から徐々に表示する（デモで姿勢が確かめられた瞬間に使う）。
    /// パッと出すより「現れた」ことが伝わり、来場者が見落としにくい。途中で X が押されたら打ち切る。</summary>
    public void FadeInSplats(float seconds)
    {
        if (_splatVisible) return;   // 既に出ている／フェード中（出し直すと一瞬消えてちらつく）
        if (splatController == null || seconds <= 0f) { SetSplatVisible(true); return; }
        _splatVisible = true;
        splatController.SetOpacity(0f);
        _fadeCo = StartCoroutine(FadeRoutine(seconds));
        Debug.Log($"[AlignmentManager] 3DGS をフェードイン（{seconds:F1}s）");
    }

    private System.Collections.IEnumerator FadeRoutine(float seconds)
    {
        float t = 0f;
        while (t < seconds)
        {
            if (!_splatVisible) { _fadeCo = null; yield break; }
            splatController.SetOpacity(Mathf.SmoothStep(0f, 1f, t / seconds));
            t += Time.unscaledDeltaTime;
            yield return null;
        }
        if (_splatVisible) splatController.Show();
        _fadeCo = null;
    }

    private Vector2 Deadzone(Vector2 v)
    {
        if (Mathf.Abs(v.x) < stickDeadzone) v.x = 0f;
        if (Mathf.Abs(v.y) < stickDeadzone) v.y = 0f;
        return v;
    }
}
