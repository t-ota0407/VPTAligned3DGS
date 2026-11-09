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

**`localize` は `run.py` が起動・終了する**（`vptserver/cv_launcher.py`。`--no-cv` で従来どおり手動）。2 つのターミナルを手で起動していた頃は、`run.py --out` と `localize --watch` の食い違いで「キーフレームは届くのに位置合わせが永遠に返らない」が起きた。契約:

| 項目 | 実装 |
|---|---|
| 監視先 | `--watch` は `run.py` の `--out` から決める（`wslpath` で変換。空白・日本語のパスもクォートして渡す） |
| ディストリ | `--wsl-distro` → `launch_config.json`（setup が書く）→ CV の venv があるものを探す。**既定のディストリに CV 環境があるとは限らない**（作者の PC でも既定は Ubuntu-20.04、CV は Ubuntu-24.04） |
| 終了の連動 | `localize` の標準入力をパイプでつなぎ、`--exit-on-stdin-eof` で EOF を見て終わる（`cv/lifecycle.py`）。`run.py` が強制終了されても残らない。正常終了ではパイプを閉じ、8 s で終わらなければ Linux 側の PID を kill。**EOF の見張りは記述子を `os.read` で直接読み、`sys.stdin` 経由で待たない** — `sys.stdin.buffer.read()` で待つとそのロックを握り続け、hloc の DataLoader が fork した子が起動時の `sys.stdin.close()` で永遠に止まる（最初のキーフレームから先へ進まない。2026-11-08 に発生） |
| 再起動 | 落ちたら 5 s 後に起動し直す。起動 30 s 以内に 3 回続けて落ちたら設定の問題として諦め、理由を出す。venv が無い（終了コード 3）ならすぐ諦めてセットアップを案内する |
| 二重起動 | `<out>/cv_heartbeat.json`（ベースの生存信号）が 8 s 以内に更新されていれば、別の `localize` が処理中として起動しない。止まったら引き継ぐ |
| 生存の判定 | ハートビートの古さは**ファイルの更新時刻（Windows の時計）**で測る。中の `t` は WSL の時計で、WSL2 はスリープ復帰後に数十秒〜分ずれる |

**セットアップ**（`setup_windows.bat` → `vptserver/setup_windows.py` → `setup_wsl.sh`）は初めての Windows を前提にする。WSL 側の版は動作確認済みの組み合わせに固定する（Ubuntu 24.04 / Python 3.12 / torch 2.6.0+cu124 / hloc `c13273b` / LightGlue `eb42fee` / `constraints-cv.txt`）。モデルの重みはセットアップ時に取得する（`cv/check_env.py`。会場にインターネットが無くても動くように）。手順は `server/README.md`「セットアップ」。

### 1.1 ファイル配置（実体）

```
server/                                    ← PC 側
├─ setup_windows.bat / build_scene_db.bat  初回のセットアップ / シーン DB の構築（ダブルクリック）
├─ start_experiment.bat / start_demo.bat   起動（run.py ＋ CV の自動起動。保存先 received / received_demo）
├─ scripts/run_server.cmd                  start_*.bat の共通部分
├─ setup_wsl.sh / constraints-cv.txt       WSL2 の CV 環境の構築 / 動作確認済みの版
├─ launch_config.json                      この PC の WSL ディストリ・venv（setup が書く・.gitignore）
├─ run.py                                  受信・ビーコン・Sim(3) 返送・GUI ホスト・CV の起動
├─ requirements.txt / requirements-cv.txt  コア依存 / CV 依存
├─ scene_data/<name>/                      COLMAP 一式（sparse/0 + images/）
├─ received/session_<時刻>/                 セッション（KF・cv.json・result_sim3.json）
├─ analysis/                               評価実験の集計
├─ tests/                                  純粋計算・プロトコル整合のテスト（`python -m tests` で全部）
├─ tools/test_send.py                      疎通テスト用のダミー送信
└─ vptserver/
   ├─ dispatch / session / liveness / events / state  run.py の中身（パケットの振り分け・セッション・PONG・実験イベント・GUI の状態）
   ├─ scenes                               DB ができているシーンの判定と DB の設定名（Windows 側と WSL2 側で共用）
   ├─ protocol / reassembler / udp_receiver / keyframe / storage / beacon
   ├─ gui / gui_status / i18n             PC の GUI（描画 / 判定の純粋関数 / 英語・日本語の文言）
   ├─ cv_launcher                          WSL2 の localize の起動・監視・終了（§1）
   ├─ setup_windows                        初回のセットアップ（WSL・ファイアウォール・シーン DB）
   └─ cv/ colmap_model, geometry, gates, hloc_db, migrate_model, localize, heartbeat（CV の生存信号。別スレッド）,
          lifecycle（起動元が終わったら終わる）, check_env（環境の確認と重みの事前取得）,
          warm_hloc（モデルと DB を読み込んだまま hloc と同じ計算をする高速経路）, check_warm（その突き合わせ）

unity/projects/VPTAligned3DGS/Assets/
├─ GSTestScene.unity                       実験用シーン（Scene List の先頭＝起動時）
├─ DemoScene.unity                         デモ展示用シーン（同じ APK。実験の最初の画面から切替。§14）
├─ Scripts/
│  ├─ UDPManager             送信・ビーコン自動検出・結果受信・PING/PONG・イベント再送
│  ├─ VpPacket               パケット（13B ヘッダ）の組み立てと読み取り・type 定数（PC 側 protocol.py と一致）
│  ├─ KeyframeUploader       キーフレーム送信（左グリップ 短押し単発 / 長押し自動）
│  ├─ PassthroughCameraSource PCA ラップ（解像度・K 診断）
│  ├─ AlignmentManager       姿勢の真実。手動操作・Sim(3) 受理・補間適用・Lock・アンカー追従の受け口
│  ├─ AnchorPersistence      空間アンカーによる永続化（保存・再試行つき復元・多数決追従。§7）
│  ├─ AnchorMath             永続化の純粋計算（相対姿勢・多数決・アンカー配置。§7）
│  ├─ AppMode                実験 ⇄ デモのシーン切替・両スティック長押しの判定（§14.1）
│  ├─ TrackingOriginWatcher  トラッキング原点の移動検出（実験とデモで共用。§8）
│  ├─ GaussianSplatController 表示・不透明度・ModelId・モデルの境界ボックス
│  ├─ PressTracker / Haptics 物理ボタンの短押し・長押しの判定 / 振動の合図（実験・デモで共用）
│  ├─ AlignmentHud(.Experiment/.Demo/.Layout)  ヘッドセット内 HUD（partial クラス。実験 / 簡易 / デモ）
│  ├─ Demo/DemoController    デモ展示モードの進行（§14）
│  ├─ Demo/RealignMonitor    再位置合わせ（集める → 待つ → 足す）の判定。PONG と時刻だけを入力にする（§14）
│  ├─ Demo/AutoFadeCycle     自動フェード（表示中に 3DGS を出し入れし続ける）の判定（§14.5b）
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
| 6 PONG | PC→Quest | 50002 | `{seq,session,kf,cv_alive,cv_age,cv_scene,cv_window,cv_ok,cv_done,cv_busy,cv_on,ev_ack,…}`。§3.3 |
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

`kf` は `run.py` がこのセッションで受け取ったキーフレーム数、`cv_ok` は CV が採用した数、**`cv_done` は CV が処理し終えた数**（採否を問わず、解き直しまで済んだ分。ハートビートの `done`）。`cv_done ≥ kf` かつ `cv_busy` でなければ、PC は届いた分をすべて処理し終えている — デモの再位置合わせが「送信を止めて結果を待つ」判定に使う（§14.4）。`cv_busy` だけでは、届いたがまだ処理を始めていない瞬間（`localize` は 1 秒ごとに見に来る）を「終わった」と取り違える。どれもセッションごとに 0 から数え直す。

**CV の生存信号は別スレッドが書く**（`vptserver/cv/heartbeat.py`）。hloc の処理中もメインループとは別に 2 秒ごとに書くので、生存信号の鮮度は PC の速さに依存しない（以前はメインループからしか書けず、遅い PC の重い処理中は途切れて「CV 停止」と誤判定された）。書く場所は 2 か所:

| 場所 | 中身 | 使い道 |
|---|---|---|
| `<session>/cv_heartbeat.json` | 進み具合（`total_ok` / `done` / `busy` / `busy_n` / 直近の解の採否） | `run.py` がそのまま PONG に載せる |
| `<base>/cv_heartbeat.json` | 生存と、いま処理しているセッション名（`session`）・`busy` だけ。**枚数は書かない** | セッションにハートビートがまだ無いとき（RESET 直後、CV が前のセッションの処理を終えていない）、`run.py` はこちらを見て `cv_on` にそのセッション名を入れる。枚数を書かないのは、前のセッションの枚数を新しいセッションのものと取り違えさせないため |

C# と Python で鍵名・type 定数が食い違わないことは `tests/test_liveness.py` が機械的に検査する（生存信号のスレッドは `tests/test_heartbeat.py`）。

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
| `scale_range` | [1e-3, 1e3] | \|s\| の**健全性**のみ（発散・NaN を落とす）。**シーンの実スケール付近に締めない** — §5.5 |
| `expected_scale` | None（無効） | §5.4。指定時は**これがスケール判定の本体**になり `scale_range` は評価しない |
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

**品質のしきい値は PC 側 cv.json が単一の真実で、Quest 側には複製しない**（§5.5）。Quest が見るのは「PC には出せない判断」だけ:

1. **健全性**（NaN / Inf / `|s|`=0 / `inliers<3`）— 壊れた値を Transform に入れない。**シーン固有の量は見ない。**
2. **改悪防止** — 前回採用より `rmseWorsenFactor`=1.5 倍以上悪化した解は棄却。PC は毎回無状態で解き直すので「前回より悪い」を知らない。
3. **連続一致による確定** — **独立に解かれた解が連続して一致した回数が `minConsecutiveAgreements`（既定 2）に達して初めて「確定」**とする。

- 一致の判定は、2 つの解でスプラットの**境界ボックス 8 隅が動く距離**（`agreeMaxSceneShiftM`、既定 **0.15 m**）。位置・回転・スケールに別々の閾値を置くより「見た目がどれだけ違うか」に直結する。境界ボックスの隅は原点から 6m 以上離れるので、8cm まで下げると実機の雑音下ではほぼ成立せず、ロックしなくなる。
- 未確定の間も姿勢は適用する（収束の様子が見える）が、`Locked` にせず永続化もしない。
- 収束時刻として記録するのは**確定した瞬間**（`OnAutoAligned`）。`OnLocked` は手動 Lock でも鳴るので収束点には使えない。アンカーからの復元は `OnLocked` も `OnAutoAligned` も鳴らさず、`OnRestored` だけを鳴らす（§7.3）。

> **なぜ連続一致か**: 悪条件の解はキーフレームが増えると跳ねる。**独立に解かれた 2 つが同じ答えに落ち着くことは、残差が小さいことよりずっと強い証拠**で、想定していない失敗モードにも効く。

**時間予算**: 確定に 20 枚程度必要。`KeyframeUploader.autoMinIntervalS` が 2.0 だと送信だけで 40 秒かかり 60 秒の試行に余裕が無いので **1.0** にした。`cv.json` の `every` も 3→**2** にして一致判定の機会を増やした。

### 5.4 `expected_scale` は評価実験では使わない

COLMAP の任意単位と実メートルの比は、その再構成に固有の固定値（irt_short2 で 0.63、bld2 で 4.52）で、`--expected-scale` を渡せば棄却ゲートにできる。**しかし正しさには不要**（反射固定＋配置ゲート＋連続一致で誤り率は既に 0%）。シーン固有の既知値を推定に注入すると「メトリックスケールを解決する」という主張が弱くなるので、既定は無効。**推定値が独立に実測した値とどれだけ一致したかを結果として報告する**ほうが強い。デバッグで棄却理由を明示したいときだけ使う。

### 5.5 ゲートにシーン固有の量を埋め込まない（bld2 の全滅から）

**2026-08-12・bld2**: 10 トライアルが 1 度もロックしなかった。局所化は良好（インライア 183〜3600・全 KF 採用）、Sim(3) も良好（インライア率 0.79〜1.00・rmse 0.033〜0.052 m）だったが、**16 セッション全部が `|scale| 4.523 outside [0.5, 2.0]` で公開されなかった。**

推定は正しかった。受理 KF 30 枚の全ペア 431 組で world/COLMAP の距離比を取ると中央値 **4.5188** で、σ=4.5226 と一致する。**bld2 の COLMAP は 1 unit ≈ 4.52 m というだけ**（irt_short2 は 0.63 m だったので [0.5, 2.0] にたまたま収まっていた）。

**教訓: `scale_range` [0.5, 2.0] は「このシーンは 1 unit ≈ 1 m」というシーン固有の前提を、汎用ゲートの顔で埋め込んでいた。** §5.4 が `expected_scale` について言うこと（シーン固有の知識を推定に注入すると主張が弱くなる）は、粗いぶん自覚されないだけで `scale_range` にもそのまま当てはまる。既定を健全性チェックのみに変更した。

同時に踏んだ 2 つ目: **Quest 側が PC 側より厳しいしきい値を複製して持っていた**（`maxRmseM` 0.05 < PC 0.08）。実測 rmse 0.0506 / 0.0519 は PC が承認したのに Quest が黙って棄却する。しかも Quest 側はシーンにシリアライズされるため再ビルドしないと直せない。→ **品質しきい値は PC 側 cv.json に一本化**し、Quest からは削除した（§5.3）。

> **原則**: ゲートは「足りていないときに確定させない」ためのもの（`Specification.md` §6.2）。**シーンによって値が変わる量を、シーン非依存のふりをした既定値に入れない。** 正しさを担保しているのは反射固定・配置ゲート・連続一致であって、スケールの妥当域ではない。
>
> **新シーン投入時の確認**: 初回は `cv_heartbeat.json` の `solve_why` を必ず見る。`solve_published: false` が続くなら理由がそこに出ている。

---

## 6. 姿勢の適用と入力

### 6.1 `AlignmentManager` が姿勢の唯一の真実

splatRoot の SRT を所有し、次の経路から更新される。

| 経路 | 契機 |
|---|---|
| 手動操作 | コントローラ（`OperationGuide.md` §5-1） |
| 半自動 | 水平化＋床スナップ（左スティック押込） |
| 自動 | `OnSim3Received` → ゲート通過 → `ApplySim3`（`AcceptAutoResults=false` の間は無視。§14.4） |
| 復元 | `AnchorPersistence` の多数決が揃った時点で `AdoptAnchoredPose(restored:true)` |
| アンカー追従 | `AnchorPersistence.LateUpdate` → `SetAnchoredPose`（毎フレーム。**`IsAnchored` の間だけ**効く。§7.3） |

`IsAnchored` は「Locked かつ、姿勢が保存済みアンカーから再構成したもの」。手動操作・`ApplySim3`・`Unlock`・`ResetPose`・呼び寄せ・水平化・`ResetForNewTrial` のどれかで姿勢が変わると false に落ち、次の保存が完了したとき（`MarkAnchored`）に戻る。落とさないと、古いアンカーから再構成した姿勢が新しく合わせた姿勢を毎フレーム上書きする。

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

表示の API（`AlignmentManager`）: `SetSplatVisible` は即時（**消すのはフェードの途中でも即時**）。`FadeInSplats` / `FadeOutSplats` は今の不透明度から徐々に変える（デモの表示開始と自動フェード。§14.5b）。`ToggleSplatVisible` は人の操作（X・HUD の表示ボタン）専用で、`OnUserToggledSplats` を鳴らす。

---

## 7. 永続化（空間アンカー）

`AnchorPersistence`（v3）と純粋計算の `AnchorMath`。なぜこの形かは `Specification.md` §15.2〜15.5。

### 7.1 保存するもの

- **splatRoot の「各アンカーから見た相対姿勢」と符号付きスケール**。ワールド座標そのものは保存しない — トラッキング原点（FloorLevel）は起動・再センタリング・電源の入れ直しで変わり、数値の基準が失われる。アンカーは空間マップの中の点で、`OVRSpatialAnchor` が毎フレーム今の原点から見た姿勢に更新する。
- 保存先 `persistentDataPath/<fileKey>_<modelId>.json`（`version: 3`）。`fileKey` は既定 `vptalign`、**デモは `vptalign_demo`**（`DemoController.Awake` が `AppMode.DemoFileKey` に設定する。実験と同じアプリなので、分けないと実験の試行開始の `ClearSaved()` がデモの保存を消す）。`modelId` は `GaussianSplatController` の欄（空なら 3DGS アセット名）。ほかに `source`（`auto` / `manual`）・`rmse`・`savedUtc`・**モデルの指紋**（`dataHash` = `GaussianSplatAsset.dataHash`、`splatCount`）を持つ。
- JSON は一時ファイルに書いてから置き換える（途中で落ちても旧ファイルが壊れない）。

### 7.2 保存の手順

1. **アンカーを `anchor.count`（3）個作る。** 置き場所は `AnchorMath.SpreadPoints` で、**歩いた軌跡**（頭の位置を `anchor.walkSampleM` 0.5m ごとに記録）から互いに `anchor.sepM`（1.5m）以上離れた点を最遠点サンプリングで選ぶ。足りなければ頭の位置とその周り（半径 `anchor.sepM` の 6 点）で補う。
2. 作成完了時点のアンカー姿勢から相対姿勢を計算し、`SaveAnchorAsync` で 1 個ずつ保存する（一部が失敗しても残りで続ける）。
3. JSON を書き換える。
4. **ここで初めて旧アンカーを `EraseAnchorAsync` で消す。** 1〜3 のどこかで失敗したら旧い保存をそのまま残す（旧実装は先に旧アンカーを消していたので、作成に失敗すると新旧どちらも失った）。
5. 保存の間に姿勢が変わっていなければ `AlignmentManager.MarkAnchored()` で追従に戻す。

保存の契機は 2 通り:

| 契機 | 使う場面 |
|---|---|
| `OnLocked`（`autoSaveOnLock=true`。スロットルあり） | 実験でも簡易でもないシーン。`save.posM` 0.02m / `save.rotDeg` 1° / `save.scaleFrac` 0.005 をすべて下回る変化は書かず、`save.intervalS` 30s 以内の更新は保留して後で書く |
| `SaveNow(source)`（スロットルなし） | デモ。`DemoController` が `autoSaveOnLock=false` にし、確定・手動 Lock のときに明示的に呼ぶ（§14） |

### 7.3 復元と多数決追従

- 起動時に `BeginRestore()`。JSON が無ければ `NoSave`、指紋が今のモデルと違えば `ModelMismatch`（**復元しない** — 同名で中身の違う `.ply` に差し替えると古い姿勢を黙って当ててしまうため）。v2 以前の保存（指紋なし）は判定できないので通す。
- **見つかるまで再試行し続ける。** `LoadUnboundAnchorsAsync` が 0 件なら `anchor.retryIntervalS`（2s）ごとに読み直し（`Loading`）、読めたら `LocalizeAsync` を見つかるまで繰り返す（`Searching`）。電源投入直後は空間の再認識に時間がかかるため、1 回のタイムアウトで諦めない。
- **毎フレーム（`LateUpdate`。`DefaultExecutionOrder(100)` で `OVRSpatialAnchor.Update` の後）**、`IsTracked` のアンカーそれぞれから splatRoot の姿勢を再構成し、`AnchorMath.LargestAgreeingSet` で**互いに一致する最大の組**を選ぶ。一致の判定は**スプラット境界ボックス 8 隅の移動量**が `anchor.agreeM`（0.10m）以内（§5.3 の連続一致と同じ考え方）。
- 組の大きさが `anchor.minAgree`（2。保存数がそれより少なければ保存数）に達したら**多数決が揃った**（`HasConsensus`）とし、その平均姿勢を使う。
  - 初めて揃ったとき: `AlignmentManager.AdoptAnchoredPose(restored:true)` → `Locked` ＋ `OnRestored`。**`OnLocked` は鳴らさない**（鳴らすと保存が走り、いま復元したアンカーを作り直す）。**`OnAutoAligned` も鳴らさない**（実験の収束点の定義）。`AllowAdopt=false` の間は採用しない（デモで作業中に古いアンカーが姿勢を奪わないため）。
  - 以後: `SetAnchoredPose`（`IsAnchored` の間だけ効く。§6.1）。
- 追跡中のアンカーが 1 個も無いときは何もしない（最後の姿勢のまま。頭のトラッキングが生きていればワールドは動かない）。
- スタッフの `ForceShowAnyway()` は必要数を 1 に下げる（未検証。次の保存で解除）。
- 2 個では食い違ったときにどちらが正しいか決められない（1 個ずつの組になる）ので、既定を 3 個にした。

### 7.4 その他

- やり直したいときは `AnchorPersistence.ClearSaved()`（ファイル削除＋アンカー消去＋保留中の保存を破棄）。
- Meta XR SDK v85 で Obsolete になった `Save` / `Erase` / `LoadUnboundAnchors` / `Localize` は使わない（`*Async` 版を使う）。

**評価実験では自動的に無効化する**（§9.2）。無効化は `ExperimentController.Awake` で行う（`Start` だと `AnchorPersistence.Start` の復元と順序が不定）。

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
| Quest | `DemoController`（デモ） | 収集中なら **Quest と PC を対でリセットして集め直す**。保存中なら保存を破棄して再位置合わせを促す。表示中ならアンカーの多数決で確かめ直し、揃わなければ非表示（§14.3） |
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
| アンカー復元で起動直後に Locked になり「即ロック成功」と誤記録される | `ExperimentController.forceExperimentMode` が永続化を自動 OFF（**`Awake` で**。§7.4）。デモは同じアプリだが保存ファイル名が別（`vptalign_demo_*`。§14.1） |
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
| `scale_range` | [1e-3, 1e3] | — | \|s\| の健全性のみ（§5.5）。`--scale-range LO HI` |
| `expected_scale` / `scale_tol` | None / 0.10 | — / 比 | §5.4（既定は無効）。指定時は `scale_range` を評価しない |
| `min_baseline_m` | 1.50 | m | 配置ゲート |
| `min_second_extent_m` | 0.40 | m | 配置ゲート |
| `handedness` | -1 | — | 反射固定（0=自由推定） |
| `CV_STALE_S`（run.py） | 8 | s | CV ハートビートの失効 |
| `WATCHDOG_S`（cv_launcher） | 5 | s | 「`localize` が動いていなければ起動する」を見る間隔（落ちたときの再起動の間隔） |
| `QUICK_FAIL_S` / `MAX_QUICK_FAILS`（cv_launcher） | 30 / 3 | s / 回 | 起動からこの秒数以内に落ちるのが続いたら再起動を諦める |
| `ALREADY_RUNNING_S`（cv_launcher） | 8 | s | ベースの生存信号がこれより新しければ、別の `localize` が処理中として起動しない |
| `stop(timeout)`（cv_launcher） | 8 | s | 正常終了で標準入力を閉じてから待つ時間。過ぎたら Linux 側の PID を kill |
| `grace_s`（cv/lifecycle） | 10 | s | 標準入力が EOF になってから、重い処理の途中でも強制終了するまでの猶予 |
| `DB_FEATURE_CACHE`（cv/warm_hloc） | 400 | 枚 | 照合のために DB 画像の局所特徴をメモリに置く枚数（LRU。superpoint_max で 1 枚 ≈ 2 MB）。結果には影響しない（速さだけ） |
| `QUEST_STALE_S`（gui_status） | 6 | s | PC の GUI で Quest を「接続中」とみなす PING の鮮度（`pingIntervalS` 2 s の 3 回分）。表示だけで、送受信の判定には使わない |
| `RECENT_KF_S`（gui_status） | 30 | s | キーフレームがこれより新しく届いていれば、GUI を閉じるときに確かめる（試行・再位置合わせの途中とみなす） |
| `CV_SILENT_AFTER_S`（gui_status） | 300 | s | `localize` のプロセスが動いているのに生存信号がこれだけ無ければ、GUI が異常として赤で出す（初回の WSL 起動と DB の読み込みの分は待つ） |

### 10.2 Quest 側（`AlignmentManager` / `UDPManager` / `KeyframeUploader`）

| キー | 既定 | 単位 | 使用 |
|---|---|---|---|
| （品質しきい値） | — | — | **持たない。PC 側 cv.json が単一の真実**（§5.3 / §5.5）|
| （健全性） | NaN / Inf / \|s\|=0 / inliers<3 | — | 壊れた値を弾くだけ。シーン固有の量は見ない |
| `rmseWorsenFactor` | 1.5 | 倍 | 改悪防止（PC は無状態なので Quest にしか出せない判断）|
| `minConsecutiveAgreements` | 2 | 回 | 確定に要する連続一致 |
| `agreeMaxSceneShiftM` | 0.15 | m | 一致の判定（bbox 8 隅の移動量） |
| `smoothMaxPosM` / `smoothMaxDeg` | 0.3 / 10 | m / deg | 補間する差の上限 |
| `smoothDurationS` | 0.5 | s | 補間時間（0=即時） |
| `startHidden` | true | — | 起動時はスプラット非表示 |
| `save.posM` / `save.rotDeg` | 0.02 / 1 | m / deg | 保存抑制しきい（`OnLocked` 経由の保存だけ。`SaveNow` は抑制しない） |
| `save.scaleFrac` / `save.intervalS` | 0.005 / 30 | 比 / s | 保存抑制 |
| `anchor.count`（`anchorCount`） | **3** | 個 | 多数決のため 3（2 では食い違いの正否を決められない）。**シーンの値が優先**: `GSTestScene` は 2 のまま（実験では永続化を使わない）、`DemoScene` は 3 |
| `anchor.sepM`（`anchorSeparationM`） | 1.5 | m | アンカー同士の最小間隔（歩いた軌跡から選ぶ） |
| `anchor.agreeM`（`anchorAgreeM`） | 0.10 | m | 2 つのアンカーの「一致」= 境界ボックス 8 隅の移動量。**要実機調整**（旧 `restoreOutlierM` 0.05 は原点位置だけの比較だった） |
| `anchor.minAgree`（`minAgreeingAnchors`） | 2 | 個 | 表示に必要な一致数（保存数が少なければ保存数） |
| `anchor.retryIntervalS`（`retryIntervalS`） | 2 | s | 読み込み・Localize の再試行間隔 |
| `anchor.walkSampleM`（`walkSampleM`） | 0.5 | m | 歩いた軌跡の記録間隔 |
| `anchor.timeoutS`（`timeoutSeconds`） | 10 | s | アンカー操作 1 回のタイムアウト |
| `anchor.fileKey`（`fileKey`） | `vptalign` | — | 保存ファイル名の接頭辞。**デモは `vptalign_demo`**（`DemoController` が実行時に設定し、`DemoScene` のシリアライズ値も同じ） |
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
| パネル高 `h`（デモ） | 628 px | 指標行の下に罫線＋チェック行（40）＋ボタン 2 列 3 行＋全幅 1 行（実験へ戻る）。最下段の下端（上から 546）と診断行の罫線（上から 568）の間に 22px |
| 実験パネルの最下段 | `Show/Hide splats [X]` と `Demo mode [both sticks hold]` を半分ずつ | デモへの入口。行を足さずに割ったので実験パネルの高さ（688）は変えていない。`NeedsSetup` の指標行にも `Demo without CPs: hold both sticks for 2 s` を出す |
| 来場者向け案内（デモ） | 560×124 px・3 行（案内 28px / 補足 17px / スタッフ向け 14px）・頭の前 `hintDistanceM` 1.0m・`hintDownOffsetM` 0.12m 下・減衰 5/s で視線に追従。文言の組み立ては 10Hz | 見回してもらうのが目的なので、主パネルのような「置いてある」追従ではなく常に視界に入れる |
| スタッフ向けの行（デモ） | `Staff: hold Y + B together for 2 s to open the staff panel`。**スタッフが要る場面だけ**（`DemoController.ShowStaffHint`: デモに入って `staffHintOnEntryS` 以内・`NeedsAlignment`・`Aligning`・`Relocating` が `restoreWaitS` を超えたとき）。Y＋B を押している間は案内ごと `Opening the staff panel…` と残り秒数に差し替える | スタッフも Y＋B を忘れるので UI に書く。ただし表示中（来場者が見ている間）は出さない — 開き方が来場者に知れると、パネルの奥の操作（手動調整して保存・保存の消去）に手が届く。2 秒の長押しでも無反応だと効いているか分からないので進み具合を出す |

### 10.5 デモ展示（`DemoController`）

値はすべて**要実機調整**（§14.6 の実機確認で決める）。

| キー | 既定 | 単位 | 使用 |
|---|---|---|---|
| `lookAroundHintS` | 3 | s | 起動後、見つからないままこの秒数で来場者に `Look around the room`（確かめ直し中は 1s） |
| `restoreWaitS` | 30 | s | 見つからなければスタッフ操作待ち（`NeedsAlignment`）へ。裏では探し続ける |
| `reverifyGraceS` | 0.5 | s | 原点の移動・スリープ復帰のあと、多数決が揃わないままこの秒数で非表示（`Relocating`） |
| `fadeInS` | 0.5 | s | 表示のフェードイン |
| `autoFadeOnStart` | false | — | デモに入った時点で自動フェード（§14.5b）を ON にしておくか。展示中はスタッフパネルの右トリガーで切り替える |
| `autoFadeShownS` / `autoFadeHiddenS` | 5 / 3 | s | 自動フェードで 3DGS を出しておく / 消しておく時間（どちらもフェードの時間を含む） |
| `autoFadeS` | 1.5 | s | 自動フェードの 1 回のフェード。出す段・消す段より長ければその長さに切り詰める |
| `resetSettleS` | 1.0 | s | RESET 後、PC が新セッションを切るのを待つ（§10.3 と同じ理由） |
| `saveTimeoutS` | 40 | s | 保存が返らなければ失敗扱いで表示へ |
| `staffComboHoldS` | 2 | s | Y＋B 同時長押しでスタッフパネルの開閉 |
| `staffHintOnEntryS` | 10 | s | デモに入ってからこの秒数は、来場者向け案内にスタッフパネルの開き方を出す（切り替えた本人が読む） |
| `collectTargetAccepted` | 24 | 枚 | 再位置合わせ: PC の**採用**枚数（`cv_ok`）がこれに達したら送信を止めて待つ。PC の解き直しの範囲（`window`）と揃える |
| `collectMaxS` | 60 | s | 最初の収集の上限。**実際に送信していた時間**で数える（PC 待ちの一時停止は数えない） |
| `topUpAccepted` / `topUpMaxS` | 6 / 30 | 枚 / s | 確定しなかったときの追加収集の枚数と上限時間（同上） |
| `maxTopUps` | 3 | 回 | 追加収集の最大回数。超えたら理由を出して保存済みの位置合わせへ戻る |
| （収集・待機の合計時間の上限） | **置かない** | — | PC が遅くても、生きていて処理が進んでいる限り待つ。止めるのは下の「止まった」「固まった」「死んだ」のときだけ |
| `backlogPauseAt` / `backlogResumeAt` | 4 / 1 | 枚 | PC の処理待ち（`kf − cv_done`）がこれ以上で送信を一時停止 / これ以下で再開（バックプレッシャー） |
| `inFlightS` | 2.5 | s | 送信を止めてから「処理し終えたか」を判定し始めるまで（最後の KF が届き PONG に載るまで。PONG は 2 秒ごと） |
| `resultGraceS` | 2.5 | s | PC が処理し終えてから、結果が届いて一致判定されるまでの猶予（`run.py` は結果を 1 秒ごとに返送） |
| `waitStallS` | 30 | s | **止まった**: このセッションのキーフレームが溜まっているのに、CV は生きていて処理中でもなく、処理済み枚数がこれだけ増えない（localize が別のフォルダを見ている等）。遅いだけなら処理中なので当たらない |
| `busyNoProgressFailS` | 900 | s | **固まった**: このセッションで処理中のまま、これだけ 1 枚も処理が終わらない。PC 待ちで送信を止めるので 1 回の処理は数枚まで（1 枚 3 分でも当たらない） |
| `pcLostFailS` | 10 | s | **死んだ**: `run.py` の無応答、または CV の生存信号の途絶がこれだけ続いた。生存信号は処理中も別スレッドが書くので、途絶える＝プロセスが死んだ |
| `cvStartWaitS` | 120 | s | CV の生存信号が**どこにも無い**（セッションにもベースにも）状態を待つ上限。新しい localize なら前のセッションを処理中でも `cv_on` で分かるので、ここに来るのは未起動・別フォルダ・古い localize のときだけ |
| `cvBusyHangS` | 300 | s | 古い localize（処理中に生存信号を書かない版）との互換: 処理中フラグのまま生存信号がこれだけ古ければ固まったとみなす |
| `clearHoldS` | 3 | s | A 長押しで保存の消去（スタッフパネルを開いている間だけ） |
| `tapMaxS` | 0.8 | s | これより短い押下を短押しとみなす |
| `experimentSwitchHoldS` | 2 | s | スタッフパネルを開いている間、両スティック押込でこの秒数 → 実験へ戻る |
| `ExperimentController.demoSwitchHoldS` | 2 | s | 実験の画面で両スティック押込をこの秒数 → デモへ |

---

## 11. ビルド・権限・プロジェクト設定

- **ビルド**: Android / ARM64 / IL2CPP / **Vulkan**（aras-p レンダラは D3D12/Metal/Vulkan のみ。OpenGL ES では描画できない）。
- **Scene List**: `GSTestScene`（先頭＝起動時）と `DemoScene` の 2 つ。**両方入っていないとデモへ切り替えられない**（`AppMode` が `LogError` で知らせる）。APK は 1 つ（§14.1）。
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
python -m tests                        # 全部（1 本ずつ別プロセス。落ちたものだけ出力を出す）
python -m tests.test_geometry          # Umeyama / Sim(3) / クォータニオン（合成データ）
python -m tests.test_gates             # 品質ゲートの純粋関数
python -m tests.test_alignment_gates   # 退化（共面・共線）と反射固定の効果
python -m tests.test_liveness          # PING/PONG・CV ハートビート。C# と Python の鍵名/type 定数の一致も検査
python -m tests.test_events            # type=7 EVENT の振り分け・重複除去・CP JSON 書き出し
python -m tests.test_heartbeat         # CV の生存信号: 処理中も別スレッドで更新される・前のセッションを処理中なら cv_on
python -m tests.test_cv_launcher       # CV の自動起動: wsl.exe の出力・日本語/空白のパス・終了の連動・再起動の方針
python -m tests.test_setup_windows     # セットアップの判断: ディストリの選び方・DB の状態・ファイアウォール
```

`test_liveness.py` と `test_events.py` は **C# のソースを読んで定数名・鍵名の一致を機械的に検査する**（type 定数とヘッダ長は `VpPacket.cs`、PONG の鍵名は `UDPManager.cs`）。プロトコルを変えたときに片側だけ直す事故を防ぐため。

**`localize.watch` を書き換えたときの回帰確認**（WSL2）: 保存済みのセッション（`received/session_*`）を数枚ずつ新しいセッションへ流し込み、`kf_localized.jsonl`・`result_sim3.json`・解き直しのログ（scale・インライア・採否）が書き換え前と一致するかを比べる。GPU で同じ入力なら結果は決定的（同じ 30 枚を 2 回流して完全に一致）。2026-10 の整理（`_Watcher` への分割）はこの方法で、公開されるセッション（`irt_short2`）とゲートで非公開になるセッション（`bld2`）の両方で一致を確認した。

**Quest 側の純粋なクラス**（`RealignMonitor` など）は Unity のバッチモードの `-executeMethod` でシナリオを流して確かめられる（2026-10 の整理で `RealignMonitor` を 30 項目で確認。テストのコードはリポジトリに残していない）。

### 12.2 hloc/pycolmap・WSL を使う部分

**WSL2 実機でしか確認できない。** `hloc_db` の構築 → 自己ローカライズ（DB 画像を自分自身に照合）→ 実キーフレームからの Sim(3) で検証する（自己テストで誤差/シーン対角 ~1e-4、実キーフレームで残差 ~1cm を確認済み）。CV 環境は `python -m vptserver.cv.check_env` で確かめる。

CV の自動起動は作者の PC で確認済み（2026-10）: 既定でないディストリの検出・CUDA の表示・`start_experiment.bat` からの起動と PONG の `cv_alive`・二重起動の見送り・正常終了・**`run.py` を強制終了しても `localize` が 2 s 以内に終わる**こと。セットアップは既存環境での再実行（何も壊さない）、空白・括弧・日本語を含むフォルダでの `.bat` の動作、**空の venv・hloc からの `setup_wsl.sh` の通し（固定した版が入り直しなしで入る）とその環境での自己ローカライズ（誤差/対角 5e-5）**を確認済み。**まっさらな Windows での通し（WSL の新規導入・ファイアウォールの変更）は未確認。**

### 12.3 実機

`CLAUDE.md` §0 の検証ゲート（両眼描画・ワールドへの貼り付き・ソート安定・フレームレート）と、`OperationGuide.md` §9-0 の実験前チェック。

---

## 13. 既知の限界

- **フル連続追従（毎フレームの微調整）は実装していない。** 現状は「アンカーで貼り付き＋定期的な再ローカライズ」の軽量版で運用している。
- **オンデバイス CV は無い。** PC（WSL2）が居ない環境では自動位置合わせは動かない（手動・半自動と、既存アンカーからの復元は動く）。
- **キーフレームの再送が無い。** 取りこぼしは破棄して次のキャプチャを待つ。
- **UDP は非暗号化**（LAN 内・研究用途）。
- **精度は歩き方に依存する**（§5.2）。ゲートは不十分な歩行を確定させないだけで、精度そのものは上げられない。
- **デモの永続化は実機で未確認**（§14.6）。空間の再認識に失敗する条件（境界のリセット・照明の大きな違い）は Quest の振る舞いに依存する。

---

## 14. デモ展示モード

なぜこの形かは `Specification.md` §15、運用は `OperationGuide.md` §10。

### 14.1 構成

- **シーン**: `DemoScene.unity`。`GSTestScene` を複製し、実験用コンポーネント（`ExperimentController` / `ExperimentLogger` / `ControlPointMeasure` / `ControlPointEdit` / `ControllerTipCalibration`）と `ControlPoints` を外して `DemoController` と `TrackingOriginWatcher` を足したもの。`AnchorPersistence` は `experimentMode=false` / `restoreOnStart=true` / `anchorCount=3` / `fileKey=vptalign_demo`。
- **同じ APK**: Scene List は `GSTestScene`（先頭＝起動時）と `DemoScene`。切替は `AppMode`（`SceneManager.LoadScene(Single)`）。
  - **実験 → デモ**: 実験の画面で**両スティック押込を 2 秒**（`ExperimentController.demoSwitchHoldS`）、または HUD の `Demo mode`。この画面ではスティック押込に他の役割が無い（`PoseControls=Locked`）。CP の設定も先端校正も要らない。
  - **デモ → 実験**: スタッフパネルを開いて同じ操作（`DemoController.experimentSwitchHoldS`）、または `Back to experiment mode`。
  - **切り替えを受け付けない局面**（実験側 `DemoSwitchBlockReason`）: 試行中・測量中・Virtual CP 編集中・先端校正中、および**測り直しの効かない記録（Real CP の実測・Virtual CP の編集・試行）が PC に未送達のとき**（outbox はシーンと一緒に消える。端末内 JSONL には残る）。**送信待ちがあるだけでは止めない** — 起動時の `session_start` 等は PC の ACK を待って常に残っており、PC を起動していないとデモへ永遠に入れなくなる（Editor で判明）。デモ側は再位置合わせ・保存・手動調整の途中を受け付けない。
- **保存の分離**: `persistentDataPath` は共通なので、デモの保存ファイル名を `vptalign_demo_<modelId>.json` に分ける（§7.1）。実験は永続化を `Awake` で止め、空間アンカーも作らないので、実験の試行開始の `ClearSaved()` はデモの保存にもアンカーにも触れない。
- **シーン切替と通信**: `UDPManager` は `OnDestroy` で受信スレッドを止めてソケットを閉じ、受信ソケットは `ReuseAddress` 付きなので、新しいシーンの `UDPManager` がそのまま同じポートを開ける（Editor で往復を確認）。
- **再起動すると実験シーンから始まる**。展示中にアプリが落ちたらスタッフが入り直す（前回のモードを覚える起動は今後の課題。`Specification.md` §15.8）。
- **共通部分の二重管理**: リグ・`Managers`・スプラットの設定は 2 つのシーンに別々に入っている。片方を変えたらもう片方も直す（Prefab 化は今後の課題。`Specification.md` §15.8）。

### 14.2 状態遷移（`DemoController.DemoState`）

| 状態 | 3DGS | 来場者向け案内 | 抜ける条件 |
|---|---|---|---|
| `Locating` | 非表示 | 3s 後 `Look around the room` | 多数決が揃う → `Showing` / 保存なし・別モデル → `NeedsAlignment` / `restoreWaitS` 経過 → `NeedsAlignment`（裏では探し続ける） |
| `Showing` | フェードイン（自動フェードが ON なら出し入れを繰り返す。§14.5b） | なし | 原点の移動・スリープ復帰・装着で確かめ直し。`reverifyGraceS` 以内に揃わなければ → `Relocating` |
| `Relocating` | 非表示 | 1s 後 `Look around the room` | 揃う → `Showing` |
| `NeedsAlignment` | 非表示 | `Please ask a staff member` | 揃う（遅れて見つかった）→ `Showing` / スタッフ操作 |
| `Aligning` | 非表示（X で覗ける） | `Aligning…`＋`Walk slowly around the room · 12 / 24` / 待機中は `Computing on the PC — you can stop walking` | `OnAutoAligned` → `Saving` / キャンセル → 保存済みの姿勢へ戻る / 足しても確定しない・PC が途切れた → 理由を出して保存済みの姿勢へ戻る（§14.4） |
| `Saving` | フェードイン（確定済み） | なし | 保存完了 → `Showing` / `saveTimeoutS` → `Showing`（未保存の警告） |
| `Manual` | 表示 | （スタッフパネル） | A（Lock）→ `Saving` / Y → 取り消して保存済みの姿勢へ |

「多数決が揃う」の判定は `Verified = AnchorPersistence.HasConsensus && AlignmentManager.IsAnchored`。保存に失敗した姿勢は `IsAnchored` にならないので、原点が動くと確かめ直しが通らず `Relocating` に留まる（スタッフパネルに「保存に失敗した」と出る）。

### 14.3 原点の移動・スリープ・装着

| 事象 | `Aligning` | `Saving` | `Showing` |
|---|---|---|---|
| `TrackingOriginWatcher.OnOriginChanged` | **Quest と PC を対でリセットして集め直す**（2 つの座標系が混ざった Sim(3) を防ぐ） | 保存を破棄（`ClearSaved`）して `NeedsAlignment`（旧座標系の splatRoot と新座標系のアンカーで相対姿勢が壊れる） | 確かめ直し |
| `OnApplicationPause(false)`（スリープ復帰） | 集め直す | — | 確かめ直し |
| `OVRManager.HMDMounted`（装着） | — | — | 確かめ直し＋通ったら表示し直す（前の来場者が X で消した場合） |
| `OVRManager.HMDUnmounted` | スタッフパネルを閉じる（開いたまま次の来場者に渡さない。手動調整中なら取り消す） | ← | ← |

原点が動いたら歩いた軌跡（アンカーの置き場所の候補）も捨てる（`ResetWalk`）。

### 14.4 再位置合わせ（Re-align）

1. 自動送信を止め、`AlignmentManager.AcceptAutoResults=false`。
2. **type=4 RESET** を送る（payload `{"trial":0,"label":"demo-<yyyyMMdd-HHmmss>-NN","mode":"demo"}`）と同時に `AlignmentManager.ResetForNewTrial()`（**対で行う**。§9.2）。ラベルは実験の `trial-NN` と衝突しない形にする（解析は `trial.json` の label で試行と照合する）。`run.py` は payload を `trial.json` に残すだけ。
3. 歩いた軌跡を捨て、`resetSettleS` 待ってから `AcceptAutoResults=true`・自動送信 ON。RESET 前の PONG のセッション名を覚えておき、**PONG のセッション名が変わるまで PC の枚数を信じない**（RESET 直後の PONG は前のセッションの `cv_ok`（例: 30）を返していることがあり、信じると集め始めた瞬間に「目標到達」と誤判定する）。
4. **集める → 待つ → 足す**（`Specification.md` §15.6b）:

   | 段階 | 進む条件 | 送信 |
   |---|---|---|
   | Collect | PC の採用枚数（新セッションの `cv_ok`）が `collectTargetAccepted`（24）に達する、または送信していた時間が `collectMaxS`（60s）。追加収集は `+topUpAccepted`（6）または `topUpMaxS`（30s）。**合計時間の上限は無い** | ON（PC の処理待ちが `backlogPauseAt`（4）以上で一時停止、`backlogResumeAt`（1）以下で再開） |
   | Wait | `inFlightS` 経過後、`cv_done ≥ kf` かつ `cv_busy` でない状態が `resultGraceS` 続く（＝PC が届いた分を処理し終え、結果も届いた） | OFF |
   | → 未確定 | 追加収集 `maxTopUps`（3）回まで Collect に戻る。超えたら諦める | — |

   **PC が遅くても正常に終わる**（実機で 1 枚 13 秒・25 枚溜まって失敗した。`Specification.md` §15.6b）:
   - **収集・待機の合計時間に上限を置かない**。PC が生きていて処理が進んでいる限り待つ。
   - PC の状態を 6 つに分ける（`RealignMonitor.Status`。判定はすべて `Demo/RealignMonitor.cs` にあり、`DemoController` は結果に応じて送信の ON/OFF・振動・文言を出すだけ）。**遅いだけ・片付け中は待つ**:

     | 状態 | 判定（PONG） | 扱い |
     |---|---|---|
     | Ok | `cv_alive`、今のセッション、処理中でない | 正常 |
     | Busy | `cv_alive`、今のセッション、`cv_busy` | 正常（処理中）。処理中のまま `busyNoProgressFailS`（15 分）1 枚も終わらなければ**固まった**として諦める |
     | Switching | `cv_alive`、`cv_on` が別のセッション | **前のセッションの片付け中**。上限なしで待つ。HUD に `Waiting for the PC to finish earlier work…` |
     | Starting | 生存信号がどこにも無い（`cv_age<0`） | `cvStartWaitS`（120s）で諦める（未起動・別フォルダ・古い localize） |
     | Stale | 生存信号が途絶えた | `pcLostFailS`（10s）で諦める |
     | Down | `run.py` が応答しない | `pcLostFailS`（10s）で諦める |
   - 加えて、Ok のまま（生きていて処理中でもない）**このセッションのキーフレームが溜まっていて** `waitStallS`（30s）処理済み枚数が増えなければ、**止まった**（localize がこのセッションを処理していない）として諦める。HUD は `run.py` のウインドウの `[cv]` の行を見るよう案内する（`localize` は `run.py` が起動するので、利用者が `--watch` を合わせる場面は `--no-cv` の手動起動だけ）。
   - 待機中の HUD は上限時間の代わりに、`PC processed 27 / 30 (processing 3)… · 32 s` と PC の処理の進み具合を出す。
   - 古い localize（生存信号を処理中に書かない版）とも動くよう、処理中フラグのまま生存信号が古いだけなら `cvBusyHangS`（300s）まで Busy とみなす。再位置合わせを始める前の判定（`RealignBlockReason`）も、その場合は `CV worker not running` ではなく `the PC is still processing earlier keyframes` と出す。

   - **止めて待つだけにはしない**: 新しい解は新しいキーフレームが届いたときにしか作られず、確定には連続一致が要るので、止めたままだと 2 つ目の解が来ないことがある。
   - 歩き方の指示は PC の棄却理由（`cv_why`）の先頭で選ぶ: `not enough movement` → `Walk farther — cover more of the room` / `path too straight` → `Walk a curved path, not a straight line` / その他の棄却 → `Keep moving slowly and look at textured surfaces`。送った枚数の半分も採用されていなければ `Few keyframes recognized — check the lighting …`。先頭の文字列が `gates.check_geometry` と一致することは `tests/test_gates.py` が検査する。Quest は品質を判定しない（PC の採用枚数と理由を表示に使うだけ。§5.5）。
   - Collect → Wait、Wait → 追加収集のたびに短く振動する（歩いているスタッフに「止まってよい／もう少し歩く」を伝える）。
   - **諦めるとき**（追加収集を使い切った・PC が止まった／固まった／死んだ）: 送信を止めて凍結し、理由（PC の棄却理由、または「照合できた枚数が少ない」「解が一致しなかった」）を `LastAlignFailure` に残して保存済みの姿勢へ戻る（無ければ `NeedsAlignment`。スタッフパネルに `Could not align: …`）。
5. `OnAutoAligned`（連続一致で確定。Collect 中でも Wait 中でも）で**自動送信を止め、`AcceptAutoResults=false` に戻す**（凍結。遅れて届いた解で来場者の前のシーンが跳ねないように）→ `SaveNow("auto")`。
6. 古い保存は、新しい保存が完了するまで消さない（§7.2-4）。

前提は PC（`ServerAlive && CvAlive`）。無ければボタンは無効で、理由が出る。PC が無いときの最終手段は手動調整（`Manual` → A で Lock して保存）。

### 14.5 入力

| 入力 | 来場者 | スタッフパネル | 手動調整中 |
|---|---|---|---|
| X | 3DGS の表示切替 | ← | ← |
| Y＋B 同時 2s | スタッフパネルを開く | 閉じる | 無効（先に Y で取り消すか A で確定する） |
| A 短押し | — | Re-align / Cancel re-align | Lock して保存（`AlignmentManager`） |
| A 長押し 3s | — | 保存の消去 | — |
| B 短押し | — | Show anyway（見つかっているが一致しないとき） | 無効（`AlignmentManager.ResetButtonEnabled=false`。初期姿勢は Editor 上の置き場所で展示では無意味なうえ、Y＋B を試すと押した瞬間に調整中の姿勢が飛ぶため） |
| Y 短押し | — | 手動調整を始める | 取り消す |
| 右トリガー | — | 自動フェードの ON/OFF（来場者が X で止めていたら再開。§14.5b） | スケール（`AlignmentManager`） |
| 両スティック押込 2s | — | 実験へ戻る（`AppMode.LoadExperiment`） | 無効（スティック押込は呼び寄せ・水平化） |
| スティック / トリガー / スティック押込 | — | — | 移動・ヨー・スケール・呼び寄せ・水平化（`AlignmentManager`） |
| 左グリップ | 何もしない（`KeyframeUploader.ManualGripEnabled=false`） | ← | ← |
| 右グリップ | 何もしない（実験の HUD トグルも無効） | スタッフパネルを隠す／出す（`DemoController.ToggleStaffPanel`。スタッフモードのまま、ボタンの割当も効いたまま。開き直すと必ず出る） | ← |

- 来場者が触れる状態では `PoseControls=Locked`（X だけが残る）。来場者の間、左右のグリップに何も割り当てないのは、来場者が握ったまま操作するため（右グリップはスタッフパネルを開いている間だけ効く。押したまま開いても、その押下では隠れない）。
- スタッフパネルを隠している間は何も表示されない（来場者向けの案内はスタッフモードの間は出さない）。3DGS を遮らずに重なりや自動フェードを確かめるため。ヘッドセットを外すとスタッフモードごと閉じる（§14.3）。
- HUD のスタッフパネルの全ボタンに上表の物理割当がある。「Clear saved」は Poke では実行せず、A 長押しを案内するだけ（元に戻せないため）。
- **スタッフパネルの開き方（Y＋B 2 秒）は来場者向け案内の 3 行目に出す**（スタッフが要る場面だけ。§10.4）。押している間は残り秒数を出す。
- 押しても受け付けない操作には、長めの振動を返す（押して無反応にしない。§9.5-7）。

### 14.5b 自動フェード（`Demo/AutoFadeCycle`）

表示中（`Showing`）に、3DGS を「出す（`autoFadeShownS`）→ フェードアウト → 消す（`autoFadeHiddenS`）→ フェードイン → …」と繰り返す。重ねる前と後の見比べを、X を知らない来場者にも見せるため（`Specification.md` §15.4-2）。スタッフがパネルの右トリガー（HUD の `Auto fade`）で ON にする。ON/OFF は起動中だけ覚える（既定は `autoFadeOnStart`）。

- **回すのは `Showing` で、確かめ直しの途中でないときだけ。** それ以外の局面に入ったら止まり、表示は `DemoController.Enter` が決める（非表示は即時）。確信の無い姿勢を見せない原則（§14.2）は変わらない。
- **消すのはフェードアウトの途中でも即時**（`AlignmentManager.SetSplatVisible(false)`）。フェードで消すのは自動フェードの消す段だけ（`FadeOutSplats`）。フェードは今の不透明度から始めるので、途中で向きを変えても跳ねない。
- **来場者が X（または HUD の表示ボタン）を押したら、その人の間は止める**（`AlignmentManager.OnUserToggledSplats`）。押した結果（出す／消す）をそのまま残す。回し続けると数秒で上書きされ、X が壊れたように見えるため。**次にかぶった人（`HMDMounted`）から再開する。** 回っていない局面（再位置合わせ中に X で覗く など）の X では止めない。止めている間、パネルのボタンは `Resume auto fade (paused by X)` になり、右トリガーで OFF にせず再開する。
- スタッフが OFF にしたとき消している段だったら、フェードインして出した状態に戻す。
- 判定（いつ出す・消すか）は純粋クラスの `AutoFadeCycle` にあり、`DemoController` は結果を `AlignmentManager.FadeInSplats` / `FadeOutSplats` に渡すだけ（`RealignMonitor` と同じ分け方）。

### 14.6 実機で確かめること（未了）

| 条件 | 期待する結果 |
|---|---|
| 同じ場所で電源 OFF→ON | 数秒で表示 |
| 部屋の別の場所・別の向きで電源 ON | 数秒で表示（原点は変わるが、アンカーが吸収する） |
| アプリの強制終了と再起動、Meta ボタンでの再センタリング | 数秒で表示 |
| 装着中のスリープと復帰 | 一瞬非表示 → 表示 |
| 照明を落とす | 見回せば表示、または `NeedsAlignment`（理由が出る） |
| 境界の履歴を消す | `NeedsAlignment`（`Saved anchors not found …`） |
| 位置合わせ中にヘッドセットを外す | 自動で集め直す |
| 実験 → デモ → 実験 → デモと切り替える | 2 回目のデモで保存が復元される（実験の試行開始を挟んでも消えない） |

各条件で表示までの秒数と、表示された姿勢の見た目のずれを記録し、§10.5 と `anchor.agreeM` を決める。
