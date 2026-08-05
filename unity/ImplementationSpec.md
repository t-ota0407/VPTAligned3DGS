# ImplementationSpec.md — VPTAligned3DGS 実装仕様（as-built）

本書は **実際に動いている実装の契約**を、型・単位・既定値・終了条件つきで記述する。曖昧語（「約」「など」「適宜」）は使わない。

- **なぜこの設計か**（トレードオフ・検討して採らなかった案）→ [`Specification.md`](Specification.md)
- **どう動かすか**（セットアップ・操作・実験手順・トラブルシュート）→ [`OperationGuide.md`](OperationGuide.md)
- **Unity/XR の作業規約** → リポジトリ直下 [`CLAUDE.md`](../CLAUDE.md)

> **v4（2026-08）での変更**: v3 までの本書には、検討したが**採用しなかった設計**（Unity Sentis によるオンデバイス推論・XFeat/GeM・`.vptdb` 独自 DB 形式・TCP＋NACK 再送・`pc_offload/`・ポート 48010/48011・Stage C/D の ICP 連続追従）が本文として残り、冒頭注記だけがそれを否定していた。読者が本文を実装だと誤読するため、**本文を as-built のみに書き直した**。撤回した設計の記録は `Specification.md` §12 の要約と git 履歴に残る。

---

## 0. 用語・記法

- `T_{a←b}`: `b` 座標系の点を `a` 座標系へ写す変換。`p_a = T_{a←b} · p_b`、合成は `T_{a←c} = T_{a←b} · T_{b←c}`。
- 剛体変換は `(R, t)`、相似変換は `(s, R, t)`。**本アプリの `s` は負値を取りうる**（§2.4）。
- フレーム: `m`（3DGS モデル＝splatRoot ローカル）、`c`（COLMAP）、`w`（Unity ワールド）、`cam`（パススルーカメラ）。
- 時刻 `t` は秒。センサ値は**キャプチャ時刻のタイムスタンプ**を持つ。
- 略語（Sim(3) / PnP / hloc / VPR / NetVLAD ほか）は `Specification.md` §14。

---

## 1. システム構成

3 プロセスが疎結合で連携する。**重い CV だけが WSL2 にある。**

```
[Quest 3 アプリ]  --UDP-->  [Windows: run.py]  --共有フォルダ(/mnt/c)-->  [WSL2: localize]
 Unity/URP/OpenXR           受信・保存・返送・GUI        hloc 再ローカライズ → Sim(3)
 3DGS 描画＋パススルー        50000/50001/50002        SuperPoint/NetVLAD/LightGlue/PnP
      ^                                                            |
      +---------------- Sim(3) を type=3 で返送 --------------------+
```

**WSL2 を挟む理由**: pycolmap は Windows ネイティブで `reconstruction.rigs` にアクセスした時点で access violation で落ちる（pycolmap 4.0.4 / 3.13.0 の両方、numpy のバージョン・torch の同居に依らず、torch を入れない純 pycolmap でも再現）。Linux では安定するため、hloc/pycolmap を使う処理だけを WSL2 に置く。UDP の受信と返送を Windows に残すのは、Quest との通信経路を単純に保つため。

### 1.1 ファイル配置（実体）

```
server/                                    ← PC 側
├─ run.py                                  受信・ビーコン・Sim(3) 返送・GUI ホスト
├─ requirements.txt / requirements-cv.txt  コア依存 / CV 依存
├─ scene_data/<name>/                      COLMAP 一式（sparse/0 + images/）
├─ received/session_<時刻>/                 セッション（KF・cv.json・result_sim3.json）
├─ analysis/                               評価実験の集計
├─ tests/                                  純粋計算のテスト
└─ vptserver/
   ├─ protocol / reassembler / udp_receiver / keyframe / storage / beacon / gui
   └─ cv/ colmap_model, geometry, gates, hloc_db, migrate_model, localize

unity/projects/VPTAligned3DGS/Assets/
├─ Scripts/
│  ├─ UDPManager             送信・ビーコン自動検出・結果受信・PING/PONG・イベント再送
│  ├─ KeyframeUploader       キーフレーム送信（左グリップ 短押し単発 / 長押し自動）
│  ├─ PassthroughCameraSource PCA ラップ（解像度・K 診断）
│  ├─ AlignmentManager       姿勢の真実。手動操作・Sim(3) 受理・補間適用・Lock
│  ├─ AnchorPersistence      空間アンカーによる永続化
│  ├─ GaussianSplatController 表示・不透明度・ModelId
│  ├─ AlignmentHud           ヘッドセット内 HUD
│  ├─ IKTargetManager / AvatarVisibility  アバター重畳（VRIK）
│  └─ Experiment/            評価実験一式（§9）
└─ Editor/ControlPointToolWindow.cs        Virtual CP の配置・JSON 取り込み
```

---

## 2. 座標系と数学的規約（実装の最重要部）

座標バグは本アプリ最大の事故要因。**符号・軸の操作をコード各所に散らさない**（`CLAUDE.md` §4-4）。

### 2.1 フレーム定義

| フレーム | 定義 | 単位 |
|---|---|---|
| `c`（COLMAP） | SfM 再構成の座標系。**右手系** | 任意単位（スケール不定） |
| `m`（モデル） | `GaussianSplatRenderer` ルートのローカル座標 | `c` と**数値が同一**（§2.2） |
| `w`（ワールド） | Unity 左手系・Y-up・フロアレベル原点 | メートル |
| `cam` | パススルーカメラ。`T_{w←cam}(t)` と `K` をフレーム毎に取得 | メートル |

### 2.2 モデル座標 = COLMAP 座標（verbatim 読み込み）

aras-p の `GaussianFileReader` / `GaussianSplatAssetCreator` を確認した結果、**PLY の点座標は軸反転なしでそのまま読み込まれる**（回転だけ表示用の swizzle 補正が入り、位置は不変）。したがって:

```
m 座標の数値 = COLMAP 座標の数値
```

COLMAP→Unity の軸変換はどこにも入っていない。**この事実が §2.4 の負スケールの根拠**になる。

### 2.3 内部行列 K（正方ピクセル＋中央縦クロップ）

Meta `PassthroughCameraAccess` は**正方ピクセル**（`Intrinsics.FocalLength.x == .y`）。`Intrinsics.SensorResolution`（例 1280×1280）と `CurrentResolution`（例 1280×960）は一致しないことがあり、低いほうは**センサを中央で縦にクロップした**もの（1280×1280 が「垂直 FOV を拡張」した側）。

**クロップは焦点距離[px]を変えない。** よって素朴な `fy *= h/sensorH` は誤り（縦を不当に縮める）。送信画像 `(w,h)` に対応する K は:

1. 非クロップ軸（画像とセンサのアスペクト比較で決まる）から一様スケール `scale` を求める
2. `fx, fy *= scale`（両者は同一値のまま）
3. `cx, cy` は中央クロップのオフセットを引いてから `*= scale`

実装は PC 側 `vptserver/keyframe.py: k_for_image_from_meta()`。META には `Intrinsics.SensorResolution` 基準の K を載せる（`KeyframeUploader`）。妥当性は PnP の再投影誤差で検証する（K が誤っていれば誤差が大きく出る）。

### 2.4 反射は定数（負スケールで表現する）

COLMAP は右手系、Unity ワールドは左手系。§2.2 のとおり軸変換が入らないので、`m`→`w` の整合には**反射**が要る。Unity の Transform は proper rotation ＋ 正スケールでは反射を表せないため、**一様スケールを負値**にして表現する（負スケール＝原点対称の鏡映）。

```
x_w = s · R · x_m + t        s < 0（このプロジェクトでは |s| ≈ 0.96）
```

**手性の違いは座標規約から決まる定数であって、推定すべき未知数ではない。** Umeyama で反射を自由推定させると、共面配置で残差が完全に同じ 2 解が生じて誤る（§5.1）。よって `geometry.umeyama(handedness=-1)` で `det(Q) = -1` を強制し、`det(Q)<0` のとき `scale = -s, R = -Q`（det +1）に分解して splatRoot へ渡す。

### 2.5 Transform への適用

- `root.localScale = (s, s, s)`（**`s < 0` を許容する**）、`root.rotation = R`、`root.position = t`。
- 親に非単位スケールがあると `lossyScale` が崩れるため、**ルートの親は単位 SRT** であること。
- **スプラットデータ・`GaussianSplatAsset` は書き換えない。** 位置合わせの結果は Transform だけで表す。
- 実装は `AlignmentManager.ApplySim3`。

---

## 3. 通信プロトコル（UDP・ベストエフォート）

13 バイトヘッダ ＋ ペイロード:

```
[ 'V','P', type(1B), reqId(4B LE), idx(2B LE), total(2B LE), len(2B LE) ] + payload[len]
```

| type | 方向 | ポート | 内容 |
|---|---|---|---|
| 1 META | Quest→PC | 50000 | UTF-8 JSON。`w,h`（送信画像）/ `sw,sh`（K の基準＝センサ解像度）/ `fx,fy,cx,cy` / `px,py,pz`（カメラ位置）/ `qx,qy,qz,qw`（回転・Unity 左手系）/ `ts` / `n`（IMG チャンク数）/ `len`（JPEG 総バイト数） |
| 2 IMG | Quest→PC | 50000 | JPEG チャンク（既定 1100B）。`idx`/`total` で再構成 |
| 3 RESULT | PC→Quest | 50002 | Sim(3) の JSON（scale/R/t・inliers・rmse・採否理由） |
| 4 RESET | Quest→PC | 50000 | 試行リセット。payload は `{trial,label,mode}`。§3.2 |
| 5 PING | Quest→PC | 50000 | 2 秒ごと。`reqId` に seq |
| 6 PONG | PC→Quest | 50002 | `{seq,session,kf,cv_alive,cv_age,cv_scene,cv_window,ev_ack,…}`。§3.3 |
| 7 EVENT | Quest→PC | 50000 | 実験ログ 1 行（UTF-8 JSON）。`reqId` に通し番号 seq。§9.3 |

- **ビーコン（別系統）**: PC が 1 秒ごとに `b"VPTSRV1" + uint16(データポート, LE)` を **50001** へブロードキャスト。Quest は**受信パケットの送信元 IP** を PC の IP として自動採用し、PlayerPrefs に保存する。
- **送るのは特徴ではなく縮小 JPEG**（長辺 512・品質 60）。CV はすべて PC 側で行う。
- 取りこぼしたキーフレームは**破棄して次のキャプチャを待つ**（NACK 再送は実装しない。キーフレームは繰り返し取れるので、再送より単純さを採った）。
- AP がブロードキャストを遮断する環境ではビーコンが届かない → Quest の `UDPManager.manualIPAddress` に PC の IPv4 を固定する（ユニキャストは通る）。ビーコン受信には `CHANGE_WIFI_MULTICAST_STATE`（＋`ACCESS_WIFI_STATE`）権限が要る。
- LAN 内・非暗号化（研究用途）。

### 3.1 セッションの契約

**`run.py` の 1 回の起動 ＝ 1 セッション。** 起動ごとに `received/session_<時刻>/` を作り、`cv.json` を書く。WSL2 の `localize.watch` は最新セッションを自動追従する。

Sim(3) は**そのセッションの Quest ワールド座標系**を基準とする。別セッションのキーフレームが混ざると座標系が食い違うので、セッションを跨いで蓄積しない。跨セッションの持ち越しは空間アンカー（§7）が担う。

### 3.2 type=4 RESET（試行の独立性）

評価実験で**アプリと `run.py` を再起動せずに独立した試行を繰り返す**ための機構。受け取ると `run.py` は新しいセッションフォルダを切り、`cv.json` と `trial.json` を書く。`localize.watch` は最新セッション追従なので、蓄積していた対応点を自ら捨てる。

UDP の取りこぼし対策で Quest は**同じ内容を 3 回**送る。ヘッダの `reqId` を resetId として使い、PC 側は同じ番号を無視する（重複でセッションが複数切られるのを防ぐ）。

Quest 側も同時に捨てる必要がある（`AlignmentManager.ResetForNewTrial`）。片方だけでは試行が独立しない（§9.2）。

### 3.3 PING/PONG（疎通と CV の生存）

**「送信先 IP を知っている」ことと「サーバが動いている」ことは別物。** IP は PlayerPrefs の学習値や手動設定で埋まるため、`run.py` が起動していなくても `HasTarget` は true になる。疎通判定には**必ず `UDPManager.ServerAlive`（PONG の鮮度）を使う。**

PONG の `cv_alive` は、WSL2 の `localize.watch` が 2 秒ごとに書く `cv_heartbeat.json` の鮮度（既定 8 秒で失効）。**`run.py` だけ動いて CV が未起動**という失敗モード（キーフレームは保存されるのに Sim(3) が永遠に返らない）を、試行を始める前に検出するためにある。

C# と Python で鍵名・type 定数が食い違わないことは `tests/test_liveness.py` が機械的に検査する。

---

## 4. CV パイプライン

### 4.1 オフライン（シーンごとに 1 回・WSL2/GPU）

`vptserver/cv/hloc_db.py`。入力は `scene_data/<name>/`（`sparse/0` ＋ `images/`）。

1. DB 画像に **SuperPoint** を抽出
2. 既存 COLMAP 姿勢の共visibility から画像ペアを作り **LightGlue** でマッチ
3. **既存姿勢を再利用して再三角測量** → `.ply` と同座標系の SfM モデル
4. **NetVLAD** の大域記述子を作る

出力 `scene_data/<name>/hloc/`。使うのは **undistort 後の `sparse/0`（PINHOLE）＋ `images/`** で、`distorted/` 側（OPENCV 歪み・SIFT）ではない。

レガシー COLMAP モデル（`rigs`/`frames` を持たない古い形式）は `migrate_model.py` で新形式へ移行してから使う（未移行だと `.rigs` アクセスで落ちる）。

### 4.2 オンライン（キーフレームごと）

`vptserver/cv/localize.py`。

1. 受信 JPEG → SuperPoint
2. NetVLAD で DB 画像を検索
3. LightGlue でマッチ → 2D-3D 対応
4. `localize_sfm`（PnP+RANSAC）→ `T_{c←cam}`、カメラ中心 `C_c`

各キーフレームの結果は `kf_localized.jsonl` に保存する。**この保存があるので、評価の曲線は hloc を再実行せずにオフラインで引き直せる**（§9.4）。

### 4.3 集約（Sim(3)）

`N` 枚（`cv.json` の `every`、既定 2）ごとに、成功したキーフレームの `(C_c, C_w)` の組から Umeyama＋RANSAC で Sim(3) を解く（`geometry.solve_sim3_ransac`、`handedness=-1` 固定）。

- **Sim(3) はカメラ中心だけで解く。** 中心は世界座標上の点で、カメラ軸の向き規約に依存しないため。
- `min`（既定 **5**）未満では解かない。`solve_sim3_ransac` は **n<5 で RANSAC を回さない**（全点で 1 回当てはめる）ので、4 以下にすると外れ値除去が一切効かない。
- `window`（既定 24、0=無制限）で直近 N 件だけを使う。
- 結果は `session/result_sim3.json` に書き、`run.py` が検知して type=3 で返送する。

---

## 5. 受理条件（残差では正しさを判定できない）

**Sim(3) の残差が小さいことは、解が正しいことを意味しない。** 実機で「採用 1 件で確定したが位置がまるで違う」が起きた原因はこれで、閾値の緩さではなく**退化**だった。検証は `tests/test_alignment_gates.py`。

### 5.1 2 種類の退化

| 退化 | 何が起きるか | 対策 |
|---|---|---|
| **共面配置＋反射の自由推定** | 3 点は必ず同一平面上に乗る。反射を許すと「その平面に関する鏡映を掛けた解」が**完全に同じ残差**を与え、2 解を区別できない。**頭の高さを変えずに部屋を歩くとカメラ中心はほぼ共面**なので、枚数を増やしても起きる | **反射を負に固定**（§2.4） |
| **共線配置** | 一直線に歩くと、その直線まわりの回転が拘束されない。反射を固定しても直らない | **配置ゲート**（§5.2） |

数値検証: 共面 6 点で自由推定は 47%、3 点では 68% が誤り。反射固定で 0%。共線配置は反射を固定しても 100% 誤るが、配置ゲートが棄却する。

### 5.2 PC 側ゲート（`vptserver/cv/gates.py`）

| キー | 既定 | 意味 |
|---|---|---|
| `min_inliers` | 30 | PnP インライアがこれ未満のキーフレームは蓄積しない |
| `min_inlier_ratio` | 0.40 | 解のインライア率（inliers/n） |
| `max_rmse` | 0.08 m | world 残差 RMSE。部屋規模で 24 点使うと 5cm を超えうる |
| `scale_range` | [0.5, 2.0] | \|s\| の緩い妥当域（保険） |
| `expected_scale` | None（無効） | §5.4 |
| `scale_tol` | 0.10 | `expected_scale` からの許容相対誤差 |
| `min_baseline_m` | **1.50 m** | カメラ中心の最大間隔（その場で首を振っただけを弾く） |
| `min_second_extent_m` | **0.40 m** | 第 2 主成分方向の広がり（一直線に歩いただけを弾く） |
| `handedness` | -1 | 反射固定。0 にすると自由推定（通常は使わない） |

**閾値を締めても精度は上がらず、ロックしなくなるだけ**（1.0m/0.8m にしたら 9m 大きく回る歩き方しか通らず 60 秒以内にロックしなくなった。逆に 0.4m/0.15m まで緩めると直線歩行が通り 57% が誤る）。精度は「どれだけ部屋を歩いたか」でほぼ決まるので、**ゲートの役割は「足りていないときに確定させない」ことだけ**で、精度の担保は歩き方の指示が行う。

| 歩き方 | ロック率 | 誤差 中央値 | 誤差 90% |
|---|---|---|---|
| その場で首を振る（0.5m）／直線 3m ／ゆるい弧 3m | 0% | — | — |
| 部屋を横切る（6m・弧） | 92% | 15.6 cm | 31.9 cm |
| **部屋を回る（9m・大きな弧）** | **100%** | **9.9 cm** | 25.5 cm |

### 5.3 Quest 側ゲートと確定条件（`AlignmentManager`）

受信した Sim(3) を同種のゲート（`minInlierRatio` 0.5 / `maxRmseM` 0.05 / `|s|∈[0.5,2.0]`）＋**改悪防止**（前回採用より `rmseWorsenFactor`=1.5 倍以上悪化した解は棄却）で判定したうえで、**独立に解かれた解が連続して一致した回数が `minConsecutiveAgreements`（既定 2）に達して初めて「確定」**とする。

- 一致の判定は、2 つの解でスプラットの**境界ボックス 8 隅が動く距離**（`agreeMaxSceneShiftM`、既定 **0.15 m**）。位置・回転・スケールに別々の閾値を置くより「見た目がどれだけ違うか」に直結する。境界ボックスの隅は原点から 6m 以上離れるので、8cm まで下げると実機の雑音下ではほぼ成立せず、ロックしなくなる。
- 未確定の間も姿勢は適用する（収束の様子が見える）が、`Locked` にせず永続化もしない。
- 収束時刻として記録するのは**確定した瞬間**（`OnAutoAligned`）。`OnLocked` は手動 Lock やアンカー復元でも鳴るので収束点には使えない。

> **なぜ連続一致か**: 悪条件の解はキーフレームが増えると跳ねる。**独立に解かれた 2 つが同じ答えに落ち着くことは、残差が小さいことよりずっと強い証拠**で、想定していない失敗モードにも効く。

**時間予算**: 確定に 20 枚程度必要。`KeyframeUploader.autoMinIntervalS` が 2.0 だと送信だけで 40 秒かかり 60 秒の試行に余裕が無いので **1.0** にした。`cv.json` の `every` も 3→**2** にして一致判定の機会を増やした。

### 5.4 `expected_scale` は評価実験では使わない

COLMAP の任意単位と実メートルの比は、その再構成に固有の固定値（このシーンで 0.96）で、`--expected-scale` を渡せば棄却ゲートにできる。**しかし正しさには不要**（反射固定＋配置ゲート＋連続一致で誤り率は既に 0%）。シーン固有の既知値を推定に注入すると「メトリックスケールを解決する」という主張が弱くなるので、既定は無効。**推定値が独立に実測した値とどれだけ一致したかを結果として報告する**ほうが強い。デバッグで棄却理由を明示したいときだけ使う。

---

## 6. 姿勢の適用と入力

### 6.1 `AlignmentManager` が姿勢の唯一の真実

splatRoot の SRT を所有し、次の経路から更新される。

| 経路 | 契機 |
|---|---|
| 手動操作 | コントローラ（`OperationGuide.md` §5-1） |
| 半自動 | 水平化＋床スナップ（左スティック押込） |
| 自動 | `OnSim3Received` → ゲート通過 → `ApplySim3` |
| 復元 | `AnchorPersistence` の起動時復元 |

差分が小さいとき（`smoothMaxPosM` 0.3m / `smoothMaxDeg` 10° 以内）は `smoothDurationS`（0.5s）で補間して適用し、ジャンプを避ける。大きいときは即時。

### 6.2 入力の受付範囲（`PoseControlMode`）

`ExperimentController.ApplyInputMode` が毎フレーム選ぶ。

| モード | 通す入力 | 局面 |
|---|---|---|
| `Full` | すべて | 通常運用（実験モード OFF） |
| `SticksOnly` | スティックのみ（トリガーのスケールは含まない） | **Real CP 測定中・Virtual CP 編集中** |
| `Locked` | X（表示トグル）のみ | 試行中 |

`SticksOnly` を開けるのは、位置合わせ前の CP の球が壁の向こうや天井裏に出て近寄れないため。**シーンごと引き寄せても測定値には影響しない**（測量が測るのは実物に先端を当てたワールド座標だけ、編集が書くのは splatRoot ローカル座標だけで、どちらも splatRoot の姿勢に依らない）。トリガーを除くのは、両局面でトリガーが「仮置き／取り消し」に取られているため（開けると 1 点触るたびにスプラットが伸縮する）。

- **入力の左右は明示指定**（`RawAxis*/RawButton` の L/R）。利き手設定で入れ替わる `Primary/Secondary` は使わない。
- 入力の割当表は `OperationGuide.md` §5-1（唯一の真実）。

### 6.3 スプラットの表示は「いま何を見る作業か」で決まる

`AlignmentManager.startHidden`（既定 true）。以後は自動で切り替わる: `Edit Virtual CP` 開始=**表示** / `Set Real CP` 開始=**非表示** / 試行の収集開始=**非表示** / 姿勢が適用された時点=**表示**。X でいつでも上書きできる。

Real CP の測定中に非表示にするのは見た目の問題ではなく**測定精度**の問題で、位置合わせ前のスプラットが先端を当てたい実物そのものを覆い隠すため。

`GaussianSplatController` のグローバル不透明度の初期化は **`Start` ではなく `Awake`** で行う（`Start` どうしの実行順は不定で、非表示にした直後に 1 で上書きされうる）。

---

## 7. 永続化（空間アンカー）

`AnchorPersistence`。Lock または自動アライン確定時に:

- 実部屋へ**空間アンカーを 2 個**（`anchor.count`、間隔 `anchor.sepM` 1.5m）保存し、**各アンカー基準の相対姿勢**として保存する。ワールド絶対で焼き込まない（アンカーは再ローカライズで自身が動く）。
- 保存先 `persistentDataPath/vptalign_<modelId>.json`。`modelId` は `GaussianSplatController` の欄（空なら 3DGS アセット名を自動採用）。モデルごとに別ファイルなので複数モデルが干渉しない。
- 復元は複数アンカーの整合を取り、`anchor.restoreOutlierM`（0.05m）を超えて外れるものは破棄。**片方のアンカーが再発見できなくても復元できる。**
- 保存はスロットルする（`save.posM` 0.02m / `save.rotDeg` 1° / `save.scaleFrac` 0.005 / `save.intervalS` 30s のいずれも下回る変化では書かない）。
- やり直したいときは `AnchorPersistence.ClearSaved()`。

**評価実験では自動的に無効化する**（§9.2）。

---

## 8. トラッキング原点の監視（`TrackingOriginWatcher`）

Sim(3) は「Quest ワールドは固定」を前提にする。しかし原点は次の契機で**鉛直軸まわりの回転＋水平移動**の形で飛ぶ。

- Meta ボタン長押し（再センタリング）
- ヘッドセットの着脱・スリープ復帰
- トラッキングロストからの復帰・空間の再ローカライズ

**飛んでも rmse もインライア率も悪化しない。** 実機で「3 試行とも同じように間違う」が起きたときの原因はこれで、試行間の解の差が **スケール 1.000・回転軸 (0,1,0)・鉛直移動 0** の相似変換になっていた（37.7°・78.1°）。推定誤差ならこの形にはならない — 誤差は最も弱く拘束される傾き方向に出るのに対し、カメラ中心がほぼ同高度に並ぶ本件ではヨーと水平位置こそ最もよく決まるパラメータだから。

検出は 3 系統: `OVRPlugin.GetLocalTrackingSpaceRecenterCount` / `OVRManager.TrackingLost` / 頭部姿勢の不連続。3 つめは**速度で判定する**（距離で見るとフレーム落ちで誤検出する）。

検出時の扱い:

| 層 | 実装 | 動作 |
|---|---|---|
| Quest | `ExperimentController` | 収集中なら**試行を自動破棄**（試行番号は消費せずラベルだけ消費。ラベルを使い回すと PC の `session_*/trial.json` と 1 対 1 対応しなくなる） |
| Quest | `ControlPointMeasure.InvalidateSurvey` | **測量を無効化**（Real CP はワールド座標なので、原点が回ると過去の座標系の値になる） |
| PC | `localize` の終盤残差警告 / `analysis/check_tracking.py` | 試行内の残差急増（実機で 0.4→0.8→1.9→2.8 m）と試行間の純ヨー食い違いを検出 |

**運用ではヘッドセットを外さない。**

---

## 9. 評価実験の実装

`Assets/Scripts/Experiment/`。実機でやることは「一定時間歩き回る」だけ（`mode="collect"` の 1 種類）。かつて E1（オーバーレイ精度）と E2（コールドスタート収束）を別モードで回していたが、**1 回の収集データから曲線全体をオフラインで再構成できる**ので統合した。

### 9.1 検証点（CP）の 2 つの側

`CP_01` … `CP_06` は**実在する 1 つの物理的特徴**（机の角など）を指し、記録する座標が 2 つある。

| 呼び方 | 何の座標か | どこで決めるか | ログ名 | 画面表示 |
|---|---|---|---|---|
| **Virtual CP** | 3DGS モデル座標（splatRoot ローカル） | Editor の Control Points ウィンドウ、または VR 内の `ControlPointEdit` | `cp_model_edit` | `VCP_01` |
| **Real CP** | 同じ特徴の**実物**のワールド座標 | VR 内の `ControlPointMeasure`（校正済みコントローラ先端で実物に触る） | `cp_survey` | `RCP_01` |

誤差は `err = |T(Virtual CP) − Real CP|`。**どちらも Sim(3) の推定には使っていない**ので評価として成立する。

- **呼び分けは HUD・ワールドのラベル・文書の文言だけ。** クラス名（`ControlPointMeasure`/`ControlPointEdit`）・プロパティ名（`IsSurveyed`/`SurveyReady`）・UDP イベント名・`controlpoints_<modelId>.json` の鍵は `CP_01` のまま（過去ログと解析スクリプトの互換を優先）。
- 個々の点を画面に出すときは内部名 `CP_01` を素で書かない。同じ点の 2 つの側がすぐ隣に並ぶので、**見分けを色だけに載せない**（色の印象はパススルーの背景と照明で変わる）。
- 写真測量の用語では、推定に使わず検証にだけ使うこの 6 点は control point ではなく **check point（検証点）**。呼称は CP のまま通し、論文でその旨を明記する。

### 9.2 試行を独立にするための 4 点

| 落とし穴 | 対策 |
|---|---|
| Quest の `_hasAcceptedSim3`（改悪防止ゲート）と PC の対応点蓄積が前試行を引きずる | `AlignmentManager.ResetForNewTrial` と UDP type=4 RESET を**対で**送る。**測量はここで捨てない**（試行を跨いで使う値） |
| アンカー復元で起動直後に `OnLocked` が鳴り「即ロック成功」と誤記録される | `ExperimentController.forceExperimentMode` が永続化を自動 OFF |
| `OnLocked` は手動 Lock でも鳴る | 収束点には `OnAutoAligned` を使う（§5.3） |
| ワールド原点の移動 | §8 |

### 9.3 ログと PC への転送（type=7 EVENT）

端末側は `persistentDataPath/experiments/exp_<時刻>.jsonl`（1 行 1 イベント、全行に `trial`/`mode`。試行外は `trial=0`）。

**解析に必要な行だけ**（`cp_survey*` / `cp_model_*` / `trial_start` / `trial_end` / `tracking_origin_changed` / `tip_calib_*` / `auto_aligned` 等。`fps`/`kf_sent`/`sim3_gate` は送らない）を UDP で PC へ送る。これにより **`adb pull` なしで PC 側だけで解析が完結する**（端末内 JSONL は冗長化として残す）。

- **ACK 付き**: PONG の `ev_ack`（連続受信済み seq）を見て Quest が未達分だけを再送する（`UDPManager._outbox`）。CP の実測値は実験を通して 6 点しかなく、後から測り直すと座標系が変わってやり直しが効かないので、ベストエフォートでは足りない。
- **保存先は 2 か所**: `received/quest_events.jsonl`（**正本**・到着順に全部）と `received/session_*/quest_events.jsonl`（複製）。複製の振り分けは**到着時刻ではなく行に載っている試行ラベル**で行う（遅れて届いた再送も正しい試行に入る）。正本を別に持つのは、試行に属さない行（`session_start`、**CP の測量**）やラベル未確定のうちに届いた行を落とさないため。
- 重複除去は `(run, seq)`。`run` は Quest 側のログファイル名（＝アプリ起動 ID）。これが無いと `trial` が起動ごとに 1 から振り直されて衝突する。
- `cp_model_point` ＋ `cp_model_edit_done(phase=end)` を受けると `run.py` が `received/controlpoints_<modelId>.json` を **`ControlPointStore` と同じ形**で書き出す（Editor の Control Points ウィンドウがそのまま取り込める）。ファイル名の規則は Unity の `ControlPointStore.FileName` と `run.cp_store_filename` の**両方**にあるので、変えるときは対で直す。
- 検証は `tests/test_events.py`。

主なイベント型は `OperationGuide.md` §9-5 の表。

### 9.4 解析（`server/analysis/`）

| スクリプト | 役割 |
|---|---|
| `check_tracking.py` | **曲線を引く前に必ず通す。** 原点が動いた試行の検出 |
| `experiment_report.py` | **主結果。** 保存済みキーフレームから試行ごとに姿勢を解き直し、測量した CP と突き合わせる |
| `accuracy_vs_time.py` | 1 試行ぶんの曲線（`experiment_report` が内部で使う） |
| `analyze_experiments.py` | 測量そのものの健全性・先端校正・収束の要約（**オーバーレイ精度は出さない**） |
| `paper_figure.py` | 上記 CSV → 論文用のベクタ図 |

**曲線をオフラインで引ける根拠**: `localize.watch` が各キーフレームのローカライズ結果を `kf_localized.jsonl` に保存するので、「最初の t 秒分だけで Sim(3) を解き直す」を t を変えて繰り返せばよく、hloc の再実行が要らない。

### 9.5 測定を無効にする落とし穴（対策済み・外さないこと）

1. **先端オフセット**: `controllerAnchor` は本体内部の追跡原点で、物に当てる物理的先端ではない。差は数 cm あり測定対象と同オーダー。`ControllerTipCalibration` のピボット校正で補正し、未校正なら測量を拒否する。校正は**残差ではなく向きの広がり**でゲートする（広がり不足だと残差 0.5mm なのにオフセットは 2mm ずれる、と数値検証済み）。
2. **校正値の持ち越し**: 保存値は `TipState.Restored` として読み込むだけで、既定では測量に使わせない（`requireCalibrationEachSession=true`）。保存値が今の物理構成（別コントローラ・治具の付け外し）に対応している保証がなく、ずれても事後のログから検出できないため。各測定に `tip_source`（Calibrated/Restored/Manual）が残り、解析スクリプトが流用件数を警告する。
3. **同じ場所で 2 点記録する事故**: 前の点から動いていなければ記録を拒否する。**誤差ではなく移動量で**判定する（誤差で弾くと結果にバイアスがかかる）。
4. **アライン済みで Virtual CP を編集すると誤差が 0 側にバイアスする**: オーバーレイが実物に重なって見えている状態で動かすと、無意識に「実物の位置」へ寄せてしまう（答えを見ながら定規を合わせるのと同じ）。`ControlPointEdit` は `HasAcceptedSim3` でも `Locked` でもない準備段階だけに限り、編集中にアラインが通ったらその場で打ち切る。全編集を `aligned` フラグ付きでログし、`analyze_experiments.py` が該当セッションを警告する。
5. **編集は測量より前に**: PC 側の解析は Virtual CP を `cp_survey` イベントに載ったスナップショットから読むので、測量後に動かすとシーンと PC の値が黙って食い違う。だから編集すると測量を無効化する。
6. **描画の失敗が状態遷移を止める**（実機で発生）: `new Material(Shader.Find("Unlit/Color"))` はビルド時の参照解析から見えずシェーダが stripping され、実機でだけ `Shader.Find` が null を返し **`ArgumentNullException`** を投げた（黙ってピンクを作るのではない）。その呼び出しが「次の点へ進む」処理より**前**にあったため、記録のたびに `Update()` ごと抜け、**測量が CP_01 から永久に進まなかった**。Editor では全ビルトインシェーダが存在するので再現しない。
   → 対策 2 層: `Project Settings > Graphics > Always Included Shaders` に `Unlit/Color` を登録／**`CpGizmos.Draw()` で描画を状態遷移から切り離す**（状態を進めてから描く・例外は 1 回だけ LogError して握る）。**ギズモを触るコードを足すときはこの順序と `Draw()` を守ること。**
7. **押して無反応にしない**: ブロック理由は `RejectReason` に載せて HUD（`FirstUnmetReason`）へ出し、拒否の触覚を返す。実機では触覚も表示も無いと「ボタンが効かない」と区別が付かない。

---

## 10. パラメータ一覧（単一の真実）

**シリアライズ値（シーンの Inspector）はコードの既定値より優先される。** 変えるときはシーンの `Managers` も合わせて直すこと。

### 10.1 PC 側（`cv.json` / `gates.py`）

| キー | 既定 | 単位 | 使用 |
|---|---|---|---|
| `scene` | 自動検出 | — | 再ローカライズ DB |
| `every` | 2 | 枚 | この枚数ごとに Sim(3) を解き直す |
| `min` | 5 | 枚 | これ未満では解かない（RANSAC が回る下限） |
| `window` | 24 | 枚 | 直近 N 件で解く（0=無制限） |
| `min_inliers` | 30 | 個 | KF 採否 |
| `min_inlier_ratio` | 0.40 | 比 | 解の公開 |
| `max_rmse` | 0.08 | m | 解の公開 |
| `scale_range` | [0.5, 2.0] | — | \|s\| の妥当域 |
| `expected_scale` / `scale_tol` | None / 0.10 | — / 比 | §5.4（既定は無効） |
| `min_baseline_m` | 1.50 | m | 配置ゲート |
| `min_second_extent_m` | 0.40 | m | 配置ゲート |
| `handedness` | -1 | — | 反射固定（0=自由推定） |
| `CV_STALE_S`（run.py） | 8 | s | CV ハートビートの失効 |

### 10.2 Quest 側（`AlignmentManager` / `UDPManager` / `KeyframeUploader`）

| キー | 既定 | 単位 | 使用 |
|---|---|---|---|
| `minInlierRatio` / `maxRmseM` | 0.5 / 0.05 | 比 / m | 受理ゲート |
| `minScaleAbs` / `maxScaleAbs` | 0.5 / 2.0 | — | \|s\| の妥当域 |
| `rmseWorsenFactor` | 1.5 | 倍 | 改悪防止 |
| `minConsecutiveAgreements` | 2 | 回 | 確定に要する連続一致 |
| `agreeMaxSceneShiftM` | 0.15 | m | 一致の判定（bbox 8 隅の移動量） |
| `smoothMaxPosM` / `smoothMaxDeg` | 0.3 / 10 | m / deg | 補間する差の上限 |
| `smoothDurationS` | 0.5 | s | 補間時間（0=即時） |
| `startHidden` | true | — | 起動時はスプラット非表示 |
| `save.posM` / `save.rotDeg` | 0.02 / 1 | m / deg | 保存抑制しきい |
| `save.scaleFrac` / `save.intervalS` | 0.005 / 30 | 比 / s | 保存抑制 |
| `anchor.count` / `anchor.sepM` | 2 / 1.5 | 個 / m | 永続化の冗長化 |
| `anchor.restoreOutlierM` | 0.05 | m | 復元時の外れ判定 |
| `autoMinIntervalS` | **1.0** | s | 自動送信の最短間隔（§5.3 の時間予算） |
| `pingIntervalS` / `serverTimeoutS` | 2 / 6 | s | 疎通確認 |

### 10.3 評価実験

| キー | 既定 | 根拠 |
|---|---|---|
| `ExperimentController.collectSeconds` | 60 s | 1 試行の収集時間。曲線の右端はここで決まる |
| `ExperimentController.plannedTrials` | 10 | 試行数（HUD の進捗表示用。強制ではない） |
| `ExperimentController.resetSettleS` | 1.0 s | PC が新セッションフォルダを作り終えるまでの待ち |
| `ExperimentController.aLongPressS` | 1.0 s | A 短押し=確定・長押し=`Edit Virtual CP`。**離す前に発火**させ触覚で伝える（1 秒は目で測れない） |
| `ControllerTipCalibration.minSamples` | 24 | 未知数 6 に対する十分な冗長性 |
| `ControllerTipCalibration.minSpreadDeg` | 45° | 向きの広がり不足だと残差が小さいまま解がずれる |
| `ControllerTipCalibration.minSampleAngleDeg` | 6° | 静止中の同一姿勢の重複を避ける |
| `ControllerTipCalibration.requireCalibrationEachSession` | true | 保存値が今の物理構成に対応している保証がない |
| `ControllerTipCalibration.driftWarnMm` | 5 mm | 前回値との差の警告（コントローラ・治具の取り違え検出） |
| `ControlPointMeasure.allowUncalibrated` | false | 未校正で測った Real CP は系統誤差込みで使えない |
| `ControlPointMeasure.minTipSeparationM` | 0.10 m | 同じ場所で 2 点記録する事故を弾く |
| ↑ の緩和 | `max(0.03, min(0.10, 0.5 × 実距離))` | CP どうしが近いと 10cm では正しい記録まで弾く（CP_02–CP_03 は約 15cm）。**m に直せるときだけ**緩める（`TryGetMetricScale`）。**位置合わせ前の splatRoot のスケールは使わない** — 手動合わせの途中値で m との比を表しておらず、小さいとしきい値が 1cm 未満に落ちてガードが黙って無効になる。下限 3cm は「動かさず 6 点連打（全体の広がり 6cm）」を捕まえる床 |
| `ControlPointMeasure.markerRadius` | 0.025 m | 球の半径（直径 5cm。実測点の橙はこの 1.2 倍）。`CpGizmos.RefDistM` 以内での実寸 |
| `ControlPointMeasure.labelHeightM` | 0.05 m | ラベルの文字高（同上）。球の真上・ヨーのみビルボード |
| `CpGizmos.RefDistM` | 1.0 m | **この距離までは実寸、以遠は距離に比例して拡大**し見かけの大きさを保つ。実寸固定だと 3m 先の球は 0.5°（10px 前後）でスプラットの背景に埋もれ、実機で「CP_01 だけ見えて CP_02〜06 が見えない」（描画はされているのに探せない）と報告された。1m 以内を実寸にするのは先端を当てる精度を落とさないため |
| `CpGizmos.MaxGrowth` / `CurrentBoost` | 6 / 1.6 | 拡大の上限倍率 / いま対象の点だけの追加倍率（＋縦の棒 `CPStem`） |
| `CpGizmos.ShaderCandidates` | `Unlit/Color` → `Universal Render Pipeline/Unlit` → `Sprites/Default` | **`Unlit/Color` は Always Included Shaders に登録済み**（§9.5-6）。全滅時は `LogError` で直し方を出す |
| `CpGizmos.VirtualLabel` / `RealLabel` | `VCP_01` / `RCP_01`（編集前のゴーストは `VCP_01 (old)`） | §9.1 |
| `ControlPointEdit.allowUncalibrated` | false | 未校正だと先端オフセットがそのまま Virtual CP に焼き込まれる |
| `ControlPointEdit.warnMoveM` | 1.0 m | これ以上動いたら誤タッチを疑って HUD で警告（記録は止めない） |
| `ControlPointEdit.writeDeviceJson` | true | 端末側にも書く（UDP が落ちたときの冗長化。**起動時に自動復元はしない**） |

### 10.4 HUD（`AlignmentHud`）

| 項目 | 確定値 | 根拠 |
|---|---|---|
| 文言 | **英語のみ** | 実機で読むのは HUD だけなので日本語と混ぜない（日本語はログと `[Tooltip]` に限る）。HUD へ出る文字列を返すプロパティ（`UDPManager.TargetDescription` 等）も英語 |
| `AddText` の折り返し | `Wrap` ＋ best fit（下限＝指定サイズ×0.60（1 行）/ 0.70（複数行）、最小 10px） | `HorizontalWrapMode.Overflow` は**クリップしない**ので長い行が枠外へ出る。10px は 1px=1mm・約 1.2m 先で読める下限 |
| 準備チェック行の高さ | 40 px（2 行）／文字 15px | 常態は 319px で 1 行だが、Tracking・Upload・Virtual CP edited が加わると 650px（欄幅 476px の 1.4 倍）になる。**一番読ませたい局面**なので縮めず折り返す |
| パネル高 `h` | 688 px（実験用）/ 510 px（簡易） | 上の 2 行化で 670 → 688。行を足したらここも見直す |
| 更新レート | 10 Hz | 毎フレーム文字列を作ると GC で 72fps を削る |
| 追従 | 視線から 24° 外れたときだけ滑らかに追従。**手を近づけている間は動かない** | Poke するボタンが逃げないように |

---

## 11. ビルド・権限・プロジェクト設定

- **ビルド**: Android / ARM64 / IL2CPP / **Vulkan**（aras-p レンダラは D3D12/Metal/Vulkan のみ。OpenGL ES では描画できない）。
- **URP**: Render Graph **Compatibility Mode Off**。**Render Mode = Multi-Pass**（aras-p v1.1.1 は Single Pass Instanced 非対応でスプラットが見えない）。`GaussianSplatURPFeature` を **PC・Mobile 両方の URP Renderer** に付与する（実機は Mobile 品質 Renderer を使うため、片方だけだと実機で見えない）。
- **OpenXR/Meta feature**: Passthrough, Anchors, Scene/MRUK, **Camera（PCA）**。Meta XR SDK（OVR）リグ＋Building Blocks 構成。
- **カメラ取得**: `PassthroughCameraAccess`（Building Block「Passthrough Camera Access」で配置）。旧 `WebCamTextureManager`/`PassthroughCameraUtils` は使わない。`OculusProjectConfig` で `isPassthroughCameraAccessEnabled=true`、実行時に `OVRPermissionsRequester` で権限要求。
- **権限**: `horizonos.permission.HEADSET_CAMERA` のみ（新 API では `android.permission.CAMERA` は不要）。ビーコン受信用に `CHANGE_WIFI_MULTICAST_STATE` ＋ `ACCESS_WIFI_STATE`。
- **バージョン要件**: Horizon OS **v81+**・MRUK **v81+**（`PassthroughCameraAccess` の要件。PCA 自体は v74+ だが新コンポーネントは v81+）。XR Simulator は PCA 非対応 → 実機か Horizon Link v2.1+ で確認。
- **解像度**: `PassthroughCameraSource` は 1280×1280 を要求するが、環境により 1280×960 が返る（`CurrentResolution != Intrinsics.SensorResolution` になりうる）。K の算出が解像度差を吸収する（§2.3）。固定値に依存せず `GetSupportedResolutions()` から選ぶ。
- **Always Included Shaders**: `Unlit/Color` を登録済み。**コードで `new Material(Shader.Find(...))` するシェーダを増やすときは、必ずこのリストにも足す**（§9.5-6）。
- **スプラット数**: `.ply` は GPU バッファ 128MB 制限（≒32B/スプラット → 約 419 万が上限）。Multi-Pass は描画コストがほぼ 2 倍なので予算は控えめに。採用シーンは約 15 万スプラット。

---

## 12. テスト

### 12.1 純粋計算（PC 側・numpy だけで通る）

```bash
cd server
python -m tests.test_geometry          # Umeyama / Sim(3) / クォータニオン（合成データ）
python -m tests.test_gates             # 品質ゲートの純粋関数
python -m tests.test_alignment_gates   # 退化（共面・共線）と反射固定の効果
python -m tests.test_liveness          # PING/PONG・CV ハートビート。C# と Python の鍵名/type 定数の一致も検査
python -m tests.test_events            # type=7 EVENT の振り分け・重複除去・CP JSON 書き出し
```

`test_liveness.py` と `test_events.py` は **C# のソースを読んで定数名・鍵名の一致を機械的に検査する**。プロトコルを変えたときに片側だけ直す事故を防ぐため。

### 12.2 hloc/pycolmap を使う部分

**WSL2 実機でしか確認できない。** `hloc_db` の構築 → 自己ローカライズ（DB 画像を自分自身に照合）→ 実キーフレームからの Sim(3) で検証する（自己テストで誤差/シーン対角 ~1e-4、実キーフレームで残差 ~1cm を確認済み）。

### 12.3 実機

`CLAUDE.md` §0 の検証ゲート（両眼描画・ワールドへの貼り付き・ソート安定・フレームレート）と、`OperationGuide.md` §9-0 の実験前チェック。

---

## 13. 既知の限界

- **フル連続追従（毎フレームの微調整）は実装していない。** 現状は「アンカーで貼り付き＋定期的な再ローカライズ」の軽量版で運用している。
- **オンデバイス CV は無い。** PC（WSL2）が居ない環境では自動位置合わせは動かない（手動・半自動と、既存アンカーからの復元は動く）。
- **キーフレームの再送が無い。** 取りこぼしは破棄して次のキャプチャを待つ。
- **UDP は非暗号化**（LAN 内・研究用途）。
- **精度は歩き方に依存する**（§5.2）。ゲートは不十分な歩行を確定させないだけで、精度そのものは上げられない。
