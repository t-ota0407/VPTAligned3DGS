# VPTAligned3DGS オフロードサーバ（PC側）

Quest 3 から UDP で送られてくるキーフレーム（パススルーカメラ画像＋内部行列 K＋カメラのワールド姿勢＋時刻）を受信・再構成し、`received/` に保存する。
起動中は LAN にビーコンを流し、**Quest 側が送信先 IP を自動検出**できるようにする（IP を手入力して再ビルドする必要がない）。
将来はここに自動位置合わせの CV（特徴抽出 → 3DGS レンダ画像とのマッチ → PnP → 相似変換 Sim(3)）を実装し、求めた変換を Quest へ返す。

## セットアップ（venv）

Quest と **同じ LAN** にある PC で実行する。

```bash
cd server
python -m venv .venv

# Windows (PowerShell)
.venv\Scripts\Activate.ps1
# Windows (cmd)
.venv\Scripts\activate.bat
# macOS / Linux
source .venv/bin/activate

pip install -r requirements.txt
```

## 実行

```bash
python run.py                    # data=:50000 受信, beacon=:50001 送信, 保存先 ./received/
python run.py --verbose          # 届いた全データグラムの送信元IP/サイズを表示（疎通確認）
python run.py --no-beacon        # ビーコンを止める（手動IP運用にしたいとき）
python run.py --port 50000 --beacon-port 50001 --out received
```

**停止は Ctrl-C。** `stopping... / bye` と表示されて終了する（受信がアイドル中でも即停止する）。

## 自動検出のしくみ（IP 手入力＆再ビルド不要）

1. サーバは 1 秒ごとに `b"VPTSRV1" + uint16(データポート)` を `:50001` にブロードキャストする。
2. Quest 側（`UDPManager`, `autoDiscovery=true`）がこれを受信し、**パケットの送信元 IP を PC の IP として自動採用**、そこへキーフレームをユニキャスト送信する。
3. 学習した IP は Quest の PlayerPrefs に保存され、次回起動時はビーコンが来る前から即利用される。

→ PC の IP が変わっても、サーバを起動してヘッドセットを被るだけで再接続する。手動固定したい場合のみ Quest の `UDPManager.manualIPAddress` に IP を入れる（その場合は自動検出より優先）。

> Quest が同一サブネットにいることが前提。`--verbose` で `[rx] ...B from <QuestのIP>` が出れば経路 OK。

## ネットワーク注意

- PC のファイアウォールで **UDP 50000（データ）の受信**を許可すること（Windows は初回ダイアログで「許可」）。
- ビーコン送信はアウトバウンドなので通常は追加設定不要。
- ベストエフォート送信。チャンク取りこぼしがあるキーフレームは破棄し、次のキャプチャを待つ（再送は将来課題）。

## パケットプロトコル（Quest 側 `KeyframeUploader.cs` と一致）

13 バイトヘッダ + ペイロード:

```
[ 'V','P', type(1B), reqId(4B LE), idx(2B LE), total(2B LE), len(2B LE) ] + payload[len]
```

- `type=1` META: UTF-8 JSON。キー `w,h`（送信画像の幅/高さ）, `sw,sh`（K の基準＝センサ解像度）, `fx,fy,cx,cy`（K@sensor）, `px,py,pz`（カメラ位置）, `qx,qy,qz,qw`（カメラ回転, Unity 左手系）, `ts`（タイムスタンプ）, `n`（IMG チャンク総数）, `len`（JPEG 総バイト数）。
- `type=2` IMG: JPEG を分割したチャンク。`idx`/`total` で順序・個数を示す。
- `type=3` RESULT（PC→Quest, `:50002`）: 解いた Sim(3) の JSON。
- `type=5` PING（Quest→PC, `:50000`）/ `type=6` PONG（PC→Quest, `:50002`）: **疎通確認**。
  Quest が 2 秒ごとに PING を送り、PC が即 PONG を返す。`reqId` に seq を載せて往復させるので RTT が取れる。
  PONG の payload は `{"seq","session","kf","cv_alive","cv_age","cv_scene","cv_window"}`。
  - **なぜ必要か**: Quest 側が「送信先 IP を知っている」ことと「サーバが動いている」ことは別物。
    IP は前回起動時の学習値が PlayerPrefs に残り、手動設定もできるため、`run.py` を起動していなくても
    送信先は埋まる。往復を確認しない限り疎通判定にはならない。
  - **`cv_alive`**: WSL2 の `localize.watch` がセッションフォルダに書く `cv_heartbeat.json`（2 秒ごと）の
    鮮度で判定する。`run.py` だけ動いていて CV が未起動だと、キーフレームは保存されるのに Sim(3) が
    永遠に返らない。外からは「位置合わせが失敗している」ようにしか見えないので、Quest 側へ伝えて
    試行を始める前に止められるようにしている。
  - 副次的な効果として、キーフレームを 1 枚も送っていない段階で PC が Quest の IP を学習できる
    （従来はキーフレーム到着まで Sim(3) の返送先が分からなかった）。
- `type=4` RESET（Quest→PC）: **評価実験の試行リセット**。payload は JSON（`trial`, `label`, `mode`）。
  受け取ると `run.py` は新しい `received/session_<時刻>/` を切り、`cv.json` と `trial.json` を書く。
  WSL2 の `localize.watch` は最新セッションを追従するので、Cc/Cw の蓄積を自ら捨てる。
  UDP の取りこぼしに備えて Quest は同じ内容を 3 回送るため、ヘッダの `reqId` を resetId として
  使い、PC 側は同じ番号を無視する（重複でセッションが複数切られるのを防ぐ）。
  → **`run.py` を再起動せずに独立した試行を繰り返せる**（論文 E1 の K 試行・E2 の 10 試行）。

ビーコン（別系統）: `b"VPTSRV1" + uint16(データポート, LE)` を `:50001` にブロードキャスト。

`K_for_image()` は送信画像サイズ `(w,h)` に合わせて K をスケールするので、CV はこちらを使う。

## 自動位置合わせ CV（M3 step3, 実装中）

重い CV（特徴抽出・COLMAP 再ローカライズ・PnP・Sim(3)）は PC で行う。依存は別管理:

```bash
pip install -r requirements.txt -r requirements-cv.txt   # numpy/opencv/pycolmap（+任意で hloc/torch）
python -m tests.test_geometry                              # 純粋数学の検証（numpy だけで通る）
```

### シーンデータ配置（COLMAP）
3DGS を作ったときの COLMAP 出力一式を `scene_data/<name>/` に置く（例 `scene_data/example1/`）。
再ローカライズ DB に使うのは **undistort 後の `sparse/0`（PINHOLE・`.ply` と同座標系）＋ `images/`**。

```
scene_data/example1/
├─ sparse/0/{cameras,images,points3D}.bin   ← 使用（PINHOLE・3DGSと同座標系）
├─ images/*.jpg                              ← 使用（undistort 済み DB 画像 691枚）
├─ distorted/{database.db, sparse/0}         ← 元 COLMAP（OPENCV 歪み・SIFT）。任意
└─ input/                                    ← 元入力画像。任意
```

### 座標系の確定（重要）
aras-p UnityGaussianSplatting は **PLY 点座標を verbatim（軸反転なし）で読む**（ソース確認済み）。
よって 3DGS モデル座標の数値 = COLMAP 座標の数値。COLMAP は右手系・Unity は左手系なので、
モデル→ワールドの整合には **反射＝負の一様スケール** が要る（実機の手動合わせでも `scale≈-0.957`
と負値で成立）。`geometry.solve_sim3_to_unity` は反射を許す Umeyama で当てはめ、Unity の
localScale を **符号付き（負可）** にして反射を表現する。Sim(3) は**カメラ中心**だけで解く
（中心は世界座標上の点でカメラ軸規約に依らない）。

### パイプライン（採用: hloc / SuperPoint+LightGlue+NetVLAD）
1. **オフライン（シーンごと1回・GPU）**: `sparse/0`＋`images/` から再ローカライズ DB を構築。
   DB 画像に SuperPoint 抽出 → 既存 COLMAP 姿勢で SuperPoint 点群を**再三角測量**（`.ply` と同座標系）→ NetVLAD 大域記述子。
   ```bash
   pip install -r requirements.txt -r requirements-cv.txt
   pip install torch torchvision   # CUDA 版は pytorch.org 参照
   # hloc は PyPI に無い → GitHub からサブモジュール込みで導入
   git clone --recursive https://github.com/cvg/Hierarchical-Localization.git
   cd Hierarchical-Localization && python -m pip install -e . && cd ..
   python -m vptserver.cv.hloc_db --scene scene_data/example1   # 出力 scene_data/example1/hloc/
   ```
2. **オンライン（キーフレームごと）**: 受信 JPEG → SuperPoint → NetVLAD 検索 → LightGlue マッチ → 2D-3D → `solvePnPRansac`(K_for_image) → `T_colmap←cam`、カメラ中心 `C_c`。
3. **集約**: 複数 KF の (`C_c`, Quest world 位置 `C_w`) を `solve_sim3_ransac` で Sim(3) 化（反射→負スケール）。
4. **返送**: WSL2 の `localize.py` が `received/result_sim3.json` を書き出す → Windows の `run.py` が検知して **type=3 RESULT を Quest:50002 へ返送** → Quest `UDPManager` が受信 → `AlignmentManager.ApplySim3` が splatRoot に適用（符号付きスケール＝鏡像対応）。

> 現状: 1〜4 すべて実装済み。1(構築)・自己ローカライズ・実キーフレーム→Sim(3)(17/17, 残差1cm) は WSL2 で動作確認済み。残るは実機で「同一セッションでキーフレーム取得→Sim(3)適用→オーバーレイが実部屋に重なる」ことの確認。

### エンドツーエンドの実行（自動運用）
```
# Windows: 受信＋ビーコン＋返送＋GUI。起動ごとに received/session_<時刻>/ を生成し、
#          CV 設定（scene/every/min）を session/cv.json に書き出す。
python run.py                         # scene は自動検出。変えるなら --scene scene_data/example2 --every 5 等
# WSL2: 引数なしでOK（最新 session_* を自動追従。scene/every/min は run.py の cv.json か自動検出で解決）
python -m vptserver.cv.localize
# Quest: 部屋を3か所以上の別位置から左グリップ → 数枚ごとに Sim(3) 更新→ Quest 返送→ 自動でアライン更新
```
- **設定は run.py 側に集約**: `--scene`/`--every`/`--min` を run.py に渡すと `session/cv.json` に記録され、
  WSL2 の localize はそれを読む。よって **WSL2 は `python -m vptserver.cv.localize` だけ**でよい。
  `scene_data/` にビルド済みシーンが1つなら scene 指定すら不要（自動検出）。
- **セッションフォルダ**: run.py は起動ごとに `received/session_<timestamp>/` を作り保存。WSL2 は最新を自動追従。
  **手動クリア不要**でセッション分離（別座標系の混入や reqId 衝突を回避）。`--flat` で旧来の直下保存。
- GUI: Quest 接続・受信数・現在セッション・最新 Sim(3)（scale/inliers/rmse）・イベントログを可視化。
- localize の引数は任意で上書き可（`--scene`/`--watch`/`--every`/`--min`/`--keyframes <dir>`（単発））。

運用契約: **1回の run.py 実行＝1セッション**。新しいセッション（Quest 再起動を含む）では run.py を再起動する
（新フォルダ＋cv.json が作られ、watch も追従。手動削除は不要）。Sim(3) はそのセッションの Quest ワールド
座標系基準。跨セッションの永続化は M1.5 のアンカー相対保存で対応予定。

### 評価実験の解析（実験のあと）

```
# 1) まず原点が動いた試行を落とす。ここを飛ばすと「よく合った解」に見えたまま数 m ずれる
python analysis/check_tracking.py received/session_20260802_14[34]*

# 2) 主結果。--run はその実験の Quest ログ ID（received/quest_events.jsonl の run 欄）
python analysis/experiment_report.py --received received --run exp_20260802_141845 --outdir analysis_out

# 3) 測量そのものの健全性（先端校正の流用・model 側 CP の編集など）
python analysis/analyze_experiments.py received/quest_events.jsonl --outdir analysis_out

# 4) 論文に貼る図（PDF・ベクタ）
python analysis/paper_figure.py --indir analysis_out --outdir ../extended-abst/figures
```

**`--run` を省かないこと。** `received/quest_events.jsonl` は正本なので過去の実験が全部入っており、
一方 `experiment_report` は label ごとに**最新の** session を採る。絞らないと過去 run の `trial-01` まで
最新の session に紐づき、当時の測量（別の `local`）を引いて数 m の誤差の行として並ぶ。

`experiment_report` の出力: `curve_aggregate.csv`（曲線）/ `curve_per_trial.csv`（試行ごと）/
`cp_errors.csv`（CP × 試行の誤差。図と本文が同じ数字を使うためのもの）。

## 構成

```
server/
├─ run.py                  ← エントリポイント（受信→再構成→保存ループ + ビーコン起動）
├─ test_send.py            ← 疎通テスト用ダミー送信（Quest 不要）
├─ requirements.txt        ← 受信/保存のコア依存（numpy[+opencv]）
├─ requirements-cv.txt     ← CV 依存（pycolmap, opencv, 任意で hloc/torch）
├─ README.md
├─ received/               ← 保存先（自動生成。1 セッション = 1 Quest ワールド座標系）
├─ scene_data/<name>/      ← COLMAP 一式（sparse/0 + images/ を DB に使用）
├─ analysis/
│  ├─ check_tracking.py    ← 原点が動いた試行の検出（**曲線を引く前に必ず通す**）
│  ├─ experiment_report.py ← 主結果。複数試行 → 収集時間 vs CP 誤差の曲線 / CP 別内訳 / 再現性
│  ├─ accuracy_vs_time.py  ← 1 試行ぶんの曲線（experiment_report が内部で使う）
│  ├─ analyze_experiments.py ← 測量そのものの健全性・先端校正・収束の要約
│  └─ paper_figure.py      ← 上の CSV → 論文に貼るベクタ図（PDF・段幅に合わせる）
├─ tests/
│  ├─ test_geometry.py     ← Sim(3)/Umeyama/クォータニオンの合成データ検証
│  ├─ test_gates.py        ← 品質ゲートの純粋関数
│  └─ test_liveness.py     ← PING/PONG・CV ハートビート。C# と Python の鍵名/type 定数の一致も検査
└─ vptserver/
   ├─ __init__.py
   ├─ protocol.py          ← パケット定義と parse()
   ├─ reassembler.py       ← reqId ごとにチャンク集約 → Keyframe
   ├─ udp_receiver.py      ← UDP ソケット受信ループ（timeout で Ctrl-C 対応）
   ├─ keyframe.py          ← Keyframe（K / 姿勢 / 画像）データクラス
   ├─ storage.py           ← jpg + json 保存
   ├─ beacon.py            ← サーバ存在通知ビーコン（Quest 自動検出用）
   └─ cv/                  ← 自動位置合わせ CV
      ├─ colmap_model.py   ← COLMAP sparse(.bin) リーダ（numpy のみ）
      ├─ geometry.py       ← qvec→R / カメラ中心 / Umeyama(反射許容) / Sim(3) / RANSAC
      ├─ migrate_model.py  ← レガシー COLMAP モデルを新形式(rigs/frames付き)へ移行
      ├─ hloc_db.py        ← オフライン DB 構築（SuperPoint 再三角測量＋NetVLAD, GPU）
      └─ localize.py       ← オンライン・ローカライズ（検索→マッチ→PnP）＋自己テスト＋Sim(3)
```

> 環境メモ: CV（hloc/pycolmap）は **WSL2(Ubuntu)+CUDA** で実行する（Windows ネイティブの pycolmap は `.rigs` で落ちるため）。UDP 受信は Windows の `run.py` に残し、CV は WSL2 で動かして共有フォルダ経由で連携する疎結合構成。
