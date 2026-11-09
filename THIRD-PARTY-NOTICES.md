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
- 上流: https://github.com/aras-p/UnityGaussianSplatting
- 改変後も MIT のまま配布する（上流の著作権表示とライセンス本文はそのまま保持）。

### 同梱版への改変

同梱しているのは **v1.1.1（上流タグ `v1.1.1`・コミット `9310dce`）に改変を加えたもの**で、素の v1.1.1 ではない。
2026-11-09 に上流のタグと突き合わせた結果、改変は `unity/package/` の次の 6 ファイル（改行コードの違いは除く）。
それ以外のファイル、および `unity/docs/`・`unity/LICENSE.md` は上流と同一。

| ファイル | 改変の内容 |
|---|---|
| `Shaders/GaussianComposite.shader` | 合成の出力の α を全体の不透明度 `_GaussianSplatGlobalOpacity`（`GaussianSplatController` が `Shader.SetGlobalFloat` で設定。表示・非表示・フェードに使う）にする。色を α で割って戻し、α=0 の画素は捨てる。Editor（非 Android）では、シーンの深度テクスチャ `_GS_DepthTex` を見て手前に物がある画素を捨てる（ソフトウェア深度テスト）。`#pragma use_dxc` を外す |
| `Shaders/RenderGaussianSplats.shader` | `ZTest Always`（シーンの深度バッファに対して深度テストしない）。`#pragma use_dxc` を外す（Vulkan のため） |
| `Shaders/SplatUtilities.compute` | スプラット中心のクリップ座標の y を反転する（`centerClipPos.y *= -1`）。先頭に UTF-8 BOM |
| `Runtime/GaussianSplatURPFeature.cs` | 描画先（GaussianSplatRT）に深度バッファを結び付けない。`_CameraTargetTexture` に GaussianSplatRT を渡し、投影の上下反転（FlipProjectionIfBackbuffer）を抑える。Editor では深度テクスチャを合成シェーダに渡す。パスの順序を Android 実機は `BeforeRenderingOpaques`、Editor は `BeforeRenderingTransparents` にする |
| `Runtime/GaussianSplatRenderer.cs` | ソータが使えないとき（wave ops 非対応）に `Dispatch` を呼ばないガード。先頭に UTF-8 BOM |
| `Editor/GaussianSplatAssetCreator.cs` | コメントの綴りの修正のみ（unformly → uniformly） |

- **由来**: 本リポジトリの初回コミット（`2b00acb`）から含まれており、それ以後は変えていない。基盤に採用した既存アプリ
  `scalabroom`（`CLAUDE.md` §9）から引き継いだもの。
- **改変者**: Takashi Ota（`scalabroom` で改変。本リポジトリに入れてからの改変は無い）。
- **戻さないこと。** Quest 3 で両眼描画・貼り付き・ソートを実機で確認した構成（`CLAUDE.md` §9）は、この改変を含む。
  個々の改変がそれぞれ必須かどうかは検証していない。
- `unity/package/` をさらに改変したら、この表に足す（改変した旨・改変者・内容）。

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
