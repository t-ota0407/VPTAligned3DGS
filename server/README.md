# VPTAligned3DGS — PC オフロードサーバ

Quest 3 から UDP で送られてくるキーフレーム（パススルーカメラ画像 ＋ 内部行列 K ＋ カメラのワールド姿勢 ＋ 時刻）を受信し、**COLMAP 再構成に対して再ローカライズして Sim(3) を解き、Quest へ返す**。

**重い CV は WSL2(Ubuntu)+CUDA で動かす。** UDP の受信・返送・GUI は Windows の `run.py` が担当し、両者は共有フォルダ（`/mnt/c`）経由で疎結合に動く。

> pycolmap は Windows ネイティブで `reconstruction.rigs` にアクセスした時点で access violation で落ちる（pycolmap 4.0.4 / 3.13.0 の両方、numpy のバージョン・torch の同居に依らず再現）。Linux では安定するため、hloc/pycolmap を使う処理だけを WSL2 に置いている。

関連文書: 全体の入口 [`../README.md`](../README.md) / 設計 [`../unity/Specification.md`](../unity/Specification.md) / 実装仕様 [`../unity/ImplementationSpec.md`](../unity/ImplementationSpec.md) / 運用手順 [`../unity/OperationGuide.md`](../unity/OperationGuide.md)

---

## セットアップ（PC ごとに一度）

Quest と**同じ LAN** にある Windows 10/11 の PC（**NVIDIA GPU 推奨**）で、**`server\setup_windows.bat` をダブルクリック**する。何度実行しても安全（足りないものだけを入れる）。初回は 10〜30 分ほどかかり、WSL 側で数 GB（CUDA 版 torch など）をダウンロードする。

| 段 | やること |
|---|---|
| 1 | Python 3.9 以上を探す（無ければ winget で Python 3.12 を入れるか尋ねる）→ `server\.venv` を作り `requirements.txt` を入れる。壊れた `.venv`（元の Python を消した等）は作り直す |
| 2 | WSL2 と CV 用のディストリを決める。CV 環境（`~/vptcv`）のあるディストリをそのまま使い、無ければ **Ubuntu-24.04** に作る（それも無ければ `wsl --install -d Ubuntu-24.04` を尋ねる。初回は Windows の再起動と Ubuntu のユーザー作成が要る）。WSL1 なら WSL2 へ変換する（WSL1 は GPU を使えない） |
| 3 | そのディストリで `setup_wsl.sh` を実行: OS パッケージ（python3-venv / git / libgl1 / glib。**sudo のパスワードを聞かれる**）→ venv → torch（GPU が見えれば CUDA 12.4 版）→ CV 依存 → LightGlue と hloc → 動作確認と**重みの事前取得** |
| 4 | 使うディストリを `launch_config.json` に記録する（`run.py` の CV 自動起動が読む） |
| 5 | ファイアウォール: Quest → PC の UDP 50000 を許可する規則を作る（管理者の許可を求める）。ネットワークが**「パブリック」**ならパブリックでも許可するかを尋ねる。Python を遮断する規則があれば無効にする（→「ネットワーク」） |
| 6 | `scene_data/` のシーンを調べ、再ローカライズ DB の無いものはその場で作るかを尋ねる |

- **版の固定**: WSL 側は動作確認済みの組み合わせを入れる — Ubuntu 24.04 / Python 3.12 / torch 2.6.0+cu124・torchvision 0.21.0 / hloc `c13273b`（1.5）/ LightGlue `eb42fee` / その他は `constraints-cv.txt`（pycolmap==3.13.0・numpy 2.4 等）。公開後に依存の新版が出ても同じ組み合わせを再現するため。Python 3.12 以外のディストリでは制約を外して入れる（警告が出る）。版を上げるときは `setup_wsl.sh` の先頭と `constraints-cv.txt` を同時に直す。
- **重みの事前取得**: hloc は初回の使用時に NetVLAD・LightGlue の重みをネットから取る。インターネットの無い展示会場で初めて動かすとそこで止まるので、セットアップの最後（`python -m vptserver.cv.check_env`）で取得しておく（`~/.cache/torch/hub`。SuperPoint は hloc に同梱）。
- **GPU**: Windows 側に NVIDIA のドライバがあれば WSL2 から CUDA が使える（WSL 内に CUDA を入れる必要は無い）。GPU が無くても動くが、1 枚に十数秒以上かかり位置合わせに何分もかかる。ドライバを後から入れたら `setup_windows.bat` を再実行すると CUDA 版の torch に入れ替わる。
- `setup_windows.bat` の引数は `python -m vptserver.setup_windows` へ渡る（`--distro <名前>` / `--skip-firewall` / `--skip-wsl` / `--cv-venv <場所>`）。

### 手動でセットアップする場合（参考）

```powershell
# Windows（server フォルダで）
py -3 -m venv .venv
.venv\Scripts\python -m pip install -r requirements.txt
```

```bash
# WSL2（setup_windows.bat が呼ぶものと同じ。再実行しても安全）
bash /mnt/c/<repo への WSL2 パス>/server/setup_wsl.sh
#   環境変数: VPT_CV_VENV（既定 ~/vptcv）/ VPT_HLOC_DIR（既定 ~/hloc）/ VPT_TORCH_CPU=1（CPU 版 torch）
```

---

## シーン DB の構築（シーンごとに一度）

`scene_data/<name>/` に COLMAP 一式を置き（→「シーンデータの配置」）、**`build_scene_db.bat` をダブルクリック**する（DB の無いシーンをすべて。`build_scene_db.bat studio` で指定）。WSL2 の GPU で 1 シーン数分〜数十分。中身は WSL2 での次と同じ:

```bash
python -m vptserver.cv.hloc_db --scene scene_data/<name>
```

---

## 実行

| 用途 | ダブルクリック | 保存先 |
|---|---|---|
| 評価実験 | `start_experiment.bat` | `received/` |
| デモ展示 | `start_demo.bat` | `received_demo/` |

**`run.py` が WSL2 の CV（`localize`）を自動で起動・終了する**（`vptserver/cv_launcher.py`）。WSL2 のターミナルを開く必要はない。

- `localize` の監視先（`--watch`）は `run.py` の `--out` から決まる。以前は 2 つのターミナルで手で合わせる必要があり、食い違うとキーフレームが届くのに位置合わせが永遠に返らなかった。
- CV の出力は `run.py` のコンソールに `[cv]` 付きで流れ、`<out>/cv.log` にも残る。GPU を使えないときは起動時に `CUDA: NOT available` と出る。
- **ウインドウを閉じると `localize` も終わる**（GUI を閉じる・コンソールの ×・Ctrl-C）。`run.py` が強制終了されても、`localize` は標準入力の EOF を見て自分で終わる（`--exit-on-stdin-eof`・`cv/lifecycle.py`）。
- `localize` が落ちたら 5 秒後に起動し直す。起動直後に 3 回続けて落ちたら設定の問題とみなして諦め、理由を出す（ログは `<out>/cv.log`）。
- 同じ保存先を別の `localize`（手で起動したもの）が処理中なら、それを使い、終わったら引き継ぐ（ベースの生存信号で判定）。
- シーンが決まらない（DB 付きのシーンが複数あり `--scene` も無い）ときは、GUI でシーンを選んだ時点で起動する。
- ディストリは `launch_config.json`（`setup_windows.bat` が書く）→ CV 環境のあるものを自動で探す、の順で決まる。

`.bat` の引数は `run.py` へ渡る（例: `start_demo.bat --scene scene_data/studio`）。コンソールから直接動かすなら `.venv\Scripts\python run.py --out received`。

`run.py` のオプション:

```bash
python run.py --verbose            # 届いた全データグラムの送信元 IP / サイズを表示（疎通確認）
python run.py --no-beacon          # ビーコンを止める（手動 IP 運用）
python run.py --scene scene_data/example2 --every 5 --min 5
python run.py --port 50000 --beacon-port 50001 --out received
python run.py --no-cv              # CV を自動起動しない（WSL2 で手で起動する。下記）
python run.py --lang ja            # GUI を日本語で開く（既定: 前回 GUI で選んだもの → 英語）
python run.py --mode demo          # GUI に出す「評価実験 / デモ展示」（既定: 保存先の名前から。received_demo → デモ）
python run.py --wsl-distro Ubuntu-24.04 --cv-venv ~/vptcv   # ディストリ・venv を指定（launch_config.json に記録）
```

`--no-cv` のときの WSL2 側（`--watch` は `run.py` の `--out` と同じ名前にする）:

```bash
source ~/vptcv/bin/activate && cd /mnt/c/<repo への WSL2 パス>/server
python -m vptserver.cv.localize --watch received
```

**設定は `run.py` 側に集約される。** `--scene`/`--every`/`--min` は `session/cv.json` に書かれ、WSL2 の `localize` がそれを読む（ライブ反映）。`scene_data/` にビルド済みシーンが 1 つなら scene の指定も要らない（自動検出）。GUI からも編集できる。

### 運用契約: 1 回の `run.py` 実行 ＝ 1 セッション

起動ごとに `received/session_<時刻>/` を作って保存し、WSL2 は最新を自動追従する。**手動クリアは不要**で、セッションが自動的に分離される（別座標系の混入や reqId 衝突を回避）。Sim(3) はそのセッションの Quest ワールド座標系が基準。跨セッションの持ち越しは Quest 側の空間アンカーが担う。

評価実験では、Quest から **type=4 RESET** が届くと `run.py` と Quest アプリを再起動せずに新しいセッションへ切り替わる。

---

## ネットワーク

- PC のファイアウォールで **UDP 50000（データ）の受信**を許可する。`setup_windows.bat` が規則「VPTAligned3DGS (UDP 50000 in)」を作る。
  - **「パブリック」のネットワークで詰まりやすい**（会場・学内の Wi-Fi はパブリック扱いが多い）。初回のダイアログで既定のまま「許可」を押すとプライベートだけが許可され、パブリックには**ブロック規則**が作られる。ブロックは許可より優先されるので、規則を足しても届かない。`setup_windows.bat` はパブリックでの許可を尋ね、Python のブロック規則を無効にする。届かなくなったら `setup_windows.bat` を再実行する。
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

PONG の payload: `{"seq","session","kf","cv_alive","cv_age","cv_scene","cv_window","cv_ok","cv_done","cv_busy","cv_on","ev_ack",…}`

- **なぜ PING/PONG が要るか**: Quest 側が「送信先 IP を知っている」ことと「サーバが動いている」ことは**別物**。IP は前回起動時の学習値が PlayerPrefs に残り、手動設定もできるため、`run.py` を起動していなくても送信先は埋まる。往復を確認しない限り疎通判定にならない。
- **`cv_alive`**: WSL2 の `localize.watch` がセッションフォルダへ 2 秒ごとに書く `cv_heartbeat.json` の鮮度（既定 8 秒で失効）。`run.py` だけ動いていて CV が未起動だと、キーフレームは保存されるのに Sim(3) が永遠に返らない。外からは「位置合わせが失敗している」ようにしか見えないので、Quest へ伝えて試行を始める前に止められるようにしている。
- **`kf` / `cv_ok` / `cv_done`**: このセッションで `run.py` が受け取った枚数 / CV が採用した枚数 / CV が処理し終えた枚数（採否を問わず解き直しまで済んだ分。ハートビートの `done`）。Quest のデモ展示は、採用枚数で収集を区切り、`cv_done ≥ kf` かつ処理中でないことで「PC が全部処理し終えた」と判定する（`unity/ImplementationSpec.md` §3.3・§14.4）。
- **生存信号は別スレッドが書く**（`vptserver/cv/heartbeat.py`）。hloc の処理中も 2 秒ごとに更新されるので、PC が遅くても「処理中」と「停止」を取り違えない。セッションのフォルダには進み具合、`--watch` のベースのフォルダには生存と処理中のセッション名を書く。
- **`cv_on`**: このセッションにまだハートビートが無いとき（Quest の RESET 直後、CV が前のセッションの処理を終えていない）、ベースのハートビートから「CV が今処理しているセッション名」を入れる。Quest はこれで「止まった」ではなく「片付け中」と判断して待つ。このとき枚数（`cv_ok` 等）は載せない（前のセッションの枚数と取り違えさせない）。
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
   **モデル（SuperPoint・NetVLAD・LightGlue）と DB（大域記述子・局所特徴）は起動時に読み込んだまま使い回す**（`cv/warm_hloc.py`）。hloc の main 関数は呼ぶたびにこれらを読み直すため、届いた分をまとめて処理する 1 回ごとに約 10 秒の固定費があった（RTX 3070 Ti Laptop・irt_short2 で、1 枚 14.1 s → 2.4 s、4 枚 18.9 s → 9.4 s）。残る 1 枚あたり約 2.4 s はほぼ LightGlue の照合（1 枚あたり検索上位 20 枚）。
   計算は hloc と同じ（前処理・float16 への丸め・検索順・照合の入力の形まで合わせてある）。**`warm_hloc.py` か hloc の版を変えたら、WSL2 で `python -m vptserver.cv.check_warm --scene scene_data/<name> --keyframes received/session_<…>` を実行し、`ALL SAME` を確かめる**（検索ペア・照合・PnP のインライア数・推定姿勢を突き合わせる）。準備や処理に失敗したときは、警告を出して hloc の main を呼ぶ以前の経路に切り替える（`VPT_CV_PLAIN_HLOC=1` で常に以前の経路）。
3. **集約**: 複数 KF の (`C_c`, Quest world 位置 `C_w`) を `solve_sim3_ransac` で Sim(3) 化（**反射固定 `handedness=-1`** → 負スケール）。
4. **返送**: WSL2 の `localize` が `session/result_sim3.json` を書く → Windows の `run.py` が検知して type=3 を Quest:50002 へ返送 → `AlignmentManager.ApplySim3` が splatRoot に適用。

### 品質ゲート（`cv/gates.py`）

**残差が小さいことは解が正しいことを意味しない。** 共面配置（頭の高さを変えずに歩くと起きる）では反射を掛けた解が同じ残差を与え、共線配置（一直線に歩く）では直線まわりの回転が拘束されない。対策は**反射の固定**と**配置ゲート**で、閾値の締め方では解決しない。

| キー | 既定 | 意味 |
|---|---|---|
| `min_inliers` | 30 | KF 採否（PnP インライア下限）|
| `min_inlier_ratio` | 0.40 | 解の公開に必要なインライア率 |
| `max_rmse` | 0.08 m | world 残差 RMSE の上限 |
| `scale_range` | [1e-3, 1e3] | \|s\| の**健全性**のみ（発散・NaN を落とす）。`--scale-range LO HI`。**シーンの実スケール付近に締めないこと** — COLMAP の 1 unit が何 m かは再構成ごとに違う（`ImplementationSpec.md` §5.5）|
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
python -m tests                        # 全部（1 本ずつ別プロセスで実行し、落ちたものだけ出力を出す）

python -m tests.test_geometry          # Umeyama / Sim(3) / クォータニオン（numpy だけで通る）
python -m tests.test_gates             # 品質ゲートの純粋関数
python -m tests.test_alignment_gates   # 退化（共面・共線）と反射固定の効果
python -m tests.test_liveness          # PING/PONG・CV ハートビート
python -m tests.test_events            # type=7 EVENT の振り分け・重複除去・CP JSON 書き出し
python -m tests.test_heartbeat         # CV の生存信号（処理中も別スレッドで更新・前のセッションを処理中なら cv_on）
python -m tests.test_cv_launcher       # CV の自動起動（wsl.exe の出力・日本語/空白のパス・終了の連動・再起動の方針）
python -m tests.test_setup_windows     # セットアップの判断（ディストリの選び方・DB の状態・ファイアウォール）
```

`test_liveness.py` と `test_events.py` は **C# のソースを読んで定数名・鍵名の一致を機械的に検査する**（パケットの type 定数とヘッダ長は `VpPacket.cs`、PONG の鍵名は `UDPManager.cs`）。プロトコルを変えたときに片側だけ直す事故を防ぐため。hloc/pycolmap を使う部分と、WSL・ファイアウォールを実際に操作する部分（`cv_launcher` の起動・終了、`setup_windows` / `setup_wsl.sh`）は実機でしか確認できない。WSL 側の環境は `python -m vptserver.cv.check_env` で確かめられる。

---

## 構成

```
server/
├─ setup_windows.bat         初回のセットアップ（ダブルクリック。→ vptserver/setup_windows.py と setup_wsl.sh）
├─ start_experiment.bat      評価実験の起動（run.py ＋ CV の自動起動。保存先 received/）
├─ start_demo.bat            デモ展示の起動（同。保存先 received_demo/）
├─ build_scene_db.bat        シーンの再ローカライズ DB の構築（WSL2）
├─ scripts/run_server.cmd    start_*.bat の共通部分
├─ setup_wsl.sh              WSL2 の CV 環境の構築（版を固定。再実行しても安全）
├─ launch_config.json        この PC の WSL ディストリ・venv と GUI の言語（setup と GUI が書き、run.py が読む。.gitignore 済み）
├─ run.py                    エントリポイント（引数の解析と組み立てだけ。中身は vptserver/ の各モジュール）
├─ requirements.txt          受信・保存のコア依存（numpy / opencv / customtkinter）
├─ requirements-cv.txt       CV 依存（pycolmap==3.13.0 ほか。torch と hloc は setup_wsl.sh が入れる）
├─ constraints-cv.txt        CV の動作確認済みの版（setup_wsl.sh が pip の制約に使う）
├─ received/                 保存先（自動生成。1 セッション = 1 Quest ワールド座標系）
├─ scene_data/<name>/        COLMAP 一式（sparse/0 + images/ を DB に使用）
├─ analysis/
│  ├─ check_tracking.py      原点が動いた試行の検出（**曲線を引く前に必ず通す**）
│  ├─ experiment_report.py   主結果。収集時間 vs CP 誤差の曲線 / CP 別内訳 / 試行間の再現性
│  ├─ accuracy_vs_time.py    1 試行ぶんの曲線（experiment_report が内部で使う）
│  ├─ analyze_experiments.py 測量の健全性・先端校正・収束の要約
│  ├─ paper_figure.py        上記 CSV → 論文用のベクタ図（段幅に合わせる）
│  └─ tune_gates.py          ゲート閾値の掃引（歩き方ごとのロック率・誤差の模擬）
├─ tests/                    純粋計算・プロトコル整合のテスト（上記。共通の check() は tests/_check.py）
├─ tools/test_send.py        疎通テスト用のダミー送信（Quest 不要。`python tools/test_send.py 127.0.0.1`）
└─ vptserver/
   ├─ dispatch.py            データポートに届くパケットの振り分け（キーフレーム / PING→PONG / RESET / 実験イベント）
   ├─ session.py             セッションフォルダの切替・cv.json の書き出し・Sim(3) の Quest への返送
   ├─ liveness.py            PONG の中身（CV の生存信号の読み取り。古さはファイルの更新時刻で測る）
   ├─ events.py              実験イベント（type=7）の保存・model 側 CP の JSON への組み直し
   ├─ state.py               GUI とコンソールが読む共有状態
   ├─ scenes.py              DB ができているシーンの判定と DB の設定名（Windows 側と WSL2 側の両方が使う）
   ├─ protocol.py            パケット定義と parse()（Quest 側は VpPacket.cs）
   ├─ reassembler.py         reqId ごとにチャンクを集約 → Keyframe
   ├─ udp_receiver.py        UDP 受信ループ（timeout で Ctrl-C 対応）
   ├─ keyframe.py            Keyframe（K / 姿勢 / 画像）データクラスと K_for_image()
   ├─ storage.py             jpg + json 保存
   ├─ beacon.py              サーバ存在通知ビーコン（Quest の自動検出用）
   ├─ gui.py                 customtkinter の GUI（描画だけ。未導入ならヘッドレスで動く）
   ├─ gui_status.py          GUI に出す判定（総合ステータス・CV の行・枚数。純粋関数）
   ├─ i18n.py                GUI の文言（英語・日本語。既定は英語）
   ├─ cv_launcher.py         WSL2 の localize の起動・監視・終了（ディストリの検出・パスの変換）
   ├─ setup_windows.py       初回のセットアップ（WSL・ファイアウォール・シーン DB）
   └─ cv/
      ├─ check_env.py        CV 環境の確認と重みの事前取得（setup_wsl.sh の最後）
      ├─ heartbeat.py        CV の生存信号（別スレッドで 2 秒ごと）
      ├─ lifecycle.py        起動元が終わったら localize も終わる（標準入力の EOF）
      ├─ colmap_model.py     COLMAP sparse(.bin) リーダ（numpy のみ）
      ├─ geometry.py         qvec→R / カメラ中心 / Umeyama（反射固定可）/ Sim(3) / RANSAC
      ├─ gates.py            品質ゲート（純粋関数）
      ├─ migrate_model.py    レガシー COLMAP モデルを新形式（rigs/frames 付き）へ移行
      ├─ hloc_db.py          オフライン DB 構築（SuperPoint 再三角測量 ＋ NetVLAD, GPU）
      ├─ localize.py         オンライン・ローカライズ（検索 → マッチ → PnP）＋ 自己テスト ＋ Sim(3)
      ├─ warm_hloc.py        localize の高速経路: モデルと DB を読み込んだまま hloc と同じ計算をする
      └─ check_warm.py       高速経路が hloc の main を呼ぶ経路と同じ結果を出すかの突き合わせ（WSL2）
```

### GUI

`run.py` のウインドウ（customtkinter）だけで「準備できたか・止まっていないか・なぜ確定しないか・次に何をするか」が分かるようにしてある。コンソールの `[cv]` の行は詳しく調べるときだけ見ればよい。customtkinter が入っていなければヘッドレスで動く。

| 場所 | 出るもの |
|---|---|
| ヘッダ | **評価実験（青）/ デモ展示（紫）**の札と言語の切替。札は保存先の名前で決まる（`start_experiment.bat` → `received` → 実験、`start_demo.bat` → `received_demo` → デモ。`--mode` で上書き）。窓の題名にも出る |
| 一番上（総合ステータス） | いま一番大事な 1 行と次にやること。赤＝CV を起動できない（`Retry`・`Open log`）/ 琥珀＝人の操作が要る（シーン未選択・Quest 未接続・CPU で動作）/ 青＝待てばよい（CV 起動中・処理中・前のセッションの片付け）/ 緑＝準備完了。判定の材料は Quest の HUD と同じ（PONG の CV 生存・PING）。Quest 未接続のときは、この PC の IPv4（Quest の Manual IP に入れる値）も出る |
| STATUS | Quest（**PING で生存を見る**ので、キーフレームを送っていない試行前・デモの表示中も「接続中」と出る）/ CV（動作中・ディストリ・GPU か CPU か・処理中の枚数）/ セッション |
| THIS SESSION | 受信・処理済み・採用・待ちの枚数（待ちが減らずに増え続けるなら CV が追いついていない）/ **直近の解の採否と理由**（公開ゲートで落ちた解も `✕ rejected — rmse=…` の形で出る）/ 最後に Quest へ返送した Sim(3)（反射固定で scale は常に負が正常なので、大きさ `\|scale\|` で出す） |
| SETTINGS | scene / every / min。変更は即 `cv.json` へ反映。シーンが決まっていないときは「(select a scene)」を琥珀で出す（CV はシーンを選んだ時点で起動する）。**キーフレームが届いた後にシーンを変えようとすると確かめる**（`localize` は DB を読み直し、そのセッションの途中経過を捨てるため。Quest で試行の開始・Re-align をすれば新しいセッションになる） |
| EVENTS | 重要度で色分け（赤＝エラー / 琥珀＝警告・ゲート不合格 / 緑＝準備完了・Sim(3) の返送 / 青＝RESET・試行の区切り）。**`Important`**（既定）は覚えておくべき行だけ、**`All`** は CV の出力をすべて（キーフレームごとの採否・hloc の進み具合。キーフレームの受信は 1 行にまとめて書き換える）。新しい行は下に足し、いちばん下を見ているときだけ送る（上へ戻って読んでいる間は動かない）。下の `Open folder` / `Open cv.log` で保存先と CV のログを開く |
| 下の 1 行 | 保存先の絶対パス・この PC の IPv4・ポート・稼働時間 |

**閉じるとき**、CV が処理中・待ちがある、またはキーフレームが 30 秒以内に届いている（試行・再位置合わせの途中）なら確かめる。閉じると CV（`localize`）も止まるため。

CV の行の重要度は `localize` の出力から決める。`localize` は警告以上の行に `[localize] WARNING:` / `[localize] ERROR:` を付けて出す（`cv/localize.py` の `_LevelFormatter`。INFO は今までどおり `[localize] …`）。`cv_launcher.cv_event_level` がそれと hloc の `… WARNING]`・Python の例外を見て色を決める。

**言語**は既定が英語で、右上で日本語に切り替えられる。選んだ言語は `launch_config.json` の `gui_lang` に残る（`--lang en|ja` が優先）。窓の大きさは画面の作業領域に合わせる（表示倍率 150% のノート PC でも収まるよう、総合ステータスの下を 2 列にしている）。

`received/` `received_demo/` `scene_data/` `analysis_out/` `Hierarchical-Localization/` `.venv/` `launch_config.json` は `.gitignore` 済み。

**`.bat` / `.cmd` は ASCII だけで書き、CRLF で取り出す**（`.gitattributes`）。cmd.exe は LF だとラベルへのジャンプを外すことがあり、非 ASCII はコード ページで化ける。また `( )` のブロックの中でパスを展開しない（`VPT (1)` のような括弧入りのフォルダ名でブロックが壊れる。ZIP を二度展開するとよく付く）。日本語の案内は Python 側で出す。**`setup_wsl.sh` は LF**（CR が混ざると bash が落ちる。念のため setup は LF に直した一時ファイルを実行する）。
