# CLAUDE.md — VPTAligned3DGS

このファイルは Claude Code がこのリポジトリで作業する際の指針です。**毎回最初に目を通すこと。**

---

## 0. 最優先：レンダラ実機検証ゲート（位置合わせ設計より前）

> **✅ 現状: このゲートはクリア済み（2026-06, scalabroom 基盤採用）。** ゼロ構築では 3DGS 表示とパススルー両立が難航し #151 のズレも再現したため、**両者がすでに両立して動作する既存アプリ（`scalabroom`）をコピーして基盤に採用**した。採用構成は **§2 の表**（Multi-Pass / Vulkan / aras-p v1.1.1 素 / 約15万スプラット）に確定記録。以下の基準・背景は経緯として残す。

位置合わせアルゴリズムの巧拙以前に、**aras-p レンダラが Quest 3 スタンドアロンで安定して描画・貼り付くか**が本プロジェクト全体の前提になる。ここが崩れると以降の設計はすべて無意味なので、**設計確定の前に最小検証を必ず通す。**

- **背景（既知のリスク）**: aras-p 公式は「D3D12/Metal/Vulkan のみ動作、モバイル/Web は未テスト」が基本姿勢。Quest 対応は *reportedly works* 段階で、スタンドアロン（Android ビルド＝本ターゲット）では「ソートが効かずスプラットがちらつく／消える」「**ヘッドポーズに合わせてスプラットが貼り付かずズレる**」等の報告がある（GitHub Issues #26/#112/#151/#205）。最後のズレは本アプリの核心（オーバーレイの貼り付き）を直接脅かす。
- **検証ゲート（合格基準）**: 位置合わせ実装に入る前に、素の `.ply` を表示するだけの最小シーンを Quest 3 **スタンドアロン実機**で動かし、次を満たすこと。
  1. Vulkan ビルドで両眼に正しく描画される（片目欠け・ステレオずれなし）。
  2. 頭を動かしてもスプラットが**ワールドに固定されて貼り付く**（#151 のズレが出ない）。
  3. GPU ソート（DeviceRadixSort）が実機 GPU で機能し、視点回転でソート崩れが出ない（#112）。
  4. 想定スプラット数（目安 40 万・Medium 品質）で目標フレームレートが出る（→ §4-3）。
- **採用バージョンの固定（確定）**: `scalabroom` 基盤 / **Unity 6000.3.10f1** / **URP（Render Graph Compatibility Mode Off）** / **OpenXR Render Mode = Multi-Pass**（aras-p v1.1.1 は Single Pass Instanced 非対応）/ **Graphics API = Vulkan** / aras-p `org.nesnausk.gaussian-splatting` **v1.1.1（素の構成・フォーク不要）** / `GaussianSplatURPFeature` を PC・Mobile 両 URP Renderer に付与 / フロアレベル原点。約 15 万スプラットの `.ply`（GPU バッファ 128MB 制限内）で合格。

---

## 1. プロジェクト概要

- **目的**: 作成済みの 3D Gaussian Splatting（3DGS）シーンの座標を、Meta Quest 3 のビデオパススルー（VPT）と**自動で位置合わせ**する XR アプリを開発する。
- **コア課題**: 「どんなアルゴリズムで VPT と 3DGS シーンを位置合わせするか」の設計。実装の前に必ず設計を固める（→ §4）。
- **プラットフォーム**: Meta Quest 3（Android / Horizon OS）。
- **3DGS 表示**: aras-p の [UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting)（Unity package `org.nesnausk.gaussian-splatting`）を使用。入力は `.ply` 形式。

---

## 2. 技術スタック / バージョン前提

| 項目 | 採用 | 備考 |
|---|---|---|
| Unity | **Unity 6000.3.10f1** | scalabroom 基盤の採用版。公式 Unity MCP（AI Assistant）も Unity 6 以上を要求 |
| レンダーパイプライン | **URP**（Render Graph Compatibility Mode Off） | Quest VR + 3DGS の定番。BiRP は VR で行列周りの不具合報告あり |
| Render Mode | **OpenXR Multi-Pass** | aras-p v1.1.1 は Single Pass Instanced 非対応のため Multi-Pass。`GaussianSplatURPFeature` を PC・Mobile 両 Renderer に付与（§6） |
| Unity MCP | **Unity 公式 AI Assistant**（`com.unity.ai.assistant`） | Editor 内の Unity Bridge 経由で操作 |
| 3DGS | `org.nesnausk.gaussian-splatting`（aras-p）**v1.1.1（素・フォーク不要）** | **Quest 3 スタンドアロン動作は確認済み（§0 クリア）**: Multi-Pass＋Vulkan＋素の v1.1.1 で両眼描画・貼り付き・ソート安定・72fps 圏内。Graphics API は **Vulkan 必須**（OpenGL ES 非対応）。`.ply` は GPU バッファ 128MB 制限（≒32B/スプラット → 約 419 万が上限。採用は約15万） |
| Graphics API | **Vulkan**（Quest 3） | aras-p レンダラは D3D12/Metal/Vulkan のみ対応。OpenGL ES では描画不可 |
| XR / パススルー | **Unity OpenXR + Meta OpenXR feature**（`com.unity.xr.meta-openxr`）をベースに、**Meta XR Core SDK + MR Utility Kit (MRUK)** を併用 | パススルー表示・空間アンカー・シーンメッシュ |
| カメラ映像 | **Passthrough Camera API (PCA)** = Meta XR SDK の **`PassthroughCameraAccess`**（Building Block「Passthrough Camera Access」, MRUK v81+） | CV/ML ベースの位置合わせ用。旧 `WebCamTextureManager`/`PassthroughCameraUtils` は非推奨。権限は `HEADSET_CAMERA` のみ（§7） |
| ビルド | Android / ARM64 / IL2CPP | Quest 3 向け |

バージョンや採用パッケージを変更した場合は、**この表とコード内コメントを必ず同期**すること。

---

## 3. ディレクトリ構成

```
<repo root>/
├─ CLAUDE.md                      ← 本ファイル（Unity/XR 運用規約・最優先の前提 §0）
└─ unity/
   ├─ readme.md                   ← 使い方（運用手順）の索引（セットアップ・実行・操作・トラブルシュート）
   ├─ Specification.md            ← 設計書（位置合わせの方式・トレードオフ・as-built）
   ├─ ImplementationSpec.md       ← 実装仕様書（確定値・データ形式・座標数式・§13 パラメータが単一の真実）
   └─ projects/VPTAligned3DGS/    ← Unity プロジェクト本体
      ├─ Assets/
      ├─ Packages/
      └─ ProjectSettings/
```

- **3 文書の役割**: `Specification.md`=なぜこの設計か／`ImplementationSpec.md`=どう実装するか（暫定既定値に確定済み・パラメータは同書 §13 が単一の真実）／本ファイル=Unity/XR 運用と前提ゲート。
- 設計の正は `unity/Specification.md`、実装の正は `unity/ImplementationSpec.md`。実装に着手する前に両方を必ず読む。未確定の箇所は勝手に決めず、ユーザーに確認するか各仕様に「要確認」として明示する。確定値を変えたら `ImplementationSpec.md` §13 を更新し他文書と同期する。
- Unity プロジェクトのファイル（シーン、コンポーネント、設定）は **MCP 経由で操作**する（§5）。`.unity` / `.asset` / `ProjectSettings` を手書き編集するのは避ける。

---

## 4. 最重要ルール

1. **実装より設計が先。** VPT ⇄ 3DGS の位置合わせアルゴリズムが未確定のうちは、コードを書く前に `unity/Specification.md` に設計案（前提・入力・手順・座標系・評価方法）を書き、ユーザーと合意する。複数案がある場合はトレードオフを併記する。
2. **Unity の状態は MCP 経由で確認・変更する。** シーン構成やコンポーネントの現状を「こうなっているはず」と仮定せず、MCP で実際に問い合わせてから操作する（§5）。
3. **Quest 3 のパフォーマンス予算を常に意識する。** スプラット数が支配的。目安として**約 40 万ガウシアンで 72fps 安定**という報告があるが、この種の数字は多くが**単視点（非ステレオ）基準**である点に注意。VR はステレオ描画で負荷が増え、特に **Multi-Pass はほぼ 2 倍**になる（→ §6）。実機予算は §0 の検証ゲートで採用 Render Mode のまま実測し、超過する `.ply` は品質レベルを落とす／Cutout で削る等の方針を設計に含める。
4. **座標系の違いを甘く見ない。** 3DGS（COLMAP 由来）と Unity（左手系・Y-up）では軸・スケール・向きが異なる。位置合わせの正確さに直結するため、変換規約を `Specification.md` に明記し、コードで一元管理する。
5. **破壊的操作の前に確認。** シーンやアセットを削除・大幅改変する MCP 操作の前にはユーザーに確認する。

---

## 5. Unity MCP（公式 AI Assistant）運用

- Unity 側: `Edit > Project Settings > AI > Unity MCP` で **Unity Bridge が Running（緑）** であることを前提に動く。Stopped の場合はユーザーに開始を依頼する。Editor 起動時に relay バイナリが `~/.unity/relay/` に配置され、MCP クライアントがこれを起動する。
- 作業の基本動線:
  1. MCP で現在のシーン／GameObject／コンポーネントの状態を**取得**する。
  2. 変更を**最小単位**で適用する。
  3. Console ログを MCP で読み、エラー／警告を確認してから次へ進む。
- C# スクリプトの新規作成・編集後はコンパイルの完了とエラーの有無を MCP で確認する。コンパイルエラーを放置して次の操作に進まない。
- Editor がバックグラウンドでも処理されるが、Play Mode の開始・停止など状態変更は MCP の状態取得で結果を確認する。

> MCP サーバーを別実装（CoplayDev/unity-mcp など）に変更した場合は、ツール名・ワークフローが変わるため本節を更新すること。

---

## 6. 3DGS（UnityGaussianSplatting）の扱い

- 導入: Package Manager → Add package from git URL で `org.nesnausk.gaussian-splatting` を追加。
- **`.ply` は実行時に直接ロードしない。** Editor のアセット作成ツール（`Tools > Gaussian Splats > Create GaussianSplatAsset` 系）で `.ply` を **GaussianSplatAsset + データファイルに変換**してから、`GaussianSplatRenderer` に割り当てる。この変換は Editor 専用工程なので、MCP 経由のメニュー実行 or 手順をユーザーに依頼する形で進める。
- 品質レベル（Very High / High / Medium / Low）でアセットサイズと描画負荷が変わる。Quest 3 向けは Medium 以下を起点に検証する。
- 位置合わせ対象は基本的に `GaussianSplatRenderer` を持つルート Transform。**位置合わせの結果はこの Transform（および親）に対する SRT で表現**するのが扱いやすい。スプラットデータ自体は書き換えない。
- **座標規約（確定・最重要）**: aras-p は PLY の点座標を **verbatim（軸反転なし）** で読み込む（`GaussianFileReader` で確認。回転だけ表示用 swizzle 補正、位置は不変）。よって **モデル座標＝元 COLMAP 座標の数値**。COLMAP（右手系）を Unity（左手系）で解釈すると鏡像になるため、実空間との整合には**反射**が必要で、これを **splatRoot の一様スケールを負値**にして表現する（手動合わせも自動 Sim(3) も負スケール ≈ -0.96）。`localScale=(s,s,s)` の `s<0` を許容して適用する（`AlignmentManager.ApplySim3`）。詳細は `Specification.md` §8。

### VR 表示時の設定（定番）
- URP を使用（Render Graph の **Compatibility Mode は Off**）、`XR Plugin Management` を有効化、OpenXR を初期化。Graphics API は **Vulkan**（§2）。
- **Render Mode = Multi-Pass（確定）**。aras-p v1.1.1 は Single Pass Instanced で正しく描画できなかった（スプラットが見えない）ため Multi-Pass を採用。描画コストはほぼ 2 倍になるため、スプラット数予算は控えめに（採用 `.ply` は約15万）。`GaussianSplatURPFeature` を **PC・Mobile 両方の URP Renderer に付与**する（実機は Mobile 品質 Renderer を使うため、片方だけだと実機で見えない）。
- GPU ソート（DeviceRadixSort）は **素の v1.1.1＋Multi-Pass＋Vulkan** で実機動作を確認済み（フォーク不要）。`.ply` は GPU バッファ 128MB 制限（≒32B/スプラット）に注意し、超える場合は品質レベルを落とす／スプラット数を減らす。

---

## 7. Quest 3 / VPT / パススルー

### パススルー表示
- Unity OpenXR の Meta パススルー層（composition layer）で背景にパススルーを合成する。URP の設定（カメラ背景の alpha=0 等）が正しくないと表示されないため、Meta OpenXR の手順に従う。
- **重要な制約**: OpenXR のパススルー層は**ピクセル/画像データにアクセスできない**（コンポジタに直接渡される）。映像を解析する用途には使えない。

### カメラ映像が必要な位置合わせ（CV/ML ベース）
- カメラフレーム取得は **Passthrough Camera API (PCA)** = Meta XR SDK の **`PassthroughCameraAccess`** コンポーネント（Building Block「**Passthrough Camera Access**」で配置）を使う。`GetTexture()`（GPUテクスチャ）・`Intrinsics`（K）・`GetCameraPose()`（ワールド姿勢）・`Timestamp`・`ViewportPointToRay/WorldToViewportPoint`・左右カメラ同時に対応。**旧 `WebCamTextureManager`/`PassthroughCameraUtils` は非推奨**（移行済み）。
- 必須要件:
  - Quest 3 / 3S。**Horizon OS v81 以降・MRUK v81 以降**（`PassthroughCameraAccess` の要件。PCA 自体は v74+ だが新コンポーネントは v81+）。
  - 権限: **`horizonos.permission.HEADSET_CAMERA` のみ**（新 API では `android.permission.CAMERA` は不要）。`OculusProjectConfig` で `isPassthroughCameraAccessEnabled=true`、実行時に `OVRPermissionsRequester` で要求。
  - XR Simulator は PCA 非対応 → 実機か Horizon Link v2.1+ で確認。公開可否のステータスは変わりうるので**公開検討時に最新 Meta ドキュメントを確認**する。
- 解像度は固定値に依存せず `GetSupportedResolutions()` から動的に選ぶ（既定 1280×960）。
- デバッグ補助として Building Block「**Passthrough Camera Visualizer**」やサンプル（CameraToWorld / Camera brightness estimation）を立ち上げ時のみ利用してよい（製品ビルドには含めない）。Visualizer のクアッドは `GSTestScene` で **`y = −100 m` に退避済み**（原点に置くと実験中ずっと視界を塞ぎ、CP の球や実物を隠す）。使うときだけ Inspector で `Position.y` を 0 に戻す。無効化ではなく移動にしてあるのは 1 操作で戻せる状態を保つため（テクスチャは `PassthroughCameraAccess.TargetMaterial` 経由なので位置に依らない）。

---

## 8. 位置合わせ（VPT ⇄ 3DGS）設計メモ

> **採用方針（as-built・実装完了, v6/v3）**: **「手動→自動の二段構え」＋「画像特徴ベースのレジストレーション」**を採用・実装し、**実機で自動位置合わせまで動作確認済み**。照合 DB は 3DGS レンダ画像ではなく**ユーザー保有の COLMAP（`sparse/0`＋元画像）に対する hloc 再ローカライズ**。手動(M1)・半自動の水平/床スナップ(M2)・アンカー永続化(M1.5) はブートストラップ／フォールバック。詳細は `Specification.md` v6・`ImplementationSpec.md` v3。
>
> **自動位置合わせの全体像（実装済み）**:
> 1. Quest が左グリップでパススルー画像（縮小 JPEG）＋K＋ワールド姿勢を **UDP で PC へ送信**（`KeyframeUploader`/`UDPManager`）。
> 2. PC は **WSL2(Ubuntu)+CUDA** で hloc 再ローカライズ（SuperPoint→NetVLAD検索→LightGlue→`localize_sfm`/PnP）→ COLMAP=モデル座標系のカメラ姿勢。複数 KF のカメラ中心と Quest ワールド中心から **Umeyama で Sim(3)**（`vptserver/cv/localize.py`,`geometry.py`）。
> 3. Sim(3) を **Quest へ返送**（type=3）→ `AlignmentManager.ApplySim3` が splatRoot に適用 → `OnLocked` でアンカー永続化(M1.5)。
>
> **座標規約（最重要）**: aras-p は PLY 点を **verbatim（軸反転なし）** で読むため、モデル座標＝COLMAP 座標。Unity 左手系との整合に**反射**が要るので **splatRoot の一様スケールを負値**にして表現する（手動合わせも自動 Sim(3) も負スケール）。詳細 §6・`Specification.md` §8。
>
> **計算配置と環境**: 重い CV は **WSL2 必須**（pycolmap は Windows ネイティブで `reconstruction.rigs` アクセス時にクラッシュ。Linux で安定）。UDP 受信・Sim(3) 返送・**GUI** は Windows の `run.py`。両者は **共有フォルダ `/mnt/c` 経由で疎結合**。詳細・運用手順・ポートは **§13（PC オフロードサーバの運用）** 参照。

`Specification.md` で詰める設計の候補（記録）:

- **手動 → 自動の二段構え（採用）**: まず手動（コントローラ操作）で粗合わせし、その初期姿勢から自動で微調整。実装が堅実。
- **空間アンカー / シーンメッシュ基準**: MRUK のシーン（床・壁・メッシュ）に 3DGS の幾何を合わせる。カメラ画像なしで成立。M2 の床スナップで部分採用。
- **マーカー基準**: 実空間に既知マーカー（QR 等）を置き、PCA でカメラ画像から検出して姿勢を解く。
- **画像特徴ベースのレジストレーション（採用・本命）**: パススルーカメラ画像と COLMAP DB を特徴マッチング → PnP → Sim(3)。最も高度・カメラ画像必須。

どの案でも明記すべき項目: 座標系変換規約、初期姿勢の与え方、最適化対象（位置・回転・スケール）、収束判定と精度評価指標、失敗時のフォールバック。

---

## 9. ビルド & 動作確認

- Build Profiles で Android プラットフォームに切替（早めにやる）。ARM64 / IL2CPP。
- 実機（Quest 3）で確認すべき最低ライン: 両目で 3DGS が正しく描画される / パススルーが表示される / 想定フレームレートが出る / 必要権限のダイアログが出る。
- パフォーマンス低下時はスプラット数・品質レベル・Render Mode・ソート設定を順に見直す。
- ログは MCP 経由（Editor）と実機（adb logcat 等）の両面で確認する。

---

## 10. コーディング規約 / 進め方

- C#。スクリプトは `Assets/Scripts/` 配下に機能単位でフォルダ分け。位置合わせのコア（座標変換・最適化）は MonoBehaviour から分離した純粋クラスにし、テスト可能にする。
- 1 つの変更は小さく。MCP 操作・コンパイル確認・動作確認をこまめに挟む。
- マジックナンバー（スケール係数・軸反転など座標変換に関わる値）は定数化し、根拠をコメントする。
- 外部リポジトリ（UnityGaussianSplatting / Meta サンプル）のコードを取り込む場合は、出所とライセンス（MIT 等）をコメントに残す。

---

## 11. やってはいけないこと（Don'ts）

- ❌ `Specification.md` を読まずに位置合わせの実装を始める。
- ❌ MCP で現状確認せずにシーン／コンポーネントを「あるはず」の前提で変更する。
- ❌ `.ply` を実行時に直接読む前提でコードを書く（必ずアセット変換工程を経る）。
- ❌ OpenXR パススルー層からカメラ画像を取れる前提で CV を組む（→ PCA を使う）。
- ❌ 座標系変換をコード各所に散らばらせる（一元管理する）。
- ❌ パフォーマンス予算（スプラット数）を無視した重い `.ply` を無検証で投入する。

---

## 12. 参考リンク

- UnityGaussianSplatting（公式・aras-p）: https://github.com/aras-p/UnityGaussianSplatting
- Quest スタンドアロンの既知不具合（要確認）: Issue #26（OpenGL ES 非対応）/ #112（Android でソート不動）/ #151（頭の動きで貼り付かずズレる）/ #205（黒画面・激重）
- VR 特化 fork（比較候補）: https://github.com/ninjamode/Unity-VR-Gaussian-Splatting , https://github.com/ptc-lexvandersluijs/Unity3DGS_VR
- Meta Passthrough Camera API（概要 / Unity）: https://developers.meta.com/horizon/documentation/unity/unity-pca-overview/
- PassthroughCameraAccess への移行（MRUK v81・権限・タイムスタンプ）: https://developers.meta.com/horizon/documentation/unity/unity-pca-migration-from-webcamtexture/
- Building Blocks 概要: https://developers.meta.com/horizon/documentation/unity/bb-overview/
- CameraViewer / CameraToWorld / Camera brightness サンプル: https://developers.meta.com/horizon/documentation/unity/unity-sample-camera-viewer/
- Unity-PassthroughCameraApiSamples: https://github.com/oculus-samples/Unity-PassthroughCameraApiSamples
- Unity OpenXR: Meta（Camera / Passthrough）: https://docs.unity3d.com/Packages/com.unity.xr.meta-openxr@2.2/manual/features/camera.html
- Unity MCP（公式 AI Assistant）: https://docs.unity3d.com/Packages/com.unity.ai.assistant@2.0/manual/unity-mcp-get-started.html
- hloc（hierarchical-localization, SuperPoint/LightGlue/NetVLAD）: https://github.com/cvg/Hierarchical-Localization

---

## 13. PC オフロードサーバの運用（自動位置合わせの CV 側・as-built）

リポジトリ直下 `server/`（`unity/` と同階層）に実装。**重い CV は WSL2(Ubuntu)+CUDA で実行**し、UDP 受信・Sim(3) 返送・GUI は Windows の `run.py` が担う**疎結合構成**（共有フォルダ `/mnt/c` 経由）。実装の正は `ImplementationSpec.md` v3。

### なぜ WSL2 か
pycolmap は **Windows ネイティブで `reconstruction.rigs` アクセス時に access violation でクラッシュ**する（pycolmap 4.0.4/3.13.0 両方で再現、numpy 版・torch 同居の有無に依らず、torch 抜きの純 pycolmap でも発生）。Linux（WSL2）では安定。よって hloc/pycolmap を使う CV は WSL2 で動かす。CV 依存は `requirements-cv.txt`（**torch cu124・pycolmap==3.13.0・hloc は GitHub から `git clone --recursive` で導入・numpy 2.x**）。`hloc` は PyPI に無い。

### 構成（実体）
```
server/
├─ run.py                 ← Windows: UDP受信＋ビーコン＋Sim(3)返送＋GUIホスト
├─ requirements.txt / requirements-cv.txt
├─ scene_data/<name>/     ← COLMAP 一式（sparse/0 + images/）。再ローカライズ DB
├─ received/session_<時刻>/← 起動毎のセッション（キーフレーム・cv.json・result_sim3.json）
├─ tests/test_geometry.py, test_gates.py
└─ vptserver/
   ├─ protocol / reassembler / udp_receiver / keyframe / storage / beacon / gui
   └─ cv/ colmap_model, geometry(Umeyama/Sim3), gates(品質ゲート純粋関数), hloc_db(オフラインDB構築),
          migrate_model(レガシーCOLMAP→新形式), localize(オンライン照合＋watch)
```

### ポートとプロトコル（UDP ベストエフォート）
- 13B ヘッダ `['V','P', type, reqId(4 LE), idx(2 LE), total(2 LE), len(2 LE)]` ＋ payload。
- **データ 50000**（Quest→PC: type=1 META(JSON), type=2 IMG(JPEG チャンク), **type=4 RESET**, **type=7 EVENT**）/ **ビーコン 50001**（PC→LAN ブロードキャスト `b"VPTSRV1"`＋データポート, Quest が送信元 IP を自動学習）/ **結果 50002**（PC→Quest: type=3 RESULT = Sim(3) JSON）。
- **type=4 RESET（試行リセット・評価用）**: Quest→PC。payload は JSON（`trial`/`label`/`mode`）。受けると `run.py` は新しい `received/session_<ts>/` を切り、`cv.json` と `trial.json` を書く。WSL2 の `localize.watch` は最新セッション追従なので Cc/Cw の蓄積を自ら捨てる。UDP の取りこぼし対策でヘッダの reqId を resetId として同じ値を 3 回送り、PC 側は resetId で重複を無視する。これにより **`run.py` と Quest アプリを再起動せずに独立した試行を繰り返せる**（10 試行）。
- **type=7 EVENT（Quest→PC・実験ログの 1 行）**: `ExperimentLogger` が書く行のうち**解析に必要なものだけ**（`cp_survey*` / **`cp_model_*`** / `trial_start` / `trial_end` / `tracking_origin_changed` / `tip_calib_*` / `auto_aligned` 等。`fps`/`kf_sent`/`sim3_gate` は送らない）を UTF-8 JSON で送る。reqId に通し番号 seq。PC は **2 か所**へ書く: `received/quest_events.jsonl`（トップレベル・正本。到着順に全部）と `received/session_*/quest_events.jsonl`（複製。**到着時刻ではなく行に載っている試行ラベルで振り分ける**ので、遅れて届いた再送も正しい試行のフォルダに入る）。正本を別に持つのは、試行に属さない行（session_start、**CP の測量**など）や、ラベル未確定のうちに届いた行を落とさないため。**CP の測量は実験前に 1 回だけで、どの試行にも属さない（`trial=0`）ので正本にだけ載る**。試行との対応は `trial_start.survey`（測量番号）が持つ。重複除去は `(run, seq)`。`run` は Quest 側のログファイル名（= アプリ起動 ID）で、これが無いと `trial` が起動ごとに 1 から振り直されて衝突する。**ACK 付き**: PONG の `ev_ack`（連続受信済み seq）を見て Quest が未達分だけを再送する（`UDPManager._outbox`）。CP 実測値は実験を通して 6 点しかなく、後から測り直すと座標系が変わってやり直しが効かないのでベストエフォートでは足りない。これにより **`adb pull` なしで PC 側だけで解析が完結する**（端末内 JSONL は冗長化として残る）。`cp_model_point` ＋ `cp_model_edit_done(phase=end)` を受けると `run.py` は `received/controlpoints_<modelId>.json` を **`ControlPointStore` と同じ形**で書き出す（Editor の Control Points ウィンドウがそのまま取り込める）。ファイル名の規則は Unity の `ControlPointStore.FileName` と `run.cp_store_filename` の**両方**にあるので、変えるときは対で直す。締めのあとに落ちていた点が再送で届いたら書き直す。検証は `tests/test_events.py`。
- **type=5 PING / type=6 PONG（疎通確認）**: Quest が 2 秒ごとに PING（データポート）→ PC が即 PONG（結果ポート）。`reqId` に seq を往復させて RTT を取る。PONG は `{"seq","session","kf","cv_alive","cv_age","cv_scene","cv_window"}`。**`HasTarget`（送信先 IP を知っている）を疎通判定に使わないこと** — IP は PlayerPrefs の学習値や手動設定で埋まるため、`run.py` 未起動でも true になる。判定は `UDPManager.ServerAlive`（PONG の鮮度）を使う。`cv_alive` は WSL2 の `localize.watch` が 2 秒ごとに書く `cv_heartbeat.json` の鮮度（既定 8 秒で失効）で、**run.py だけ動いて CV が未起動**という「キーフレームは届くが Sim(3) が永遠に返らない」失敗モードを実験前に検出するためにある。C# と Python の鍵名・type 定数の一致は `tests/test_liveness.py` が機械的に検査する。
- AP がブロードキャストを無線側に遮断する環境では自動検出不可 → Quest `UDPManager.manualIPAddress` に PC の IP を固定（ユニキャストは通る）。ビーコン受信に `CHANGE_WIFI_MULTICAST_STATE` 権限が必要（AndroidManifest 済み）。
- **`cv.json` フィールド**（run.py が書き watch がライブ反映）: `scene` / `every` / `min`（**既定 5**。後述）/ `window`（P2-b スライディングウィンドウ・既定24・0=無制限）に加え、品質ゲート（P1）用に `min_inliers`(30) / `min_inlier_ratio`(0.5) / `max_rmse`(0.05) / `scale_range`([0.5,2.0]) / **`expected_scale`(None)** / `scale_tol`(0.10) / `min_baseline_m`(0.40) / `min_second_extent_m`(0.15)。既定は `vptserver/cv/gates.py` の `DEFAULT_GATES`。type=3 RESULT の Sim(3) JSON は Quest 側でも同種ゲート（`AlignmentManager` Inspector）を通してから適用する。

### Sim(3) の退化と確定条件（**残差では正しさを判定できない**）

実機で「採用 1 件で確定したのに位置がまるで違う」が起きた。原因は閾値ではなく退化で、**どちらも残差が小さいまま誤る**。`unity/readme.md` §7、検証は `tests/test_alignment_gates.py`。

1. **共面配置＋反射の自由推定**: 3 点は必ず同一平面上に乗る。反射を許すと「その平面に関する鏡映を掛けた解」が**完全に同じ残差**を与えるため 2 解を区別できず、実質コインフリップになる。**頭の高さを変えずに部屋を歩くとカメラ中心はほぼ共面**なので枚数を増やしても起きる。数値検証で共面 6 点 47%・3 点 68% が誤り。
   → **対策: 反射を負に固定**（`geometry.umeyama(handedness=-1)`）。手性の違いは COLMAP 右手系 / Unity 左手系という座標規約から決まる**定数**であって推定対象ではない（§6）。固定すると失敗率 0%。自由推定に戻すなら `cv.json` の `handedness=0`。
2. **共線配置**: 一直線に歩くと直線まわりの回転が拘束されない。反射を固定しても 100% 誤る。
   → **対策: 配置ゲート**（`gates.check_geometry` / `geometry.center_conditioning`）。カメラ中心の最大間隔（1.5m）と第 2 主成分の広がり（0.4m）で弾く。
   **閾値を締めても精度は上がらず、ロックしなくなるだけ**（1.0m/0.8m にしたら 9m 大きく回る歩き方しか通らず 60 秒以内にロックしなくなった）。精度は「どれだけ部屋を歩いたか」でほぼ決まる（9m 弧 → ロック 100%・誤差中央 10cm ／ 6m 弧 → 92%・16cm ／ 3m 以下 → 確定しない）。ゲートの役割は「足りていないときに確定させない」ことだけで、精度の担保は**歩き方の指示**が行う。
3. **`expected_scale` は評価実験では使わない**: COLMAP の任意単位と実メートルの比は再構成に固有の固定値（このシーンで 0.96）で、`--expected-scale` で棄却ゲートにできる。**しかし正しさには不要**（1・2・4 で誤り率は既に 0%、追加しても改善しない）。シーン固有の既知値を推定に注入すると「メトリックスケールを解決する」という論文の主張が弱くなるので、既定は無効のまま。推定値が独立実測値とどれだけ一致したかを**結果として報告する**ほうが強い。デバッグで棄却理由を明示したいときだけ使う。
4. **確定は連続一致で**: `AlignmentManager.minConsecutiveAgreements`（既定 2）。独立に解かれた解がスプラット bbox 8 隅で **15cm** 以内に一致した回数が揃って初めて `Locked` / `OnAutoAligned`。未確定の間も姿勢は適用するが永続化しない。**残差が小さいことより、独立な解が同じ答えに落ち着くことのほうが強い証拠**。bbox の隅は原点から 6m 以上離れるので、閾値を 8cm にすると実機の雑音下でほぼ成立せずロックしなくなる。
4b. **時間予算**: 確定に 20 枚程度必要。`KeyframeUploader.autoMinIntervalS` が 2.0 だと送信だけで 40 秒かかり 60 秒制限に余裕が無いので **1.0** にした。`cv.json` の `every` も 3→**2**（一致判定の機会を増やす）。
5. **`min` は 5 以上**: `solve_sim3_ransac` は **n<5 で RANSAC を回さない**（全点で 1 回当てはめる）ため、4 以下だと外れ値除去が一切効かない。
6. **Quest のワールド原点が動く（残差では検出不能・実機で発生）**: Sim(3) は「Quest ワールドは固定」が前提だが、再センタリング／ヘッドセットの着脱・スリープ復帰／再ローカライズで原点が **鉛直軸まわりの回転＋水平移動** の形で飛ぶ。飛んでも rmse もインライア率も悪化しない。実機で「3 試行とも同じように間違う」が起きたときの原因はこれで、試行間の解の差が **スケール 1.000・回転軸 (0,1,0)・鉛直移動 0** の相似変換になっていた（37.7°・78.1°）。推定誤差ならこの形にはならない — 誤差は最も弱く拘束される傾き方向に出るのに対し、カメラ中心がほぼ同高度に並ぶ本件ではヨーと水平位置こそ最もよく決まるパラメータだから。収集の途中で動いた場合は末尾のキーフレーム残差だけが伸びる（実機で 0.4→0.8→1.9→2.8 m）。
   → **対策 3 層**: (a) Quest `TrackingOriginWatcher`（`OVRPlugin.GetLocalTrackingSpaceRecenterCount` / `OVRManager.TrackingLost` / 頭部姿勢の不連続を**速度**で判定。距離で見るとフレーム落ちで誤検出する）、(b) `ExperimentController` が収集中の検出で**試行を自動破棄**し、あわせて**測量も無効化**する（測量値はワールド座標なので原点が回ると過去の座標系の値になる）（試行番号は消費せずラベルだけ消費。ラベルを使い回すと PC の `session_*/trial.json` と 1 対 1 対応しなくなる）、(c) PC 側 `localize` の終盤残差警告と `analysis/check_tracking.py`（試行内の残差急増＋試行間の純ヨー食い違いを検出）。**運用ではヘッドセットを外さない**。

### 運用手順（実機）
```bash
# 0) シーン毎に一度（WSL2/GPU）: 再ローカライズ DB 構築
python -m vptserver.cv.hloc_db --scene scene_data/<name>

# 1) Windows: 受信＋返送＋GUI（起動毎に received/session_<時刻>/ と cv.json を生成）
python run.py            # scene/every/min は GUI でも編集可（即 cv.json 反映）

# 2) WSL2: 最新 session を自動追従し cv.json をライブ反映、N枚ごとに Sim(3) 更新
python -m vptserver.cv.localize         # 引数なし（cv.json/自動検出で解決）

# 3) Quest（同一セッション）: 部屋を3か所以上の別位置から左グリップ送信 → 自動でアライン更新
```
- **1回の run.py 実行＝1セッション**。新セッション（Quest 再起動含む）は run.py を再起動するだけ（新フォルダ＋cv.json 自動生成、手動クリア不要、別座標系混入/reqId 衝突を回避）。
- Sim(3) はそのセッションの Quest ワールド座標系基準。跨セッション永続化は M1.5 のアンカー相対保存で対応予定。
- GUI（customtkinter ネイティブウインドウ）: Quest 接続・受信数・セッション・最新 Sim(3)（scale/inliers/rmse）・イベント・設定編集。未導入ならヘッドレス。

### 評価実験の実装（論文対応）
- **実機でやることは「一定時間歩き回る」だけ**。かつて E1（オーバーレイ精度）と E2（コールドスタート収束）を別モードで回していたが、**1 回の収集データから曲線全体をオフラインで再構成できる**ので試行は 1 種類（`mode="collect"`）に統合した。
- **用語（CP の 2 つの側）**: `CP_01` は 1 つの物理的特徴で、記録する座標が 2 つある。**Virtual CP** = 3DGS モデル上の座標（`local`。Editor か `ControlPointEdit` で置く。ログ名 `cp_model_edit`）／**Real CP** = 同じ特徴の実物の位置（`tip`。`ControlPointMeasure` で実測。ログ名 `cp_survey`）。誤差は `|T(local) − tip|`。**この呼び分けは HUD の文言とワールドのラベルだけ**で、クラス名・プロパティ名（`IsSurveyed`/`SurveyReady` 等）・UDP イベント名・`controlpoints_<modelId>.json` の鍵は**従来のまま**（過去ログと解析スクリプトの互換を優先）。**画面に出る名前は `VCP_01` / `RCP_01`**（`CpGizmos.VirtualLabel`/`RealLabel`。HUD も `CurrentLabel` 経由で同じ文字を出す）——両方 `CP_01` だった頃は見分けが**色だけ**に載っていたが、測定中は同じ点の 2 つの側がすぐ隣に並ぶうえ、色の印象はパススルーの背景と照明で変わる。個々の点を画面に出すときは内部名 `CP_01` を素で書かないこと。HUD のボタンは `Edit Virtual CP`（A 長押し）／`Set Real CP`（A 短押し）。やり直しは `Redo Real CP` と書く——`Re-set` だと UDP の試行リセット（type=4 RESET）と読み違える。なお推定に一切使わない 6 点なので、写真測量の用語では control point ではなく **check point（検証点）** にあたる。呼称は CP のまま通し、論文でその旨を明記する。
- **HUD の文言は英語・枠からはみ出させない**（`AlignmentHud`）。実機で読むのは HUD だけなので日本語と混ぜない（日本語はログと `[Tooltip]` に限る。HUD に出る文字列を返すプロパティ——`UDPManager.TargetDescription` のような——を書くときは英語で）。レイアウトは `AddText` が**折り返し＋ best fit**（下限は指定サイズの 60〜70%・最小 10px）を全テキストに掛けて枠内に収める。`HorizontalWrapMode.Overflow` はクリップしないので使わない。準備チェックの行だけは best fit で縮めず **2 行ぶんの高さ**（`CheckH=40f`）を取る——警告が全部出ると幅の 1.4 倍（650px / 476px）になるが、そこは一番読ませたい局面なので小さくしない。行を足したらパネル高 `h`（実験用 688px）も見直す。
- **CP の測量は実験前に 1 回だけ**（先端校正と同じ扱いの前準備）。誤差 `|T(local) − tip|` の両辺は試行不変（`local`=Editor で置いたモデル座標、`tip`=実物のワールド座標）なので、試行ごとに測る意味が無い。試行ごとに測っていた頃は測り間違いの機会が試行数ぶん増えるだけだった（1 か所に立ったまま 6 点記録した試行が実際に出た）。
- Quest 側 `Assets/Scripts/Experiment/`: `ExperimentController`（試行制御。自動送信停止→RESET 送信→Quest 状態初期化→収集開始を 1 アクションに束ねる）／`ExperimentLogger`（JSONL。全行に `trial`/`mode` が付く。試行外は `trial=0`）／`ControlPointMeasure`（**実験前の CP 測量**。A で開始、**右トリガーで仮置き → A で確定して次の点へ**、左トリガーで仮置きの取り消し／（仮置きが無ければ）1 つ戻る。スキップは無い。**記録は 2 段階**にしてある——1 回のトリガーで確定していた頃は押した瞬間の先端位置がそのまま最終値で、トリガーを引く動作自体でコントローラが数 mm 動くうえ、狙った特徴に当たっているかを別角度から確かめる機会が無かった（ずれても「1 つ戻る」しか手が無い）。仮置きは白い点＋`RCP_01 ?` で出し、確定して初めて `cp_survey` が出て橙になる。確定までに置き直した回数は `cp_survey` の `retries` に残る。**測量中の A は中断に使わない**（余分な 1 押しで 6 点が消えるため。中断は Y のみ）。測量中は model 側 CP の球（黄=次 / シアン=未測 / 緑=測定済み）と**実測位置の橙の点**を、それぞれ `VCP_01` / `RCP_01` のラベル付きで出す。ラベルは splatRoot の**負スケールで鏡像になる**のを避けるためワールド原点直下の `CPGizmos` に置き、毎 `LateUpdate` で追従＋ビルボードする。TextMeshPro は導入していないので `AlignmentHud` と同じ world-space uGUI `Canvas`＋`Text`＋`LegacyRuntime.ttf` を使う）／**`ControlPointEdit`**（**model 側 CP を VR 内で置き直す**。A 長押しで開始、**右トリガーで先端位置へ仮置き（球が白）→ A 短押しで確定して自動で次の点へ**、左トリガーで仮置きの取り消し／（仮置きが無ければ）1 つ戻る、A 短押し（仮置きが無いとき）=`Skip`（置かずに次へ）、B 短押し=`Revert this`、B 長押し=`Revert all`、Y で終了。測量と同じ 2 段階にしてあり、確定するまで `_edited` にも入らず `cp_model_edit` も出ず測量の無効化も起きない（未確定のまま抜けたら破棄）。**HUD の全ボタンに物理割当を用意する**のは不変条件で、以前 `[poke]`（空中の 40px の板を手で突く）専用だった 3 つは実機で狙えず「機能が無い」と受け取られた。**列のどこにいるかを HUD のバー下に必ず出す**（`CP_02 · 2 / 6 · 1 moved`）——モデルが視界の外だと球の色だけでは進んだことが分からず、実機で「次の点へ進めない」と報告された（動作自体は正しかった）。Editor のマウス配置は視線方向の奥行きが取れず、その誤差は `local` 側の系統誤差として報告値に乗るため。**位置合わせ前だけ**（下の落とし穴 5）。終了時に UDP で全点を送り PC が `received/controlpoints_<modelId>.json` を書く → Editor の Control Points ウィンドウで取り込んでシーンへ恒久化する。端末側にも冗長化で書くが**起動時の自動復元はしない**（「ビルドしたシーンが model 側の正」を保つ））／`CpGizmos`（CP の球・ラベル描画。測量と編集で共有。`ControlPointMeasure` が所有し `ControlPointEdit` が借りる。**大きさは実寸固定ではなく頭からの距離で補正して見かけの大きさを一定に保つ**——実寸 3cm の球は 3m 先で 0.5°・10px 程度にしかならずスプラットの背景に埋もれ、実機で「CP_01 だけ見えて CP_02〜06 が見えない」＝描画はされているのに探せない、と報告された。1m 以内は実寸（直径 5cm）なので先端を当てる精度は不変。いま対象の点だけ更に 1.6 倍＋縦の棒を立てて部屋の反対側からでも拾えるようにする。**シェーダはコードから `Shader.Find` するだけだとビルドで剥がされる**——`new Material(Shader.Find("Unlit/Color"))` はビルド時の参照解析から見えないため実機でだけ `null` が返り、球が**ピンク（エラーシェーダ）**になった。`Project Settings > Graphics > Always Included Shaders` に `Unlit/Color` を登録して解決。ラベルが実機で読めていたのに球だけ壊れていたのは、uGUI の `UI/Default` が既定でこのリストにあり `Unlit/Color` だけ漏れていたため。**コードで作るマテリアルのシェーダを増やすときは必ずこのリストにも足す**）／`ControllerTipCalibration`（先端のピボット校正）／`ControlPointStore`（Editor で置いたモデル座標 CP の JSON 形式）／`TrackingOriginWatcher`（ワールド原点の移動検出）。Editor は `Assets/Editor/ControlPointToolWindow.cs`。
- **スプラットの表示は「いま何を見る作業か」で決まる**（起動時は非表示。`AlignmentManager.startHidden` 既定 on）。位置合わせ前のスプラットは実世界と無関係な場所に浮いて視界を塞ぐうえ「もう合っている」と誤解させる。規則は **モデルを見る作業なら出す／実環境を見る作業なら消す**: `Edit Virtual CP` 開始 → **表示**（モデル上の特徴に触るので必須）／`Set Real CP` 開始 → **非表示**（実物に先端を当てる作業で、スプラットが実物を覆い隠すと `tip` の精度に直接効く。見た目ではなく測定の問題）／試行の収集開始 → 非表示／姿勢が適用された時点 → 表示。X でいつでも切り替えられる。**球・ラベル・橙の点はこれに連動させない**——`ControlPointMeasure.GizmosActive` は測定中・編集中だけ `SplatVisible` を条件から外す。以前は `&& SplatVisible` だったので、Real CP の測定中にスプラットを消すと「次はどの点か」の案内まで丸ごと消えた。この 2 局面では X は「スプラットを覗く／隠す」だけの意味になる。初期値の設定順を確実にするため `GaussianSplatController` のグローバル不透明度初期化は **`Start` ではなく `Awake`**（`Start` どうしの順序は不定で、非表示にした直後に 1 で上書きされうる）。
- **実験中の手動姿勢操作は封じるが、CP 測量中と model 側 CP の編集中だけスティックを通す**（`AlignmentManager.PoseControlMode` = Full / **SticksOnly** / Locked を `ExperimentController.ApplyInputMode` が毎フレーム選ぶ）。位置合わせ前の CP の球は壁の向こうに出て近寄れないので、シーンごと引き寄せられないと球が目印として使えないし、編集にいたってはモデルに手が届かないと何もできない。**ただし Real CP の測定中のスティックは「球を視界に入れる」ためだけ**で、球を実物に重ねる作業ではない——重ねると球に釣られて先端が実物からずれ、それがそのままグラウンドトゥルースの誤差になる。HUD もそう書く（`bring the markers into view` / `The yellow sphere only says which point — it is NOT on the real object`）。**測量が測るのは実物に先端を当てたワールド座標だけ、編集が書くのは splatRoot ローカル座標だけ**でどちらも splatRoot の姿勢に依らないので測定値には影響せず、試行開始時の `ResetForNewTrial` が初期姿勢へ戻すので試行にも持ち込まれない。A/B/Y は `ExperimentController` が、両トリガーは測量の記録・やり直し／編集の置き直し・1 つ戻るが使っているので SticksOnly には含めない（トリガーを通すと 1 点触るたびにスプラットが伸縮する）。
- 集計は `server/analysis/accuracy_vs_time.py`（1 試行の曲線）／`experiment_report.py`（**主結果**。保存済みキーフレームから試行ごとに姿勢を解き直し、測量した CP と突き合わせる）／`check_tracking.py`（**曲線を引く前に必ず通す**。原点が動いた試行の判定）／`analyze_experiments.py`（**測量そのものの健全性**と先端校正・収束の要約。オーバーレイ精度は出さない）。
- **測定を無効にする落とし穴（対策済み。外さないこと）**:
  1. **先端オフセット**: `controllerAnchor` は本体内部の追跡原点で、物に当てる物理的先端ではない。差は数 cm あり測定対象と同オーダー。`ControllerTipCalibration` のピボット校正で補正し、未校正なら `ControlPointMeasure` が測量を拒否する。校正は**残差ではなく向きの広がり**でゲートする（広がり不足だと残差 0.5mm なのにオフセットは 2mm ずれる、と数値検証済み）。
  1b. **校正値の持ち越し**: 保存値は `TipState.Restored` として読み込むだけで、**既定では測量に使わせない**（`requireCalibrationEachSession=true`）。保存値が今の物理構成（別コントローラ・治具の付け外し）に対応している保証がなく、ずれても事後のログから検出できないため。HUD は緑の `✓` ではなく琥珀の `▲ 前回値` を出す。校正完了時に前回値との差 `drift_mm` を出して整合性チェックにし、5mm 超で警告。各測定には `tip_source`（Calibrated/Restored/Manual）が残り、解析スクリプトが流用件数を警告する。
  1c. **測量は 1 回きりなので全試行が道連れになる**。同じ場所で 2 点記録する事故を防ぐため、前の点から動いていなければ記録を拒否する（**誤差ではなく移動量で**判定する。誤差で弾くと結果にバイアスがかかる）。測量後に原点が動いたら測量ごと無効化し（`ControlPointMeasure.InvalidateSurvey`）、HUD が測り直しを要求する。
  2. **アンカー復元**: `AnchorPersistence.restoreOnStart` が効いていると起動直後に `OnLocked` が鳴り、「即ロック成功」と誤記録される。`ExperimentController.forceExperimentMode` で自動的に OFF にする。
  3. **試行の独立性**: Quest の `_hasAcceptedSim3`（改悪防止ゲート）と PC の Cc/Cw 蓄積を両方捨てないと試行が独立しない。`AlignmentManager.ResetForNewTrial` と RESET(type=4) が対で必要。**測量はここで捨てない**（試行をまたいで使い回す値なので）。
  4. **手動 Lock と自動アラインの区別**: `OnLocked` は手動 Lock やアンカー復元でも鳴る。収束点は `OnAutoAligned`（自動採用時のみ）を使う。
  4b. **描画の失敗が測定の状態遷移を止める（実機で発生・対策済み）**: `Unlit/Color` がプレイヤービルドで stripping され、`new Material(Shader.Find(...))` が **`ArgumentNullException`**（黙ってピンクを作るのではなく例外）を投げた。その呼び出しが `Record()` / `Place()` の「次の点へ進む」処理より**前**にあったため、記録のたびに `Update()` ごと抜け、**測量が CP_01 から永久に進まなかった**（7 回記録して全部 `CP_01`、`cp_survey_done` が出ず HUD は `CP not surveyed` のまま）。編集モードの「次の点へ進めない」も同一原因。Editor では全ビルトインシェーダが存在するので**再現しない**。対策は 2 層: シェーダを `Always Included Shaders` に登録／**`CpGizmos.Draw()` で描画を状態遷移から切り離す**（状態を進めてから描く・例外は 1 回だけ LogError して握る）。**ギズモを触るコードを足すときはこの順序と `Draw()` を守ること。** 検出は `received/quest_events.jsonl`（`cp_survey` の `name` が進んでいるか、`cp_model_edit action=place` が出ているか）。
  4c. **押して無反応にしない**: `StartSurvey()` は条件不足を `Debug.LogWarning` だけで黙って撥ねていた。実機では触覚も HUD 表示も無いので「ボタンが効かない」と区別が付かず、A が 8 回連打された。ブロック理由は `RejectReason` に載せて HUD（`FirstUnmetReason`）へ出し、拒否の触覚を返す。`Finish()` の「未測の点が残っている」も同様。
  5. **アライン済みで model 側 CP を編集すると誤差が 0 側にバイアスする**: 誤差 `|T(local) − tip|` の `local` は T の推定に使っていない**注釈**なので、**モデルだけを見て**決める限り評価は成立する。しかしオーバーレイが実物に重なって見えている状態で動かすと、無意識に「実物の位置」へ寄せてしまう（答えを見ながら定規を合わせるのと同じ）。よって `ControlPointEdit` は `HasAcceptedSim3` でも `Locked` でもない準備段階だけに限り、編集中にアラインが通ったらその場で打ち切る。さらに全編集を `aligned` フラグ付きでログし、`analyze_experiments.py` が該当セッションを警告する。**編集は測量より前**にすること — PC 側の解析は `local` を `cp_survey` イベントに載ったスナップショットから読む（`experiment_report.py`）ので、測量後に動かすとシーンと PC の値が黙って食い違う。だから編集すると `ControlPointMeasure.InvalidateSurvey` で測量を無効化する。

### CLAUDE への注意
- `server/` の Python を編集したら、純粋計算（`cv/geometry.py` 等）は `python -m tests.test_geometry` で検証する。hloc/pycolmap を使う部分は WSL2 実機でしか確認できないので、ユーザーに実行を依頼してログで確認する。
- 座標規約（verbatim→負スケール反射）と K 規約（正方ピクセル＋中央縦クロップ）を変えないこと（§6 / `Specification.md` §8）。