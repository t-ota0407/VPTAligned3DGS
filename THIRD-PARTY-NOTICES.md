# Third-Party Notices — VPTAligned3DGS

本プロジェクトは以下の第三者ソフトウェア・モデルに依存する。各コンポーネントは**それぞれのライセンス**に従う。
本プロジェクト自作部分のライセンスは同梱の [`LICENSE`](LICENSE)（MIT）。

> **記載の正確性について**: 下表はライセンス種別の要約であり、法的な正文ではない。実際の条項は各配布元の
> LICENSE / 利用規約を参照すること。バージョン更新時は本ファイルを見直す。

---

## ⚠ 重要（商用化の制約）

自動位置合わせの特徴抽出・画像検索に使う**学習済みモデルの一部は非商用/研究用途に限定**される。

- **SuperPoint**（特徴点抽出, Magic Leap）: 事前学習済み重みは**研究/非商用ライセンス**。
- **NetVLAD**（画像検索/大域記述子）: 公開重みは**研究用途**の条件が付く。

現構成は**学術研究・論文用途では利用可**だが、**そのまま商用製品には組み込めない**。商用化する場合は、
これらを商用可のモデル（例: 商用ライセンスの特徴抽出/検索）に差し替えること。マッチャの
**LightGlue は Apache-2.0**（商用可）なので、制約は主に SuperPoint/NetVLAD 側にある。

本リポジトリはこれらのコード・重みを**同梱しない**。`server/setup_windows.bat`（→ `setup_wsl.sh`）が利用者の PC の
WSL2 に各配布元から取得する（hloc・LightGlue は GitHub、重みは hloc / LightGlue が指す配布元）。
取得した時点で、利用者が各ライセンスに従うことになる。

---

## 3DGS 表示（Unity パッケージ・同梱）

| コンポーネント | 場所 | ライセンス | 権利者 |
|---|---|---|---|
| UnityGaussianSplatting（aras-p） | `unity/package/` | **MIT** | © 2023 Aras Pranckevičius |

- ソースをリポジトリに**同梱（再配布）**している（選択肢B）。MIT の要求どおり、著作権表示とライセンス本文を
  `unity/package/LICENSE.md`（および `unity/LICENSE.md`）として保持している。**これらは削除しないこと。**
- 本プロジェクトでは v1.1.1 を**素（未改変）**で使用（`CLAUDE.md` §2）。もし `unity/package/` 配下を改変した
  場合は、MIT に従い「改変した旨・改変者」を該当ファイルまたは本 NOTICE に明記すること。
- 上流: https://github.com/aras-p/UnityGaussianSplatting

## 位置合わせ CV パイプライン（PC / WSL2 側）

| コンポーネント | 用途 | ライセンス | 備考 |
|---|---|---|---|
| hloc (Hierarchical-Localization) | 再ローカライズ枠組み | **Apache-2.0** | https://github.com/cvg/Hierarchical-Localization |
| SuperPoint | 特徴点抽出 | **研究/非商用**（Magic Leap） | ⚠ 上記「商用化の制約」参照 |
| NetVLAD | 画像検索（大域記述子） | **研究用途** | ⚠ 同上 |
| LightGlue | 特徴マッチング | **Apache-2.0** | https://github.com/cvg/LightGlue |
| COLMAP / pycolmap | SfM モデル I/O・PnP・三角測量 | **BSD 系**（新 BSD, 引用要請あり） | https://colmap.github.io/ |
| PyTorch / torchvision | 推論基盤 | **BSD-3-Clause** | |
| OpenCV (opencv-python) | 画像処理 | **Apache-2.0**（OpenCV 4.5+） | |
| NumPy | 数値計算 | **BSD-3-Clause** | |
| h5py | 特徴量 I/O | **BSD-3-Clause** | |
| customtkinter | run.py の GUI | **MIT** | © Tom Schimansky |

## XR / プラットフォーム（Unity 側）

| コンポーネント | 用途 | ライセンス | 備考 |
|---|---|---|---|
| Meta XR SDK（`com.meta.xr.sdk.all`） | パススルー・PCA・アンカー・MRUK | **Meta 独自ライセンス** | Meta Platform Technologies SDK License |
| Unity OpenXR / Meta OpenXR feature | OpenXR 統合 | **Unity Package 利用規約** | |
| Unity URP / XR Management ほか Unity パッケージ | レンダリング・XR 管理 | **Unity Companion License 等** | Unity Editor 本体は Unity 社の独自ライセンス |
| Unity AI Assistant（`com.unity.ai.assistant`） | MCP 開発補助（実行時非依存） | **Unity Package 利用規約** | 開発時のみ |

---

## 補足

- 上表の「用途」「ライセンス」は 2026-07 時点の理解。各依存の更新時に再確認する。
- 本プロジェクト自作部分（`unity/projects/VPTAligned3DGS/Assets/Scripts/`, `server/`, 設計文書）は
  [`LICENSE`](LICENSE)（MIT, © 2026 Ota, The University of Tokyo）で提供する。
- 研究成果として公開・論文化する際は、COLMAP など**引用要請のある依存**の出典明記も忘れないこと。
