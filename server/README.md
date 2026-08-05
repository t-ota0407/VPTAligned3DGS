# VPTAligned3DGS — PC オフロードサーバ

Quest 3 から UDP で送られてくるキーフレーム（パススルーカメラ画像 ＋ 内部行列 K ＋ カメラのワールド姿勢 ＋ 時刻）を受信し、**COLMAP 再構成に対して再ローカライズして Sim(3) を解き、Quest へ返す**。

**重い CV は WSL2(Ubuntu)+CUDA で動かす。** UDP の受信・返送・GUI は Windows の `run.py` が担当し、両者は共有フォルダ（`/mnt/c`）経由で疎結合に動く。

> pycolmap は Windows ネイティブで `reconstruction.rigs` にアクセスした時点で access violation で落ちる（pycolmap 4.0.4 / 3.13.0 の両方、numpy のバージョン・torch の同居に依らず再現）。Linux では安定するため、hloc/pycolmap を使う処理だけを WSL2 に置いている。

関連文書: 全体の入口 [`../README.md`](../README.md) / 設計 [`../unity/Specification.md`](../unity/Specification.md) / 実装仕様 [`../unity/ImplementationSpec.md`](../unity/ImplementationSpec.md) / 運用手順 [`../unity/OperationGuide.md`](../unity/OperationGuide.md)

---

## セットアップ

Quest と**同じ LAN** にある PC で実行する。

### Windows 側（受信・返送・GUI）

```powershell
cd server
python -m venv .venv
.venv\Scripts\Activate.ps1
pip install -r requirements.txt
```

### WSL2 側（CV）

```bash
python3 -m venv ~/vptcv          # ホーム直下に作る（/mnt/c は遅い）
source ~/vptcv/bin/activate
python -m pip install -U pip wheel

cd /mnt/c/<repo への WSL2 パス>/server
pip install -r requirements.txt -r requirements-cv.txt
pip install torch torchvision --index-url https://download.pytorch.org/whl/cu124

# hloc は PyPI に無い → GitHub からサブモジュール込みで導入
cd ~ && git clone --recursive https://github.com/cvg/Hierarchical-Localization.git
cd Hierarchical-Localization && pip install -e .

# 確認
python -c "import numpy, cv2, pycolmap, torch; from hloc import extract_features; print('CV OK', torch.cuda.is_available())"
```

CV 依存の確定版: **torch cu124 / pycolmap==3.13.0 / numpy 2.x**（`requirements-cv.txt`）。

---

## 実行

```bash
# 0) シーンごとに一度（WSL2/GPU）: 再ローカライズ DB の構築
python -m vptserver.cv.hloc_db --scene scene_data/<name>

# 1) Windows: 受信・返送・GUI（起動ごとに received/session_<時刻>/ と cv.json を生成）
python run.py

# 2) WSL2: 最新セッションを自動追従し、cv.json をライブ反映して Sim(3) を更新
python -m vptserver.cv.localize
```

`run.py` のオプション:

```bash
python run.py --verbose            # 届いた全データグラムの送信元 IP / サイズを表示（疎通確認）
python run.py --no-beacon          # ビーコンを止める（手動 IP 運用）
python run.py --scene scene_data/example2 --every 5 --min 5
python run.py --port 50000 --beacon-port 50001 --out received
```

**停止は Ctrl-C**（受信がアイドル中でも即停止する）。

**設定は `run.py` 側に集約される。** `--scene`/`--every`/`--min` は `session/cv.json` に書かれ、WSL2 の `localize` がそれを読む（ライブ反映）。よって **WSL2 は引数なしでよい**。`scene_data/` にビルド済みシーンが 1 つなら scene の指定も要らない（自動検出）。GUI からも編集できる。

### 運用契約: 1 回の `run.py` 実行 ＝ 1 セッション

起動ごとに `received/session_<時刻>/` を作って保存し、WSL2 は最新を自動追従する。**手動クリアは不要**で、セッションが自動的に分離される（別座標系の混入や reqId 衝突を回避）。Sim(3) はそのセッションの Quest ワールド座標系が基準。跨セッションの持ち越しは Quest 側の空間アンカーが担う。

評価実験では、Quest から **type=4 RESET** が届くと `run.py` と Quest アプリを再起動せずに新しいセッションへ切り替わる。

---

## ネットワーク

- PC のファイアウォールで **UDP 50000（データ）の受信**を許可する（Windows は初回ダイアログで「許可」）。
- ポート: **50000** データ（Quest→PC）/ **50001** ビーコン（PC→LAN ブロードキャスト）/ **50002** 結果・PONG（PC→Quest）。
- **自動検出のしくみ**: サーバが 1 秒ごとに `b"VPTSRV1" + uint16(データポート)` を `:50001` へブロードキャスト → Quest（`UDPManager`, `autoDiscovery=true`）が**パケットの送信元 IP** を PC の IP として自動採用 → そこへユニキャスト送信する。学習した IP は PlayerPrefs に保存され、次回起動時はビーコンを待たずに使われる。
- AP がブロードキャストを遮断する環境（大学 WiFi 等）では届かない → Quest の `UDPManager.manualIPAddress` に PC の IPv4 を固定する（自動検出より優先。ユニキャストは通る）。切り分け手順は `../unity/OperationGuide.md` §6。
- ベストエフォート送信。チャンクを取りこぼしたキーフレームは破棄して次のキャプチャを待つ（再送はしない）。
- LAN 内・非暗号化（研究用途）。

---

## パケットプロトコル

Quest 側 `UDPManager.cs` / `KeyframeUploader.cs` と一致していること。13 バイトヘッダ ＋ ペイロード:

```
[ 'V','P', type(1B), reqId(4B LE), idx(2B LE), total(2B LE), len(2B LE) ] + payload[len]
```

| type | 方向 | 内容 |
|---|---|---|
| 1 META | Quest→PC | UTF-8 JSON。`w,h`（送信画像）/ `sw,sh`（K の基準＝センサ解像度）/ `fx,fy,cx,cy` / `px,py,pz`（カメラ位置）/ `qx,qy,qz,qw`（回転・Unity 左手系）/ `ts` / `n`（IMG チャンク数）/ `len`（JPEG 総バイト数） |
| 2 IMG | Quest→PC | JPEG を分割したチャンク。`idx`/`total` で順序・個数を示す |
| 3 RESULT | PC→Quest | 解いた Sim(3) の JSON |
| 4 RESET | Quest→PC | 試行リセット（`{trial,label,mode}`）。取りこぼし対策で**同じ内容を 3 回**送るので、`reqId` を resetId として重複を無視する |
| 5 PING / 6 PONG | 双方向 | 疎通確認。Quest が 2 秒ごとに PING、PC が即 PONG。`reqId` に seq を載せて往復させ RTT を取る |
| 7 EVENT | Quest→PC | 実験ログ 1 行。PONG の `ev_ack` で受領確認し、未達分を Quest が再送する |

PONG の payload: `{"seq","session","kf","cv_alive","cv_age","cv_scene","cv_window","ev_ack",…}`

- **なぜ PING/PONG が要るか**: Quest 側が「送信先 IP を知っている」ことと「サーバが動いている」ことは**別物**。IP は前回起動時の学習値が PlayerPrefs に残り、手動設定もできるため、`run.py` を起動していなくても送信先は埋まる。往復を確認しない限り疎通判定にならない。
- **`cv_alive`**: WSL2 の `localize.watch` がセッションフォルダへ 2 秒ごとに書く `cv_heartbeat.json` の鮮度（既定 8 秒で失効）。`run.py` だけ動いていて CV が未起動だと、キーフレームは保存されるのに Sim(3) が永遠に返らない。外からは「位置合わせが失敗している」ようにしか見えないので、Quest へ伝えて試行を始める前に止められるようにしている。
- 副次的な効果として、キーフレームを 1 枚も送っていない段階で PC が Quest の IP を学習できる。

`keyframe.K_for_image()` は送信画像サイズ `(w,h)` に合わせて K をスケールする（正方ピクセル＋中央縦クロップ前提）。**CV はこちらを使う。**

---

## シーンデータの配置（COLMAP）

3DGS を作ったときの COLMAP 出力一式を `scene_data/<name>/` に置く。再ローカライズ DB に使うのは **undistort 後の `sparse/0`（PINHOLE・`.ply` と同座標系）＋ `images/`**。

```
scene_data/example1/
├─ sparse/0/{cameras,images,points3D}.bin   ← 使用（PINHOLE・3DGS と同座標系）
├─ images/*.jpg                              ← 使用（undistort 済み DB 画像）
├─ distorted/{database.db, sparse/0}         ← 元 COLMAP（OPENCV 歪み・SIFT）。任意
└─ input/                                    ← 元入力画像。任意
```

レガシー COLMAP モデル（`rigs`/`frames` を持たない古い形式）は `migrate_model.py` で新形式へ移行してから使う（未移行だと `.rigs` アクセスで落ちる）。

---

## CV パイプライン

**座標系（最重要）**: aras-p UnityGaussianSplatting は **PLY 点座標を verbatim（軸反転なし）で読む**（ソース確認済み）。よって 3DGS モデル座標の数値 = COLMAP 座標の数値。COLMAP は右手系・Unity は左手系なので、モデル→ワールドの整合には**反射＝負の一様スケール**が要る（実機の手動合わせでも `scale ≈ -0.957` と負値で成立）。Sim(3) は**カメラ中心だけ**で解く（中心は世界座標上の点で、カメラ軸の規約に依らないため）。

1. **オフライン（シーンごと 1 回・GPU）**: `sparse/0` ＋ `images/` から再ローカライズ DB を構築。DB 画像に SuperPoint を抽出 → 既存 COLMAP 姿勢で SuperPoint 点群を**再三角測量**（`.ply` と同座標系）→ NetVLAD 大域記述子。出力は `scene_data/<name>/hloc/`。
2. **オンライン（キーフレームごと）**: 受信 JPEG → SuperPoint → NetVLAD 検索 → LightGlue マッチ → 2D-3D → `solvePnPRansac`（`K_for_image`）→ `T_{colmap←cam}`、カメラ中心 `C_c`。
3. **集約**: 複数 KF の (`C_c`, Quest world 位置 `C_w`) を `solve_sim3_ransac` で Sim(3) 化（**反射固定 `handedness=-1`** → 負スケール）。
4. **返送**: WSL2 の `localize` が `session/result_sim3.json` を書く → Windows の `run.py` が検知して type=3 を Quest:50002 へ返送 → `AlignmentManager.ApplySim3` が splatRoot に適用。

### 品質ゲート（`cv/gates.py`）

**残差が小さいことは解が正しいことを意味しない。** 共面配置（頭の高さを変えずに歩くと起きる）では反射を掛けた解が同じ残差を与え、共線配置（一直線に歩く）では直線まわりの回転が拘束されない。対策は**反射の固定**と**配置ゲート**で、閾値の締め方では解決しない。

| キー | 既定 | 意味 |
|---|---|---|
| `min_inliers` | 30 | KF 採否（PnP インライア下限）|
| `min_inlier_ratio` | 0.40 | 解の公開に必要なインライア率 |
| `max_rmse` | 0.08 m | world 残差 RMSE の上限 |
| `scale_range` | [0.5, 2.0] | \|s\| の緩い妥当域 |
| `min_baseline_m` | 1.50 m | カメラ中心の最大間隔（その場で首を振っただけを弾く）|
| `min_second_extent_m` | 0.40 m | 第 2 主成分の広がり（一直線を弾く）|
| `expected_scale` | None | シーン既知スケール。**評価実験では使わない**（正しさには不要で、主張が弱くなる）|
| `handedness` | -1 | 反射固定（0 で自由推定）|

**PC 側ゲートは「明らかにおかしい解」を落とすだけ**で、正しさの判定は Quest 側の連続一致が担う。PC 側を厳しくすると候補が一つも届かず、一致判定まで到達せずロックしなくなる。設計上の根拠は `../unity/Specification.md` §6。

---

## 評価実験の解析

```powershell
# 1) まず原点が動いた試行を落とす。ここを飛ばすと「よく合った解」に見えたまま数 m ずれる
python analysis\check_tracking.py received\session_20260802_14[34]*

# 2) 主結果。--run はその実験の Quest ログ ID（received/quest_events.jsonl の run 欄）
python analysis\experiment_report.py --received received --run exp_20260802_141845 --outdir analysis_out

# 3) 測量そのものの健全性（先端校正の流用・Virtual CP の編集など）
python analysis\analyze_experiments.py received\quest_events.jsonl --outdir analysis_out

# 4) 論文に貼る図（PDF・ベクタ）
python analysis\paper_figure.py --indir analysis_out --outdir <図の出力先>
```

**`--run` を省かないこと。** `received/quest_events.jsonl` は正本なので過去の実験が全部入っており、一方 `experiment_report` は label ごとに**最新の** session を採る。絞らないと過去 run の `trial-01` まで最新の session に紐づき、当時の測量（別の `local`）を引いて数 m の誤差の行として並ぶ。

`experiment_report` の出力: `curve_aggregate.csv`（曲線）/ `curve_per_trial.csv`（試行ごと）/ `cp_errors.csv`（CP × 試行の誤差。図と本文が同じ数字を使うためのもの）。

---

## テスト

```bash
python -m tests.test_geometry          # Umeyama / Sim(3) / クォータニオン（numpy だけで通る）
python -m tests.test_gates             # 品質ゲートの純粋関数
python -m tests.test_alignment_gates   # 退化（共面・共線）と反射固定の効果
python -m tests.test_liveness          # PING/PONG・CV ハートビート
python -m tests.test_events            # type=7 EVENT の振り分け・重複除去・CP JSON 書き出し
```

`test_liveness.py` と `test_events.py` は **C# のソースを読んで定数名・鍵名の一致を機械的に検査する**。プロトコルを変えたときに片側だけ直す事故を防ぐため。hloc/pycolmap を使う部分は WSL2 実機でしか確認できない。

---

## 構成

```
server/
├─ run.py                    エントリポイント（UDP 受信 → 再構成 → 保存 ＋ ビーコン ＋ Sim(3) 返送 ＋ GUI）
├─ test_send.py              疎通テスト用のダミー送信（Quest 不要）
├─ rigs_probe.py             pycolmap の .rigs クラッシュを切り分ける最小再現スクリプト
├─ requirements.txt          受信・保存のコア依存（numpy / opencv / customtkinter）
├─ requirements-cv.txt       CV 依存（pycolmap==3.13.0 ほか。torch と hloc は別途）
├─ received/                 保存先（自動生成。1 セッション = 1 Quest ワールド座標系）
├─ scene_data/<name>/        COLMAP 一式（sparse/0 + images/ を DB に使用）
├─ analysis/
│  ├─ check_tracking.py      原点が動いた試行の検出（**曲線を引く前に必ず通す**）
│  ├─ experiment_report.py   主結果。収集時間 vs CP 誤差の曲線 / CP 別内訳 / 試行間の再現性
│  ├─ accuracy_vs_time.py    1 試行ぶんの曲線（experiment_report が内部で使う）
│  ├─ analyze_experiments.py 測量の健全性・先端校正・収束の要約
│  ├─ paper_figure.py        上記 CSV → 論文用のベクタ図（段幅に合わせる）
│  └─ tune_gates.py          ゲート閾値の掃引（歩き方ごとのロック率・誤差の模擬）
├─ tests/                    純粋計算・プロトコル整合のテスト（上記）
└─ vptserver/
   ├─ protocol.py            パケット定義と parse()
   ├─ reassembler.py         reqId ごとにチャンクを集約 → Keyframe
   ├─ udp_receiver.py        UDP 受信ループ（timeout で Ctrl-C 対応）
   ├─ keyframe.py            Keyframe（K / 姿勢 / 画像）データクラスと K_for_image()
   ├─ storage.py             jpg + json 保存
   ├─ beacon.py              サーバ存在通知ビーコン（Quest の自動検出用）
   ├─ gui.py                 customtkinter の GUI（未導入ならヘッドレスで動く）
   └─ cv/
      ├─ colmap_model.py     COLMAP sparse(.bin) リーダ（numpy のみ）
      ├─ geometry.py         qvec→R / カメラ中心 / Umeyama（反射固定可）/ Sim(3) / RANSAC
      ├─ gates.py            品質ゲート（純粋関数）
      ├─ migrate_model.py    レガシー COLMAP モデルを新形式（rigs/frames 付き）へ移行
      ├─ hloc_db.py          オフライン DB 構築（SuperPoint 再三角測量 ＋ NetVLAD, GPU）
      └─ localize.py         オンライン・ローカライズ（検索 → マッチ → PnP）＋ 自己テスト ＋ Sim(3)
```

GUI（customtkinter のネイティブウインドウ）には、Quest の接続状態・受信数・現在のセッション・最新 Sim(3)（scale/inliers/rmse）・イベントログが出る。設定の編集は即座に `cv.json` へ反映される。customtkinter が入っていなければヘッドレスで動く。

`received/` `scene_data/` `analysis_out/` `Hierarchical-Localization/` `.venv/` は `.gitignore` 済み。
