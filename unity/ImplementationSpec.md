# ImplementationSpec.md — VPTAligned3DGS 実装仕様

本書は **実装者（人間または AI）がこのまま実装に着手できる粒度** の仕様。設計の意図・トレードオフは `Specification.md`（設計書）、Unity/XR の運用規約・前提は `CLAUDE.md` を参照する。本書はそれらの上に立つ「確定した実装契約」であり、曖昧語（「約」「など」「適宜」）を避け、**型・単位・既定値・終了条件・受け入れ基準**を明記する。

> ステータス: **v3（自動位置合わせ実装完了・as-built 確定実装）**。`Specification.md` v6 に追従。v1 のゼロ構築・オンデバイス完結・`.ply` 仮想レンダ DB・Sentis は不採用。**実際に動いている実装**を直下「v3 as-built」と各節 v2/v3 注記に確定する（ここが正。v1 の §3.1 詳細モジュール構成・§12 TCP/NACK・§13 一部既定値は旧設計で、下記が優先）。本書 §6〜§11 の広い設計は地図として残す。
>
> **v3 as-built（すべて実機動作確認済み）**:
> - **基盤**: scalabroom。Unity 6000.3.10f1 / URP（Render Graph Compatibility Off）/ OpenXR **Multi-Pass**（aras-p v1.1.1 は SPI 非対応。§14 の「既定 SPI」は誤りで Multi-Pass が正）/ Vulkan / aras-p v1.1.1 素 / `GaussianSplatURPFeature` を PC・Mobile 両 Renderer / フロアレベル原点。約15万スプラット `.ply`。
> - **自動位置合わせ = hloc COLMAP 再ローカライズ**（オンデバイス CV/Sentis は不採用）。
>   - オフライン（シーン毎1回・WSL2/GPU, `vptserver/cv/hloc_db.py`）: `sparse/0`＋`images/` から **SuperPoint** 抽出 → 既存 COLMAP 姿勢の共visibility → **LightGlue** マッチ → **既存姿勢を再利用して再三角測量**（`.ply` と同座標系の SfM）→ **NetVLAD**。出力 `scene_data/<name>/hloc/`。
>   - レガシー COLMAP モデル（rigs/frames 無し）は新形式へ**移行**してから使う（`migrate_model.py`。pycolmap で読み→書き出し。未移行だと `.rigs` でクラッシュ）。
>   - オンライン（`vptserver/cv/localize.py`）: 受信 JPEG → SuperPoint → NetVLAD 検索 → LightGlue → `localize_sfm`(PnP) → `T_{c←cam}`、カメラ中心 `C_c`。複数キーフレームの `(C_c, Quest world 中心 C_w)` を `solve_sim3_ransac` で **Sim(3)** に（反射→負スケール）。自己テストで誤差/シーン対角 ~1e-4 を確認、実キーフレームで残差 ~1cm。
> - **座標規約（最重要）**: aras-p は PLY を **verbatim** で読む → モデル座標＝COLMAP 座標。Unity 左手系との整合に**反射**が要るので **splatRoot の一様スケールを負値**にする（手動も自動も負スケール）。Sim(3) はカメラ中心の Umeyama（反射許容）で当て、`det(Q)<0` なら `scale=-s, R=-Q`（§2 v3 注記・`cv/geometry.py`）。
> - **内部行列 K**: 正方ピクセル＋中央縦クロップ前提。`vptserver/keyframe.py: k_for_image_from_meta`（非クロップ軸から一様 scale、fx=fy 同一、cx/cy は中央クロップ補正）。
> - **計算配置（確定）**: CV は **WSL2(Ubuntu)+CUDA**（pycolmap は Windows ネイティブで `.rigs` アクセス時にクラッシュするため Linux 必須）。UDP 受信・Sim(3) 返送・GUI は **Windows `run.py`**。両者は **共有フォルダ `/mnt/c` 経由で疎結合**。CV 依存: torch(cu124)・**pycolmap==3.13.0**・hloc（GitHub から `--recursive`）・numpy 2.x（`requirements-cv.txt`）。
> - **通信プロトコル（確定）**: UDP ベストエフォート。13B ヘッダ `['V','P', type(1), reqId(4 LE), idx(2 LE), total(2 LE), len(2 LE)]` ＋ payload。type=1 META(UTF-8 JSON)・type=2 IMG(JPEG チャンク 既定1100B)・**type=3 RESULT(Sim(3) JSON, PC→Quest)**。ポート: **データ 50000 / ビーコン 50001 / 結果 50002**（§13 の 48010/48011 は旧値）。ビーコン= `b"VPTSRV1"`＋データポート(u16) を LAN ブロードキャスト。AP がブロードキャストを遮断する環境では `manualIPAddress` 固定（ユニキャストは通る）。受信に `CHANGE_WIFI_MULTICAST_STATE` 権限。
> - **運用自動化**: run.py は起動毎に `received/session_<時刻>/` 生成、`cv.json{scene,every,min}` を書く。WSL2 localize は最新 session を自動追従し cv.json を**ライブ反映**、新規成功 N 枚ごとに Sim(3) 更新→`session/result_sim3.json`→run.py が type=3 で返送。**GUI**（customtkinter ネイティブ）で状態可視化＋設定編集（即 cv.json）。
> - **as-built ファイル構成**（§3.1 の旧詳細案ではなくこれが実体）:
>   - PC `server/`: `run.py`(UDP受信/返送/GUIホスト)・`vptserver/`(`protocol`,`reassembler`,`udp_receiver`,`keyframe`,`storage`,`beacon`,`gui`,`cv/`{`colmap_model`,`geometry`,`gates`,`hloc_db`,`migrate_model`,`localize`})・`scene_data/<name>/`・`tests/`{`test_geometry.py`,`test_gates.py`}・`requirements.txt`/`requirements-cv.txt`。
>   - Quest `Assets/Scripts/`: `UDPManager`(送信＋ビーコン自動検出＋結果受信/`Sim3Result`)・`KeyframeUploader`(左グリップ 短押し単発/長押し自動＋移動量トリガ)・`PassthroughCameraSource`(PCAラップ・解像度・K診断)・`AlignmentManager`(M1手動/M2自動水平/`OnSim3Received`ゲート＋補間適用)・`AnchorPersistence`(永続化v2: シーン別JSON・複数アンカー)・`GaussianSplatController`(`ModelId`)・`AlignmentHud`(HUD＋Poke)・`IKTargetManager`(アバター重畳: CameraRig アンカー→VRIK の IK ターゲット)。**アバター重畳（`Avatar`＝VRIK＋`IKTargetManager`, RootMotion FinalIK）は必要機能として維持**。未使用の `ControllerManager` のみ削除。
> - **実装状況（as-built・実機確認済み）**: M1/M1.5/M2/M3 に加え、**P1 品質ゲート二段**（`cv/gates.py`＋`OnSim3Received`・改悪防止）／**P2 自動送信＋スライディングウィンドウ＋更新補間・保存スロットル**／**P3 HUD（Poke 操作）**／**P4 永続化v2（シーン別JSON・複数アンカー冗長・外れ除去平均・modelIdハイブリッド）** を実装。残る任意課題はフル連続追従(Stage D。現状は軽量版で運用)。精度評価・回転可観測性向上は見送り。

---

## 0. 用語・記法・前提

- 座標・姿勢の記法: `T_{a←b}` は「`b` 座標系の点を `a` 座標系へ写す」剛体/相似変換。点の変換は `p_a = T_{a←b} · p_b`。合成は `T_{a←c} = T_{a←b} · T_{b←c}`。
- 変換の中身: 剛体は `(R, t)`（`R∈SO(3)`, `t∈ℝ³`）、相似は `(s, R, t)`（`s>0`）。4×4 同次行列でも保持。
- フレーム一覧（§2 で厳密化）: `m`（モデル=スプラット）, `w`（Unity ワールド）, `hmd`, `camL/camR`（PCA 左右）, `depth`（深度センサ）, `cv`（CV 計算用の右手フレーム）。
- 時刻: `t` は秒。センサ値は各々**キャプチャ時刻のタイムスタンプ**を持つ（§4.3 設計書）。
- 略語は `Specification.md` §13 を参照。

---

## 1. 確定した既定値（Decision Log）

実装はまずこの表どおりに作る。各行の「代替」は将来差し替え候補で、インターフェース（§11）を介して交換可能にする。

| 項目 | 確定既定値 | 代替 | 備考 |
|---|---|---|---|
| 局所特徴（オンデバイス主） | **XFeat + LighterGlue**（ONNX→Sentis） | ORB+BFMatcher | §6,§7 |
| 局所特徴（オンデバイス・フォールバック） | **ORB（OpenCV）** | AKAZE | NN 不要の最低保証 |
| 局所特徴（PC オフロード） | **SuperPoint + LightGlue** | DISK+LightGlue | PC は精度優先 |
| 大域記述子（オンデバイス） | **GeM プーリング小型 CNN**（ONNX→Sentis, 512次元 fp16） | DBoW2(ORB) | コサイン総当たり |
| 大域記述子（PC） | **NetVLAD** | SALAD | |
| PnP | **EPnP 初期化 + 反復精密化（OpenCV solvePnPRansac, flag=EPNP→ITERATIVE）** | SQPnP | §7.3 |
| ロバスト推定 | **RANSAC**（再投影しきい 3.0px@処理解像度, conf 0.999, maxIter 2000） | LO-RANSAC | |
| 相似変換推定 | **Umeyama（スケール有）+ RANSAC ラッパ**（カメラ中心集合に適用） | 3D-3D 直接 | §7.4 |
| 精密 ICP | **point-to-plane, scaled-ICP(7DoF)→6DoF, Huber** | GICP | §8.1 |
| 最終精密化 | **特徴再投影誤差 on Sim(3) を LM 最適化（測光は従）** | 測光主 | §8.2 |
| スケール確定 | **Environment Depth の 3D-3D（既定・最優先）** | 多視点三角測量／MRUK寸法 | 設計書 §7 |
| 通信（既定） | **TCP（LOC_REQ/RESP）＋ UDP ブロードキャスト（ディスカバリのみ）** | 全 UDP+自作再送（設計書 §5.4） | §12 |
| 直列化 | **little-endian 固定長バイナリ＋4Bレングス前置（TCP）** | FlatBuffers | §5,§12 |
| `T` スムージング | **Sim(3) 上の指数移動平均 + 並進/回転に one-euro** | カルマン | §9 |
| 推論ランタイム | **Unity Sentis（GPU Compute backend）** | CPU backend | NNAPI 無し（設計書 §5.3） |
| カメラ取得(PCA) | **Meta XR SDK `PassthroughCameraAccess`（MRUK v81+／Building Block「Passthrough Camera Access」で追加）** | 旧 `WebCamTextureManager`＋`PassthroughCameraUtils`（レガシ・非推奨） | §3,§14。`GetTexture()/Intrinsics/GetCameraPose()/Timestamp/ViewportPointToRay` を提供。権限は `HEADSET_CAMERA` のみ |
| カメラ可視化(デバッグ) | **Building Block「Passthrough Camera Visualizer」（開発時のみ・任意）** | 自前デバッグ表示 | §15。製品パイプラインの依存にしない |
| 永続化 | **空間アンカー相対で `T` を保存（複数アンカー冗長）** | 単一アンカー | §10 |

---

## 2. 座標系と数学的規約（実装の最重要部）

座標バグは本アプリ最大の事故要因。**規約を一箇所（`CoordinateConventions` 静的クラス）に集約**し、ここ以外で軸反転・符号操作を書かない（`CLAUDE.md` §4-4/§10）。

### 2.1 フレーム定義
- **`m`（モデル）**: `GaussianSplatRenderer` ルートの**ローカル座標**。すなわち aras-p インポータ適用後・Unity 左手系で表現された点。**COLMAP 右手系は本ランタイムには現れない**（COLMAP→Unity 変換はインポータと前処理で吸収。§6.1）。`m` の単位はモデル固有スケール（無次元、要 `s`）。
- **`w`（ワールド）**: Unity 左手系・Y-up・メートル。Quest トラッキングの基準。
- **`hmd`**: ヘッドセット本体。`T_{w←hmd}(t)` は XR から取得（メートル）。
- **`camL/camR`**: PCA 物理カメラ。`T_{w←cam}(t)` と内部 `K` をフレーム毎に取得。
- **`depth`**: Environment Depth の出力視点。`T_{w←depth}(t)` を取得（取れない実装では深度を MRUK/再投影で `w` 化）。
- **`cv`**: CV ライブラリ（OpenCV）が想定する**右手・カメラ z 前方・y 下**の作業フレーム。**solvePnP/Umeyama などの数値計算は `cv` で行い、境界で一度だけ Unity と相互変換**する。

### 2.2 ハンドネス変換（Unity ⇄ CV）
Unity 左手 ↔ 右手は **Z 反転** `S = diag(1, 1, -1)`（`S = S⁻¹`, `det S = -1`）で統一する。
- 点: `p_cv = S · p_u`、`p_u = S · p_cv`。
- 回転行列: `R_cv = S · R_u · S`、`R_u = S · R_cv · S`（`det` は不変=+1、正しい回転になる）。
- 並進: `t_cv = S · t_u`。スケール `s` は不変。
- **クォータニオンの直接符号操作は禁止**。必ず行列に変換してから `S·R·S` を適用し、再正規化する（符号取り違え防止）。`CoordinateConventions.ToCv(Pose)/ToUnity(Pose)` に実装し、§15 の往復ユニットテストで保証。

### 2.3 カメラ投影モデル
- 画像座標: 原点 左上、x 右、y 下（OpenCV 流）。`K = [[fx,0,cx],[0,fy,cy],[0,0,1]]`。
- 投影: `cv` カメラ系の点 `X=(X,Y,Z)`（Z>0 前方）→ `u = fx·X/Z + cx`, `v = fy·Y/Z + cy`。
- 歪み: PCA は内部で補正済みを想定（`distCoeffs=0`）。**実機で歪み残差を確認**し、残るなら `K` と並べて係数を DB/設定に保持（§13 `pca.distCoeffs`）。
- Unity カメラ（`u_cam`: z 前方・y 上・左手）から `cv` カメラへは §2.2 の `S` を適用。

### 2.4 推定対象と出力の合成
最終出力は `T_{m→w} = (s, R, t)`（点: `x_w = s·R·x_m + t`）。これを `GaussianSplatRenderer` ルート Transform に設定する（§2.5）。

- **PnP**（1 フレーム, `cv`）: ライブ 2D ↔ モデル 3D（`m` を `cv` 化）から `T_{cv_cam←m}` を解く。カメラ中心（モデル単位）`C^m = -R_{cv_cam←m}ᵀ · t_{cv_cam←m}`。
- **メートル側**: 既知 `T_{w←cam}(t)` からカメラ中心（メートル）`C^w`。
- **相似変換**（≥3 キーフレーム）: 集合 `{C^m_i}`（モデル単位）→ `{C^w_i}`（メートル）に **Umeyama（スケール有）** を適用し `T_{m→w}=(s,R,t)` を得る（§7.4）。重力事前拘束時は `R` をヨーのみに制約（§7.2）。
- **3D-3D 直接（補助）**: PnP インライアの 3D 点（`m`）と、その画素に対応する Environment Depth のメートル 3D 点（`w`）の対応からも同じ `T_{m→w}` を Umeyama で推定可能（設計書 §6 外観ルート末尾）。

### 2.5 Transform への適用（非等方スケール禁止）
`T_{m→w}` の `s` は**等方スケール**。Unity Transform に入れる際:
- `root.localScale = (s, s, s)`、`root.rotation = R`（Unity 系）、`root.position = t`（Unity 系）。
- 親に非単位スケールがあると `lossyScale` が崩れるため、**ルートの親は単位 SRT** を必須とする（前提条件、起動時に検証して警告）。
- スプラットデータ・`GaussianSplatAsset` は**書き換えない**（設計書 §8）。

### 2.6 Sim(3) のパラメータ化（最適化用）
精密化（§8.2）は Sim(3) のリー代数 `sim3 ≅ ℝ⁷`（並進3, 回転3, スケール1）上で**左摂動** `T ← exp(δ) · T` を用いる。`exp/log` は標準 Sim(3) 指数写像を `Sim3.cs` に実装（§15 で往復テスト）。ヤコビアンは自動でなく解析（§8.2）。

---

## 3. システム・アーキテクチャ

### 3.1 モジュール構成（責務分離）
純粋計算（`MonoBehaviour` 非依存・テスト可能）と Unity 結合層を分ける（`CLAUDE.md` §10）。`Assets/Scripts/` 配下:

```
Scripts/
├─ Core/                      # 純粋C#・Unity非依存・単体テスト対象
│  ├─ Math/  CoordinateConventions, Sim3, SE3, Umeyama, OneEuroFilter
│  ├─ Align/ PnPSolver, IcpRefiner, Sim3Refiner, HypothesisVerifier
│  ├─ Features/ IFeatureExtractor, IGlobalDescriptor, IMatcher (interfaces + impls)
│  ├─ Db/    RegistrationDb (load/query), DbSchema
│  ├─ State/ AlignmentStateMachine, AlignmentState (Tの真実)
│  └─ Net/   OffloadProtocol (serialize/deserialize), OffloadClient
├─ Runtime/                   # Unity/XR結合層 (MonoBehaviour)
│  ├─ AppController           # 状態機械のホスト・起動順
│  ├─ PassthroughCameraSource # Meta PassthroughCameraAccessをラップ→フレーム(I,K,T_w_cam,ts)
│  ├─ DepthSource             # Environment Depth→点群/深度
│  ├─ MrukSource              # 床/壁/アンカー
│  ├─ HmdPoseSource           # T_w_hmd(t) 履歴バッファ
│  ├─ Tracker (Stage D)       # 別スレッドワーカのUnity側窓口
│  ├─ Localizer (Stage B/C)   # オンデバイス/オフロードの切替
│  ├─ OverlayPresenter        # AlignmentState.T を root へ適用(描画スレッド)
│  ├─ ManualAligner (Stage E) # ハンドジェスチャ/ギズモ
│  └─ PersistenceManager (Stage F) # アンカー相対保存/復元
├─ Editor/                    # Stage A 前処理ツール
│  └─ RegistrationBaker       # .ply→DB生成（メニュー/CLI）
└─ Tests/  EditMode + PlayMode
```

PC 側参照実装は別リポ/ディレクトリ `pc_offload/`（Python, §12.4）。

### 3.2 データの所有とスレッドモデル
- **`AlignmentState`** が唯一の真実 `T_current`（と信頼度・更新時刻）を保持。**イミュータブルなスナップショットを `volatile` 参照のアトミック差し替え**で公開（ロックフリー読み出し）。
- **描画スレッド**（72/90Hz, `OverlayPresenter`）: 毎フレーム `AlignmentState` から最新スナップショットを読み、`T` を root に適用するだけ（CV なし＝貼り付き保証, 設計書 §5.5）。
- **追従ワーカ**（5–15Hz, `Tracker`）: 別スレッド/ジョブで `ΔT` を計算→`AlignmentState` に publish。
- **ローカライズワーカ**（オンデマンド, `Localizer`）: 重い B/C を別スレッド/オフロード。完了時に publish。
- センサ（PCA/Depth/HMD）は**時刻付きリングバッファ**で各 `*Source` が保持し、ワーカは「撮影時刻に最も近い姿勢」を補間取得（§4.3 設計書の前方補正に必須）。

```
[PCA/Depth/HMD Sources]→(時刻付きリングバッファ)→┬→[Tracker 5-15Hz]→┐
                                                  └→[Localizer on-demand]┤
                                                                         ▼
                                                              [AlignmentState.T]→[OverlayPresenter 72/90Hz]→root Transform
```

### 3.3 起動順序（AppController）
1. アセット読込（GaussianSplatAsset, DB, Sentis モデル）。失敗→致命エラー UI。
2. XR/OpenXR/Passthrough/MRUK 初期化、権限確認（§14）。
3. `*Source` 開始、リングバッファ充填待ち。
4. 永続アンカーがあれば復元を試行（Stage F→TRACKING）。無ければ UNLOCALIZED。
5. 状態機械開始（§4）。

---

## 4. ランタイム状態機械

### 4.1 状態
| 状態 | 意味 | 主な処理 |
|---|---|---|
| `BOOTSTRAP` | 起動・アセット/XR 初期化 | §3.3 |
| `UNLOCALIZED` | 未確定。ユーザに見回しを促す UI | キーフレーム収集準備 |
| `LOOKAROUND` | 見回し中・キーフレーム蓄積 | §6.1 設計書, KF 選択 |
| `GLOBAL_LOC` | 大域ローカライズ実行（Stage B） | §7 |
| `REFINE` | 初期精密化（Stage C, 一度きり） | §8 |
| `TRACKING` | 確定・連続追従（Stage D, LOCKED） | §9, 残差監視 |
| `RELOCALIZING` | 追従ロスト→復帰（B 再実行） | §7（軽量再入） |
| `MANUAL` | 手動フォールバック（Stage E） | §10.E |
| `PERSISTED` | アンカー保存/復元処理 | §10.F |

### 4.2 遷移表
| From | イベント / ガード | To | アクション |
|---|---|---|---|
| BOOTSTRAP | initOK | UNLOCALIZED | 既存アンカーあれば PERSISTED 試行へ |
| UNLOCALIZED | userStart | LOOKAROUND | KF バッファ初期化 |
| LOOKAROUND | `kfCount ≥ kf.min` (=3) | GLOBAL_LOC | B 起動（オンデバイス or オフロード） |
| LOOKAROUND | `elapsed > lookaround.timeout` (=12s) かつ kf不足 | MANUAL | 見回し失敗→手動誘導 |
| GLOBAL_LOC | `verify.pass`（§7.5） | REFINE | B 結果を初期値に |
| GLOBAL_LOC | `verify.fail` かつ retries<2 | LOOKAROUND | KF 追加収集 |
| GLOBAL_LOC | `verify.fail` かつ retries≥2 | MANUAL | 自動失敗→手動 |
| REFINE | refineConverged | TRACKING | `T` 確定・publish・アンカー保存 |
| REFINE | refineDiverged | GLOBAL_LOC | やり直し |
| TRACKING | `residual > track.lostThresh`（§9.4）持続 | RELOCALIZING | 追従値を初期値に B 再入 |
| TRACKING | anchorUpdated | TRACKING | アンカー相対で `T` 補正（§10.F） |
| RELOCALIZING | locSuccess | TRACKING | publish |
| RELOCALIZING | locFail (timeout) | MANUAL | |
| MANUAL | userSnap | REFINE | 手動初期値→C/D 同一バックエンド |
| any | userReset | UNLOCALIZED | `T` 破棄 |

時間・しきい値の実値は §13。

---

## 5. データモデル & ファイル形式

### 5.1 レジストレーション資産（サイドファイル）
`GaussianSplatAsset` の傍らに 2 ファイルで格納（`<name>.vptdb`＋`<name>.vptdb.json`）。Editor が生成（§6）、ランタイムが読み込み（`RegistrationDb`）。

**マニフェスト（JSON, 人間可読・検証用）** `<name>.vptdb.json`:
```json
{
  "schemaVersion": 1,
  "modelId": "room_a",
  "createdUtc": "2026-06-15T00:00:00Z",
  "bakerGitHash": "....",
  "featureMethod": "XFeat",         // DBと実機クエリで一致必須
  "globalDescMethod": "GeM",
  "descDim": 64,                     // 局所記述子次元(int8)
  "globalDim": 512,                  // 大域記述子次元(fp16)
  "keyframeCount": 1234,
  "pointCount": 530000,              // P_m（ICP用）
  "gravityUp_m": [0,1,0],           // m系の重力上方向（単位ベクトル）
  "floorPlane_m": [a,b,c,d],        // m系の床平面 ax+by+cz+d=0
  "modelToMetersHint": 0.0,          // 既知ならスケール初期値(無ければ0)
  "bounds_m": {"min":[..],"max":[..]},
  "sourceMode": "render|colmap"
}
```

**バイナリ** `<name>.vptdb`（little-endian, 連結ブロック。各ブロックは `[uint32 tag][uint64 byteLen][data]`）:
- `HEAD`: マジック `"VPTDB\0"`、`schemaVersion(uint16)`、ブロック索引（オフセット表）。
- `KEYF`: キーフレーム配列。1 レコード:
  - `id(uint32)`, `T_w_cam_atBake(float32×7: tx,ty,tz, qx,qy,qz,qw)`（**m 系**での仮想カメラ姿勢）, `K(float32×4: fx,fy,cx,cy)`, `width,height(uint16)`,
  - `globalDesc(float16×globalDim)`,
  - `nKp(uint16)`, 続いて各キーポイント: `u,v(float16×2)`, `score(float16)`, `desc(int8×descDim)`, `p_m(float32×3: 逆投影3Dモデル点)`, `validDepth(uint8)`。
- `PNTS`: ICP 用ダウンサンプル点群 `P_m`。各点 `pos(float32×3)`, `normal(float16×3)`。
- `VPRX`: 大域記述子の検索索引（既定は線形＝索引省略可。代替で IVF/PQ）。

**スキーマ規約**:
- `featureMethod/globalDescMethod/descDim/globalDim` は**実機クエリ側と完全一致**でなければロード時にエラー（混在不可, 設計書 §5.3）。
- すべての姿勢・点は **`m` 系**（Unity 左手・モデル単位）で格納。CV 変換はランタイムが境界で行う。
- 追記・拡張はブロック追加で後方互換（未知 tag は読み飛ばし）。

### 5.2 ランタイム中核データ構造
```csharp
struct Sim3 { float s; Quaternion R; Vector3 t; }      // Unity系
struct AlignmentSnapshot { Sim3 T; float confidence; double srcTimestamp; int version; }
struct Keyframe { ... §5.1 KEYF のメモリ版 ... }
struct LiveFrame { Texture2D/NativeArray img; double ts; float[] K; Pose T_w_cam; }
```
`AlignmentState.Current` は `AlignmentSnapshot`（イミュータブル, アトミック差し替え）。

---

## 6. Stage A — オフライン前処理（Editor / Python）

`Editor/RegistrationBaker`。メニュー `Tools > VPTAligned3DGS > Bake Registration DB` と CLI 両対応。

### 6.1 手順（確定アルゴリズム）
1. **`.ply` → `GaussianSplatAsset`** 変換（aras-p のアセット作成ツールを呼ぶ）。品質レベルは引数（既定 Medium, `CLAUDE.md` §6）。**ここで COLMAP→Unity の座標変換はインポータに委ね**、以降の全点は `m`（Unity 左手）で扱う。
2. **床面・重力推定**: スプラット中心点群に RANSAC 平面フィット（最大の水平面を床と仮定）。法線の上向きを重力上方向 `gravityUp_m`、平面係数 `floorPlane_m` を記録。失敗時は MRUK 床と整合させる手動指定にフォールバック（要確認 UI）。
3. **仮想カメラ生成**（設計書 §5.1 確定値）:
   - 歩行可能領域 = 床面投影での占有グリッド（セル `grid.spacing`=0.75m）。各セル中心、目線高さ `cam.height`∈{1.2,1.45,1.7}m。
   - 各位置でヨー `cam.yawStep`=36°（10 方向）、ピッチ 0°（必要なら ±15° を追加可）。
   - **無効ビュー除外**: その姿勢のレンダ深度の有効画素率 < `bake.minValidDepth`(=0.5) または平均不透明度 < `bake.minOpacity`(=0.3) を破棄（壁内部・空ビュー排除）。
   - 画角は `pca.fovHint`（実機 PCA に近い、既定 水平 70°, アスペクト 4:3）。
4. **各ビューをレンダ**（aras-p レンダラを Editor で off-screen 描画）→ RGB＋深度。
5. **特徴抽出**: `featureMethod`(=XFeat) で局所キーポイント＋記述子（int8 量子化, `descDim`=64）、`globalDescMethod`(=GeM) で大域記述子（fp16, 512）。
6. **逆投影**: 各キーポイントをレンダ深度で `m` の 3D 点へ（`p_m`）。深度無効点は `validDepth=0`。
7. **DB 品質ゲート**: キーポイント数 < `bake.minKp`(=150) または有効 3D 点率 < 0.4 のビューは破棄。残ビューを `KEYF` に格納。
8. **点群 `P_m`**: スプラット中心をボクセルダウンサンプル（`icp.voxel`=2cm 相当のモデル単位＝スケール未確定なので `bounds` 比で決定）し法線推定→`PNTS`。
9. **強化モード（任意）**: COLMAP の元画像・姿勢・疎点があれば、レンダ画像の代わりに**実画像から特徴抽出**して `KEYF` を差し替え（`sourceMode="colmap"`）。姿勢は COLMAP→`m` へ §6.1-1 と同じインポータ規約で変換。
10. **書き出し**: §5.1 の 2 ファイル。Editor に「キーフレーム数・点数・推定メモリ（§13 式）・所要時間」を表示。

### 6.2 受け入れ基準（Stage A）
- DB ロード往復で全フィールドがビット一致（§15）。
- ランダム 20 ビューを相互検索したとき、自己ビューが Top-1（健全性）。
- 推定メモリ合算 < `mem.budget`（§13）。超過時は `grid.spacing↑`/`keyframe 上限`で自動間引き。

---

## 7. Stage B — 大域ローカライズ（初期化・復帰）

入力: `LOOKAROUND` で収集した k 枚のライブキーフレーム（`I, K, T_w_cam, ts`）。出力: `T_{m→w}` 初期値 + 信頼度。

### 7.1 ライブキーフレーム収集（§6.1 設計書の確定値）
- 採用条件: 前 KF から 回転 ≥ `kf.rotDeg`(=10°) **または** 並進 ≥ `kf.transM`(=0.15m)。
- 品質ゲート: IMU 角速度 < `kf.maxAngVel`(=30°/s)（ブラー回避）、輝度が `kf.lumaRange`(=[0.15,0.9]) 内。
- 枚数: `kf.min`(=3)〜`kf.max`(=8)。各 KF は撮影時刻姿勢で確定（§4.3 設計書）。

### 7.2 重力事前整合
- ライブの重力下方向は HMD/IMU から既知。`gravityUp_m`（DB）とライブ重力を合わせ、**回転をヨー1自由度に縮約**。以降の RANSAC/最適化はヨー＋並進(3)＋スケールの5自由度を主に探索（残り2回転は微小残差として §8 で吸収）。

### 7.3 外観ルート（既定の主経路）
各ライブ KF について:
1. **大域検索**: ライブ大域記述子 vs DB 全 `globalDesc` のコサイン上位 `vpr.topK`(=10)。
2. **局所マッチ**: 上位候補と XFeat+LighterGlue でマッチ。相互最近傍＋比率 `match.ratio`(=0.9 for LightGlue系はスコア閾) でフィルタ。
3. **2D-3D 構築**: ライブ 2D ↔ 候補 KF の `p_m`（3D）。
4. **PnP+RANSAC**（`cv` 系, EPnP→ITERATIVE）: 再投影しきい `ransac.reprojPx`(=3.0), conf 0.999。インライア ≥ `pnp.minInliers`(=30) で成功。→ `T_{cv_cam←m}` → Unity 化して `T_{w←cam}` と整合用に `C^m` 算出。
5. **集約**: 成功 KF が ≥3 になったら、`{C^m_i}→{C^w_i}` に **Umeyama+RANSAC**（§7.4）で `T_{m→w}` を推定。

### 7.4 Umeyama（スケール有）+ RANSAC
- 最小サンプル 3 点、しきい `umeyama.inlierM`(=0.05m, w 系残差), maxIter 500。
- 全インライアで最終 Umeyama 再フィット。`s,R,t` を返す。重力拘束時は `R` をヨーに射影してから返す。
- 出力スケール健全性: `s∈[scale.min, scale.max]`（=DB `bounds` から導く妥当域）外なら棄却。

### 7.5 幾何ルート（低テクスチャ・照明変化に強い・併用）
- Environment Depth/MRUK のライブ点群を `w` で取得→重力で床面 2D（x,y）へ射影。
- DB 点群 `P_m` を同様に床面 2D 射影（モデル単位）。
- **2D 相似マッチ**: ヨー＋xy＋スケールの分枝限定/相関（回転は 5° 刻み総当たり、スケールは粗→細）。粗一致を外観ルートの初期値/検証に使用。

### 7.6 仮説検証（成功判定）
スコア `J = w1·inlierRatio + w2·depthConsistency − w3·photoResidual`（重み `verify.w`=[0.6,0.4,0.1]、測光は従）。
- 合格: `inlierRatio ≥ verify.minInlierRatio`(=0.35) **かつ** `depthConsistency ≥ verify.minDepth`(=0.6)。
- 融合: 外観・幾何の両 `T` が `verify.agreeM`(=0.1m)/`verify.agreeDeg`(=3°) 内で一致すれば信頼度を加点。
- 失敗時は §4.2 の遷移。

### 7.7 オンデバイス/オフロードの切替
- 既定はオンデバイス試行。`R1`（§1, Sentis 実時間）が未達なら `offload.enabled=true` でオフロード（§12）。タイムアウト `loc.timeout`(=2.0s) でフォールバック。

---

## 8. Stage C — 初期精密化（一度きり）

入力: Stage B の `T`。出力: 精密化 `T`（数 cm / 数 px）。

### 8.1 幾何 ICP（point-to-plane, scaled→6DoF）
- 対応: ライブ点群（Environment Depth を `w`→`m` に `T⁻¹` で写像）と DB `P_m` の最近傍（KD-tree）。法線整合 `|n_a·n_b|>icp.normalCos`(=0.8)。
- 棄却: 距離 > `icp.maxCorrDist`（=median×3）, Huber δ=`icp.huber`(=0.02m 相当)。
- 解: 第1段 **scaled-ICP（7DoF）**で `s` を含め線形化解、第2段 6DoF で固定 `s` 微修正。
- 終了: 更新量 < `icp.eps`(=1e-4) または `icp.maxIter`(=20)。点数 `icp.points`(=2k–5k, §5.5 設計書)。

### 8.2 特徴/測光 render-and-compare（主=特徴, 従=測光）
- DB 近傍 KF を選び、現 `T` でモデルをレンダ→ライブと特徴マッチ→**再投影誤差**を残差に。
- パラメータ: Sim(3) リー代数 `δ∈ℝ⁷`（§2.6, 左摂動）。コスト `Σ ρ( ‖π(T·p_m) − u_live‖² )`, ρ=Huber(`refine.huber`=2px)。
- 解法: **LM**（初期 λ=1e-3）。ヤコビアン解析（投影×Sim(3) 随伴）。終了: コスト相対変化 < `refine.eps`(=1e-4) または `refine.maxIter`(=15)。
- 測光項は重み `refine.photoW`(=0.1) で従属（照明非依存を優先, 設計書 §9）。発散時は §4.2 で B へ。

### 8.3 受け入れ基準（Stage C）
- フィデューシャル接地真値（§15）に対し 3D RMSE < `acc.rmseCm`(=3cm), 再投影中央値 < `acc.reprojPx`(=3px)。未達なら REFINE 失敗扱い。

---

## 9. Stage D — 連続追従（リアルタイム微調整）

`Tracker` ワーカ。レート `track.hz`(=5–15, 適応)。描画はこの結果を待たない（§3.2）。

### 9.1 予測
- 直近 `T` とヘッドポーズ列から `T` を現在時刻へ前方補正（§4.3 設計書）。CV は撮影時刻に適用→現在へ伝播。

### 9.2 軽量 ICP + 疎特徴トラッキング
- ICP: ダウンサンプル `track.icpPoints`(=2k) で point-to-plane を `track.icpIter`(=1–3) 反復（インクリメンタル）。
- KLT: `track.kltPoints`(=200–500) を前フレームから追跡し、再投影残差で微小 `ΔT`（並進・ヨー主）。

### 9.3 スムージングと予算
- `T` 更新は Sim(3) 上 EMA（係数 `track.ema`=0.3）＋ one-euro（minCutoff=`oe.minCut`=1.0, beta=`oe.beta`=0.5）。
- 1 サイクル CV コスト上限 `track.budgetMs`(=4ms)。超過は**スキップ**（描画継続, 設計書 §5.5）。

### 9.4 残差監視・ロスト判定
- 残差（ICP RMS or 再投影中央値）が `track.lostThresh`(=再投影 12px or 3D 8cm) を `track.lostFrames`(=10) 連続超過で `RELOCALIZING`。
- サーマル/低残差時はレート低下（`track.hz`→下限）。描画最優先。

---

## 10. Stage E（手動）/ F（永続化）詳細

### 10.E ハイブリッド/手動フォールバック
- ハンドジェスチャ or ギズモで root を粗配置（並進・ヨー・スケール）。床スナップ（重力拘束）を補助。
- 「確定」操作で現配置を初期値に **C→D の同一バックエンド**へ。対称部屋・極低テクスチャのブートストラップにも使用。

### 10.F ロック・永続化・監視
- 確定後、近傍に**空間アンカーを ≥2 個生成**し、各アンカー基準で `T_rel = T_{anchor←m}` を保存（`w` 絶対では焼き込まない, 設計書 §8/§F）。
- 復元: アンカーをロード→`T_{w←anchor}`（現在）× `T_rel` で `T_{m→w}` 再構成。複数アンカーで整合（外れは破棄）。`T_rel` は相似変換（スケール含む）。
- `anchorUpdated` イベントで `T` を即補正。全アンカーが復元不能なら `GLOBAL_LOC` へ。
- 保存形式: アンカー UUID＋`T_rel`（Sim3 = 8 float: s, t(3), q(4)）＋`modelId`＋信頼度を JSON（端末ローカル）。

---

## 11. 公開インターフェース（C# 契約）

純粋計算は以下のインターフェースで実装・差し替え可能にする（§1 の代替交換のため）。すべて Unity 非依存型（自前 `Vec3/Quat/Mat` か `System.Numerics`）で受け、結合層が `UnityEngine` 型と変換。

```csharp
// 座標
static class CoordinateConventions {
  Pose ToCv(Pose unity); Pose ToUnity(Pose cv);
  Vec3 ToCv(Vec3 p);     Vec3 ToUnity(Vec3 p);
}
interface IPnPSolver {            // 2D-3D→姿勢
  bool Solve(IReadOnlyList<Vec2> uv, IReadOnlyList<Vec3> xyz_m, Intrinsics K,
             RansacOpts opt, out Pose T_cvcam_m, out int[] inliers);
}
interface ISimilaritySolver {     // Umeyama+RANSAC
  bool Solve(IReadOnlyList<Vec3> src, IReadOnlyList<Vec3> dst, bool withScale,
             out Sim3 T, out int[] inliers);
}
interface IIcpRefiner {           // point-to-plane scaled/6dof
  Sim3 Refine(PointCloud live_m, PointCloud model_m, Sim3 init, IcpOpts opt, out IcpStats s);
}
interface ISim3Refiner {          // render-and-compare LM
  Sim3 Refine(IReadOnlyList<FeatureMatch> m, Intrinsics K, Sim3 init, RefineOpts o, out RefineStats s);
}
interface IFeatureExtractor { Features Extract(ImageGray img); }     // XFeat/ORB
interface IGlobalDescriptor { float[] Describe(ImageGray img); }     // GeM/DBoW
interface IMatcher { Match[] Match(Features a, Features b); }        // LighterGlue/BF
interface IRegistrationDb {
  DbManifest Manifest { get; }
  int[] QueryGlobal(float[] q, int topK);
  Keyframe GetKeyframe(int id);
  PointCloud ModelCloud { get; }
}
interface IAlignmentState { AlignmentSnapshot Current { get; } void Publish(AlignmentSnapshot s); }
interface ILocalizer { Task<LocResult> Localize(IReadOnlyList<LiveFrame> kfs, CancellationToken ct); } // B
interface ITracker { void Step(LiveFrame f, AlignmentSnapshot prev, out AlignmentSnapshot next); }     // D
interface IOffloadClient { Task<LocResult> Solve(LocRequest req, CancellationToken ct); }              // §12
interface IPersistence { void Save(Sim3 T, string modelId); bool TryRestore(string modelId, out Sim3 T); }
```

各 `*Opts` の既定値は §13。実装は `Core/` に、結合は `Runtime/` に置く。

---

## 12. PC オフロード（Stage B-PC）

> **v2 注記（実装済みの形が正）**: §12.1〜12.4 は「特徴送信＋TCP＋NACK」の設計。**現行実装はより簡素**で、以下が正:
> - **トランスポート**: UDP ベストエフォート（TCP ではない）。13B ヘッダ ＋ ペイロード（`['V','P', type, reqId(4 LE), idx(2 LE), total(2 LE), len(2 LE)]`）。type=1=META(UTF-8 JSON), type=2=IMG(JPEG チャンク, 既定 1100B)。取りこぼしキーフレームは破棄→再キャプチャ（NACK 未実装）。
> - **ディスカバリ**: PC が周期ビーコン（UDP **50001**, `"VPTSRV1"`＋データポート(u16)）をブロードキャスト → Quest が送信元 IP を自動学習。手動 IP 指定も可。
> - **ポート**: データ **50000** / ビーコン **50001**（§13 の 48010/48011 は旧設計値）。
> - **送信内容**: 実機は**特徴ではなく縮小 JPEG**（長辺 512・品質 60）＋ META（K@sensor, ワールド姿勢 `T_{w←cam}`, タイムスタンプ）。PC が**全 CV**（特徴抽出・COLMAP 再ローカライズ・PnP・Umeyama）を担う。
> - **DB**: `.ply` 仮想レンダではなく**ユーザー保有 COLMAP（`sparse/0`＋元画像）**を PC 側 DB とする。
> - **実装**: Quest `KeyframeUploader.cs`/`UDPManager.cs`、PC `server/`（`vptserver/`: protocol, reassembler, udp_receiver, keyframe, storage, beacon ＋ run.py）。Sim(3) 返送（LOC_RESP 相当）は M3 ステップ3 で追加。

### 12.1 役割分担
実機は**特徴抽出まで**を行い（生画像は送らない, 設計書 §5.4）、PC が検索・マッチ・PnP・Umeyama を解いて `Sim(3)` を返す。座標規約は実機と一致（送受とも `m`/`w` 系を明示）。

### 12.2 トランスポート（確定）
- **ディスカバリ**: 起動時 UDP ブロードキャスト `PING`→PC `PONG`（IP/ポート/能力）。手動 IP 設定も可。
- **本体**: **TCP**（信頼性は TCP に委任、設計書 §5.4 の自作 NACK 層は不要）。1 メッセージ = `[uint32 len][header][payload]`。
- LAN 内・非暗号化（研究用途）。必要なら TLS。

### 12.3 メッセージ（バイナリ, little-endian）
ヘッダ（16B）: `magic(2)="VP" | version(1) | msgType(1) | requestId(4) | payloadLen(4) | crc32(4)`。
| msgType | 方向 | payload |
|---|---|---|
| `PING(1)` | 実機→PC(UDP) | clientInfo |
| `PONG(2)` | PC→実機(UDP) | serverInfo, caps |
| `LOC_REQ(3)` | 実機→PC(TCP) | `nKf(u16)` + 各 KF: `K(4f)`,`T_w_cam(7f)`,`ts(f64)`,`nKp(u16)`,(`u,v(2×f16)`,`desc(int8×D)`,`score(f16)`)*, `globalDesc(f16×G)` |
| `LOC_RESP(4)` | PC→実機(TCP) | `status(u8)`, **Sim3 = `s(f32)`,`t(3×f32)`,`q(4×f32: x,y,z,w)`（計 8 float）**, `inliers(u16)`, `confidence(f16)` |

> `LOC_REQ` 概算: 1KF ≈ 1024×(4+64+2)B ≈ 72KB＋大域 1KB。k=3–8 で 0.2–0.6MB（TCP なので分割は OS 任せ）。

### 12.4 PC 参照実装（`pc_offload/`, Python）
- `discovery.py`（UDP）, `server.py`（TCP, スレッドプール）, `solver.py`（hloc 流: NetVLAD 検索→SuperPoint+LightGlue→pycolmap/OpenCV PnP→Umeyama）。
- DB は実機と同一 `modelId` の**実画像/レンダ DB**を PC 側にも保持（高精度版）。`requestId` で重複排除。
- 返却 `Sim3` は `m`→`w`。実機はそのまま §2.5 で適用。

---

## 13. パラメータ一覧（単一の真実）

実装は `AlignConfig`（ScriptableObject）に集約し、ここを唯一の編集点とする。`約` は使わない。

| キー | 既定 | 単位 | 範囲 | 使用 |
|---|---|---|---|---|
| `grid.spacing` | 0.75 | m | 0.3–1.5 | A:仮想カメラ |
| `cam.height` | {1.2,1.45,1.7} | m | — | A |
| `cam.yawStep` | 36 | deg | 15–60 | A |
| `pca.fovHint` | 70 | deg(水平) | — | A |
| `bake.minValidDepth` | 0.5 | 比 | 0–1 | A 除外 |
| `bake.minOpacity` | 0.3 | 比 | 0–1 | A 除外 |
| `bake.minKp` | 150 | 個 | — | A ゲート |
| `descDim` | 64 | 次元 | — | 局所記述子 |
| `globalDim` | 512 | 次元 | — | 大域記述子 |
| `kf.rotDeg` | 10 | deg | — | B:KF採用 |
| `kf.transM` | 0.15 | m | — | B |
| `kf.maxAngVel` | 30 | deg/s | — | B ブラー |
| `kf.lumaRange` | [0.15,0.9] | 比 | — | B 露出 |
| `kf.min/max` | 3 / 8 | 枚 | — | B |
| `lookaround.timeout` | 12 | s | — | 状態機械 |
| `vpr.topK` | 10 | 個 | — | B 検索 |
| `pnp.minInliers` | 30 | 個 | — | B |
| `ransac.reprojPx` | 3.0 | px | — | B PnP |
| `umeyama.inlierM` | 0.05 | m | — | B |
| `verify.minInlierRatio` | 0.35 | 比 | 0–1 | B 検証 |
| `verify.minDepth` | 0.6 | 比 | 0–1 | B 検証 |
| `verify.agreeM/Deg` | 0.10 / 3 | m/deg | — | 融合 |
| `gate.minInliers` | 30 | 個 | — | P1 KF採否(=pnp.minInliers) |
| `gate.minInlierRatio` | 0.5 | 比 | 0–1 | P1 解公開/Quest採否 |
| `gate.maxRmseM` | 0.05 | m | — | P1 解公開/Quest採否 |
| `gate.scaleRange` | [0.5, 2.0] | — | — | P1 |σ|妥当域(既知≈0.96) |
| `gate.rmseWorsenFactor` | 1.5 | 倍 | — | P1 Quest改悪防止 |
| `cv.window` | 24 | 枚 | 0=無制限 | P2-b 直近N件で解く |
| `kf.autoMinIntervalS` | 2.0 | s | — | P2-a 自動送信の最短間隔 |
| `smooth.maxPosM/Deg` | 0.3 / 10 | m/deg | — | P2-c 補間する差の上限 |
| `smooth.durationS` | 0.5 | s | 0=即時 | P2-c 補間時間 |
| `save.posM/rotDeg` | 0.02 / 1 | m/deg | — | P2-c 保存抑制しきい |
| `save.scaleFrac/intervalS` | 0.005 / 30 | 比/s | — | P2-c 保存抑制 |
| `anchor.count` | 2 | 個 | ≥2 | P4 冗長化 |
| `anchor.sepM` | 1.5 | m | — | P4 アンカー間隔 |
| `anchor.restoreOutlierM` | 0.05 | m | — | P4 復元外れ判定 |
| `loc.timeout` | 2.0 | s | — | B/オフロード |
| `icp.voxel` | 0.02 | m | — | C/D |
| `icp.normalCos` | 0.8 | — | 0–1 | C/D |
| `icp.huber` | 0.02 | m | — | C/D |
| `icp.eps/maxIter` | 1e-4 / 20 | — | — | C |
| `refine.huber` | 2 | px | — | C |
| `refine.eps/maxIter` | 1e-4 / 15 | — | — | C |
| `refine.photoW` | 0.1 | 重み | 0–1 | C |
| `acc.rmseCm/reprojPx` | 3 / 3 | cm/px | — | 受け入れ |
| `track.hz` | 5–15 | Hz | — | D |
| `track.budgetMs` | 4 | ms | — | D |
| `track.ema` | 0.3 | — | 0–1 | D 平滑 |
| `oe.minCut/beta` | 1.0 / 0.5 | — | — | D one-euro |
| `track.lostThresh` | 12px/8cm | — | — | D ロスト |
| `track.lostFrames` | 10 | フレーム | — | D |
| `offload.enabled` | false | — | — | §12 |
| `net.discoveryPort` | 48010 | — | — | §12 |
| `net.tcpPort` | 48011 | — | — | §12 |
| `mem.budget` | 実機実測 | MB | — | §5.6 設計書 |

---

## 14. 設定・ビルド・権限

- ビルド: Android / ARM64 / IL2CPP / **Vulkan**（`CLAUDE.md` §2,§9）。
- URP: Render Graph **Compatibility Mode Off**、Render Mode = **Multi-Pass（確定）**。aras-p v1.1.1 は Single Pass Instanced 非対応のため Multi-Pass を使う（CLAUDE.md §0/§6）。`GaussianSplatURPFeature` を PC・Mobile 両 URP Renderer に付与する。
- OpenXR/Meta feature: Passthrough, Anchors, Scene/MRUK, **Camera（PCA）**。本プロジェクトは Meta XR SDK（OVR）リグ＋Building Blocks で構成（パススルー実機確認済み）。
- **カメラ取得は `PassthroughCameraAccess`（Building Block「Passthrough Camera Access」で配置, MRUK v81+）**。旧 `WebCamTextureManager`/`PassthroughCameraUtils` は使わない。`OculusProjectConfig` で **`isPassthroughCameraAccessEnabled=true`**、実行時に `OVRPermissionsRequester` で権限要求。
- マニフェスト権限: **`horizonos.permission.HEADSET_CAMERA` のみ**（新 API では `android.permission.CAMERA` は不要）。拒否時は MANUAL/縮退（幾何・深度ルートへ）。
- バージョン要件: **Horizon OS v81+・MRUK v81+**（`PassthroughCameraAccess` の要件。PCA 自体は v74+ だが新コンポーネントは v81+）。XR Simulator は PCA 非対応 → 実機か Horizon Link v2.1+ で確認。
- **解像度と K（v2 確定）**: Quest3 の PCA は 1280×960(4:3) と 1280×1280(1:1, v83+/広い垂直FOV/正方ピクセル) を持つ。`RequestedResolution` で固定でき、`PassthroughCameraSource` で 1280×1280 を要求しているが、環境により 1280×960 が返る場合がある（`CurrentResolution != Intrinsics.SensorResolution` になりうる）。**K は正方ピクセル＋中央縦クロップ前提で算出**するため、PC 側 `K_for_image` が解像度差を吸収する（Specification §8）。`Intrinsics.SensorResolution` を K の基準として META に載せる（`KeyframeUploader`）。
- **MulticastLock 権限**: サーバ自動検出のビーコン受信に `android.permission.CHANGE_WIFI_MULTICAST_STATE`（＋`ACCESS_WIFI_STATE`）が必要。`AndroidManifest.xml` に追加済み。ただしビーコンはブロードキャストのため、AP がブロードキャストを遮断する環境では届かない（その場合は手動 IP 指定にフォールバック, §12 v2 注記）。
- Sentis モデル資産（XFeat/GeM の `.sentis`）を Resources/Addressables に格納。`featureMethod` と DB マニフェストの一致を起動時に検証。

---

## 15. テスト・受け入れ基準

### 15.1 単体（EditMode, `Core/` 対象, Unity 不要）
- `CoordinateConventions`/`Sim3`/`SE3`: 既知姿勢の**往復一致**（Unity→cv→Unity が誤差 <1e-5）、`exp/log` 往復、`S R S` の `det=+1` と直交性。
- `Umeyama`: 合成データ（既知 s,R,t＋ノイズ）で `s` 誤差 <1%, R 誤差 <0.5°, t 誤差 <1mm。RANSAC が外れ値 30% で頑健。
- `PnPSolver`: 合成 2D-3D でインライア復元・姿勢誤差 < 閾。
- `IcpRefiner`/`Sim3Refiner`: 既知ズレからの収束、発散検知。
- `OffloadProtocol`: 直列化往復のビット一致、CRC 検証、壊れフレーム拒否。
- `RegistrationDb`: §6.2 のロード往復一致。

### 15.2 結合（PlayMode / 実機）
- Stage A: §6.2。
- Stage B: 録画済みデータセットでコールドスタート成功率 ≥ `acc.coldStart`(=80%, 初期目標), 収束 < `acc.convergeS`(=5s)。
- Stage C: §8.3（RMSE/再投影）。
- Stage D: 既知軌道で経時ドリフト < 2cm/分, 追従レイテンシ < 100ms, 予算内スキップ率 < 20%。
- Stage F: アンカー再ローカライズ後の `T` 連続性（ジャンプ < 1cm）。

### 15.3 接地真値（ground truth）
- フィデューシャル数点を実空間に固定、トラッキング済みコントローラで既知点に接触→`w` 実測。複数シーン×複数照明で反復（設計書 §11）。

### 15.4 検証ステップ（CI/手動）
- 数値計算は合成データで自動検証（15.1）を CI 必須化。実機項目は §13 値をログ出力し、しきい超過を自動フラグ。

### 15.5 ブリングアップ用デバッグツール（任意）
PCA レイヤの立ち上げ・整合確認に Meta 提供物を活用（製品ビルドには含めない）。
- **Building Block「Passthrough Camera Visualizer」**: カメラ映像をワールド空間に表示し、フレーム取得・解像度・遅延感を目視確認。
  **既定では `y = −100 m` に退避してある**（`GSTestScene` の `[BuildingBlock] Passthrough Camera Visualizer`）。原点に置くと実験中ずっと視界の真ん中に不透明な板が立ち、CP の球や実物を隠す。
  使うときは Inspector で `Position.y` を **0** に戻すだけ（`PassthroughCameraAccess.TargetMaterial` 経由でテクスチャが流れているので、位置を動かしても供給は切れない）。
  **無効化（`SetActive(false)`）ではなく移動**にしてあるのは、この 1 操作で戻せる状態を保つため。
- **CameraToWorld サンプル**: `Intrinsics`＋`GetCameraPose()` による 2D→3D レイ投影の整合（§8 の再投影規約の実機検証）。
- **Camera brightness estimation サンプル**: 輝度推定（§6.1 設計書のキーフレーム露出ゲート `kf.lumaRange` の実装参考）。

---

## 16. 実装フェーズ計画（マイルストン）

| M | 目標 | 含む | 完了判定 |
|---|---|---|---|
| **M0** | レンダラ実機ゲート | `CLAUDE.md` §0 検証 | 両眼貼り付き・fps 合格 |
| **M1** | 骨格＋手動＋追従(Depth) | §3,§4 骨格／Stage E 手動配置／Stage D ICP／Stage F アンカー | 手動合わせ→貼り付き維持、セッション復元 |
| **M2** | オフライン DB＋外観初期化 | Stage A（レンダ DB）／Stage B 外観ルート／Stage C 精密化 | 自動コールドスタート成功（§15.2） |
| **M3** | 幾何ルート融合＋ロバスト化 | §7.5 幾何ルート／検証融合／失敗モード対策（設計書 §10） | 低テクスチャ・照明変化でも成功率維持 |
| **M4** | PC オフロード | §12（TCP＋ディスカバリ）／PC 参照実装 | オンデバイス未達時に等価精度で代替 |
| **M5** | 最適化・サーマル | 量子化最終化・レート適応・メモリ予算 | 持続 fps・予算内（§13,§15.2） |

各 M は「単体テスト緑→結合テスト→実機確認→`CLAUDE.md`/本書のパラメータ同期」を 1 サイクルとする。

> **v2 進捗対応（現行の実装順とマッピング）**: 上表は v1 の網羅計画。現行は scalabroom 基盤で次の順に実装中・完了。
> - **M0**（レンダラゲート）= **完了**（scalabroom＋Multi-Pass＋Vulkan）。
> - **M1 手動合わせ** = **完了・実機確認済み**。コントローラ操作: 右スティック=水平移動／左スティックY=上下・X=ヨー／インデックストリガ=スケール／A=ロック・B=リセット・X=表示トグル・Y=アンロック／右スティック押込=正面リセンタ・左スティック押込=自動水平＋床スナップ。`AlignmentManager.cs`。受付範囲は `PoseControlMode`（Full / **SticksOnly**（**Real CP 測定中・Virtual CP 編集中**。スティックのみ）/ Locked（試行中））で切り替わり、X の表示トグルはどの状態でも効く。**起動時はスプラット非表示**（`startHidden`）。HUD の局面は `ExperimentController.UiPhase`（`NeedsSetup` / **`EditingCPs`**（HUD `EDIT VIRTUAL CONTROL POINTS`）/ `Surveying`（HUD `SET REAL CONTROL POINTS`）/ `Ready` / `Running` / `Review` / `AllDone`）で、`EditingCPs` を `Surveying` より先に判定する。**列挙子名は変えない**（HUD の文言だけを Virtual CP / Real CP に統一し、型名・ログ名・JSON の鍵は過去ログとの互換のため据え置く → `Specification.md` §11 の用語表）。
> - **M1.5 空間アンカー永続化** = **完了・実機確認済み**。`AnchorPersistence.cs`（OVRSpatialAnchor、UUID＋スケールを PlayerPrefs、ロック時保存・起動時復元）。
> - **M2 半自動初期化** = **完了・実機確認済み**。水平化（ヨーのみ）＋スプラット bbox 底を床 y にスナップ。`AlignmentManager.AutoLevelAndFloorSnap()`。
> - **M3 カメラ取得＋オフロード** = **完了・実機確認済み**。`PassthroughCameraSource.cs`（PCA ラップ・解像度固定・K 診断）＋`KeyframeUploader.cs`（左グリップで JPEG キーフレーム送信）＋PC `server/` 受信。
> - **次: M3 ステップ3** = PC 側 CV（COLMAP 再ローカライズ → PnP → 複数 KF で Sim(3) → Quest 返送 → `AlignmentManager` で splatRoot 適用）。上表の M2/M4 の自動初期化・オフロード本体に相当。
> - 以降: M3 ステップ4（連続追従・残差監視, 上表 M3/M5 相当）。
> - **M6 評価基盤** = **実装完了・実機検証待ち**。論文の評価（**収集時間 vs 精度の曲線**）を実施するための一式。`Assets/Scripts/Experiment/`（`ExperimentController` / `ExperimentLogger` / `ControlPointMeasure` / **`ControlPointEdit`** / `CpGizmos` / `ControllerTipCalibration` / `ControlPointStore` / `TrackingOriginWatcher`）＋ `Assets/Editor/ControlPointToolWindow.cs` ＋ `server/analysis/`（`experiment_report.py` が主結果、`analyze_experiments.py` は実測の健全性）。UDP に **type=4 RESET** と **type=7 EVENT** を追加し、`run.py` と Quest アプリを再起動せずに独立試行を回せる／`adb pull` なしで解析が完結するようにした。実機での 1 試行は「一定時間歩く」だけで、**Real CP（実物側の位置）の測定は実験前に 1 回**（先端校正と同じ前準備）。手順は `readme.md` §9、設計上の注意は `Specification.md` §11（用語 Virtual CP / Real CP を含む）と `CLAUDE.md` §13。
>
> **M6 の確定パラメータ**（実機で調整したら本節を更新）:
>
> | 項目 | 既定値 | 根拠 |
> |---|---|---|
> | `ExperimentController.collectSeconds` | 60 s | 1 試行の収集時間。曲線の右端はここで決まる |
> | `ExperimentController.plannedTrials` | 10 | 試行数（HUD の進捗表示用。強制ではない）|
> | `ExperimentController.resetSettleS` | 1.0 s | PC が新 session フォルダを作り終えるまでの待ち |
> | `ControllerTipCalibration.minSamples` | 24 | 未知数 6 に対する十分な冗長性 |
> | `ControllerTipCalibration.minSpreadDeg` | 45° | 向きの広がり不足だと残差が小さいまま解がずれるため（数値検証で確認） |
> | `ControllerTipCalibration.minSampleAngleDeg` | 6° | 静止中の同一姿勢の重複を避ける |
> | `ControllerTipCalibration.requireCalibrationEachSession` | true | 保存値が今の物理構成に対応している保証がない。ずれても事後に検出できないため毎セッション校正する |
> | `ControllerTipCalibration.driftWarnMm` | 5 mm | 前回値との差の警告閾値（コントローラ・治具の取り違え検出）|
> | `ControlPointMeasure.allowUncalibrated` | false | 未校正で測った Real CP は系統誤差込みで論文に使えない |
> | `ControlPointMeasure.minTipSeparationM` | 0.10 m | 同じ場所で 2 点記録する事故を弾く。**誤差ではなく移動量**で判定する（誤差で弾くと結果にバイアスがかかる）。比べるのは **tip と 1 つ前の tip**（実測値どうし）で、Virtual CP の位置とは比べない |
> | `ControlPointMeasure` のしきい値の緩和 | `max(0.03, min(0.10, 0.5 × 実距離))` | CP どうしが近いと 10cm では正しい記録まで弾く（このシーンの CP_02–CP_03 は約 15cm）。model 上の点間距離で緩めるが、**m に直せるときだけ**（`TryGetMetricScale`: 姿勢適用済みなら splatRoot のスケール／未適用でも実測点が 2 点以上あれば tip 間距離 ÷ model 距離の中央値）。**位置合わせ前の splatRoot のスケールは使わない**——手動合わせの途中値（現状 0.7）で m との比を表しておらず、小さい値だとしきい値が 1cm 未満に落ちてガードが黙って無効になる。下限 3cm は「動かさず 6 点連打（全体の広がり 6cm）」を確実に捕まえる床 |
> | `ControlPointMeasure.markerRadius` | 0.025 m | 目印の球の半径（＝直径 5cm。実測点の橙はこの 1.2 倍）。**`CpGizmos.RefDistM` 以内での実寸**。splatRoot のスケールは毎 `LateUpdate` で打ち消す。**シリアライズ値がコード既定値より優先される**ので、変えるときはシーンの `Managers` も MCP で直すこと |
> | `CpGizmos.ShaderCandidates` | `Unlit/Color` → `Universal Render Pipeline/Unlit` → `Sprites/Default` | 球に使うシェーダ。**`Unlit/Color` は Always Included Shaders に登録済み**（未登録だとビルドで剥がされ、実機だけ球がピンク＝エラーシェーダになる）。全滅時は `LogError` で直し方を出す（黙ってピンクを出さない）|
> | `ControlPointMeasure.labelHeightM` | 0.05 m | CP 名ラベルの文字高（`RefDistM` 以内での実寸）。球の真上に置きヨーのみビルボード |
> | ラベルの文字（`CpGizmos.VirtualLabel`/`RealLabel`） | 球=`VCP_01` / 実測点=`RCP_01`（編集前のゴーストは `VCP_01 (old)`）| 同じ点の 2 つの側（model 上の座標と実測値）が視界の中で隣り合うので、**見分けを色だけに載せない**（色の印象はパススルーの背景と照明で変わる）。HUD も `CurrentLabel` 経由で同じ文字を出し、指示と視界を突き合わせられるようにする。**変わるのは表示だけ**で、GameObject 名・ログ・UDP・JSON の `name` は `CP_01` のまま（過去ログと解析スクリプトの互換）|
> | `CpGizmos.RefDistM` | 1.0 m | **この距離までは上の実寸、それより遠い点は距離に比例して拡大**し見かけの大きさを一定に保つ。実寸固定だと 3m 先の球は 0.5°（10px 前後）にしかならずスプラットの背景に埋もれ、実機で「CP_01 だけ見えて CP_02〜06 が見えない」（＝描画はされているのに探せない）と報告された。1m 以内を実寸のままにするのは、先端を当てるときの精度を落とさないため |
> | `CpGizmos.MaxGrowth` | 6 | 拡大の上限倍率（部屋の対角より遠くても際限なく大きくしない）|
> | `CpGizmos.CurrentBoost` | 1.6 | いま対象の点だけ更に拡大する倍率。あわせて球とラベルを繋ぐ縦の棒（`CPStem`）を立て、部屋の反対側からでも「次はここ」を拾えるようにする |
> | `ControlPointEdit.allowUncalibrated` | false | 未校正だと先端オフセット（数 cm）がそのまま Virtual CP に焼き込まれる。Real CP の測定と同じ理由で必須 |
> | `ControlPointEdit.warnMoveM` | 1.0 m | 元の位置からこれ以上動いたら誤タッチを疑って HUD で警告（記録は止めない）|
> | `ControlPointEdit.writeDeviceJson` | true | 端末側 `persistentDataPath` にも書く（PC への UDP が落ちたときの冗長化。**起動時に自動復元はしない**）|
> | `ExperimentController.aLongPressS` | 1.0 s | A の短押し=`Set Real CP` トグル／長押し=`Edit Virtual CP`（`ControlPointEdit`）開始終了の境目。離す前に発火させ、触覚で「離してよい」を伝える |
> | `AlignmentManager.startHidden` | true | 起動時はスプラット非表示。位置合わせ前のスプラットは視界を塞ぐうえ「もう合っている」と誤解させる。以後は**いま何を見る作業か**で自動切替（Virtual CP 編集開始=表示 / Real CP 測定開始=非表示 / 試行の収集開始=非表示 / 姿勢適用=表示）、X で手動切替 → `readme.md` §5-3 |
> | `ControlPointMeasure.GizmosActive` | （プロパティ）| CP の球・ラベル・記録点を出す条件。**Real CP の測定中と Virtual CP の編集中は `SplatVisible` に連動させない**（測定中はスプラットを消すので、連動させると「次はどの点か」の案内ごと消える）。位置合わせ済みの通常表示では従来どおり 3DGS と連動 |
> | `AlignmentHud` の文言 | **英語のみ** | 実機で読むのは HUD だけなので日本語と混ぜない（日本語はログと `[Tooltip]` に限る）。HUD へ出る文字列を返すプロパティ（例: `UDPManager.TargetDescription`）も英語で書く |
> | `AlignmentHud.AddText` の折り返し | `Wrap` ＋ best fit（下限 = 指定サイズ × 0.60（1 行の欄）/ 0.70（複数行の欄）、最小 10px）| `HorizontalWrapMode.Overflow` は**クリップしない**ので長い行が枠の外へ出る。best fit で縮めて枠内に収める。10px は 1px=1mm・約 1.2m 先で読める下限 |
> | `AlignmentHud` 準備チェック行の高さ | 40 px（2 行）／文字 15px | 常態（Server/CV/Tip/Real CP）は 319px で 1 行に収まるが、Tracking・Upload・Virtual CP edited が加わると 650px（欄幅 476px の 1.4 倍）になる。**一番読ませたい警告の局面**なので best fit で 10px まで縮めず、2 行めを許して折り返す |
> | `AlignmentHud` パネル高 `h` | 688 px（実験用）/ 510 px（簡易）| 上の 2 行化で 670 → 688。行を足したらここも見直す |
> | `UDPManager.pingIntervalS` / `serverTimeoutS` | 2 s / 6 s | 疎通確認。`HasTarget`（IP を知っている）は疎通判定に使わない |
> | `run.CV_STALE_S` | 8 s | CV ハートビート（2 秒ごと）の失効。run.py だけ動いて CV 未起動の検出 |

---

## 17. 既知の未確定（実機で確定する変数）

本書の既定値は実装可能性を満たすための初期値。次は**実機実測で確定**する（設計書 §12 と対応）: 各しきい値（§13）、量子化モデルの精度/速度、オンデバイス大域ローカライズの実時間可否（不可なら `offload.enabled=true` 既定化）、サーマル持続、仮想レンダ DB のドメインギャップ影響、PCA-RGB↔Depth 再投影誤差、メモリ総量。確定したら本書 §1/§13 を更新し、`Specification.md` §12 と整合させる。
