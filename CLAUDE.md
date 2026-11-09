# CLAUDE.md — VPTAligned3DGS

Claude Code がこのリポジトリで作業する際の**規約**。**毎回最初に目を通すこと。**

> 本ファイルは「どう作業するか」だけを書く。**「何が・なぜ・どうなっているか」は文書側が正**:
>
> | 知りたいこと | 見る文書 |
> |---|---|
> | なぜこの設計か・何を捨てたか | [`unity/Specification.md`](unity/Specification.md) |
> | 確定値・座標数式・プロトコル・パラメータ | [`unity/ImplementationSpec.md`](unity/ImplementationSpec.md) |
> | セットアップ・操作・実験手順・トラブルシュート | [`unity/OperationGuide.md`](unity/OperationGuide.md) |
> | PC サーバの詳細 | [`server/README.md`](server/README.md) |
> | プロジェクトの入口 | [`README.md`](README.md) |
>
> **実装に着手する前に、設計書と実装仕様書の両方を読む。** 未確定の箇所を勝手に決めない（ユーザーに確認するか、文書に「要確認」として明示する）。

---

## 1. プロジェクト概要

作成済みの 3D Gaussian Splatting（3DGS）シーンを、Meta Quest 3 のビデオパススルー（VPT）と**自動で位置合わせ**する XR アプリと、その計算をオフロードする PC サーバ。

位置合わせは、パススルーカメラ画像を **3DGS の元になった COLMAP 再構成に再ローカライズ**し、複数キーフレームのカメラ中心から **Umeyama で Sim(3)** を解いて splatRoot に適用する。**自動位置合わせまで実機で動作確認済み。**

1 つのアプリに 2 つのモード: **評価実験**（`GSTestScene`・起動時）と、位置合わせを空間アンカーで永続化して電源断を跨いで再利用する**デモ展示**（`DemoScene`。最初の画面で両スティック 3 秒押込で切替。`Specification.md` §15。**実機確認は未了**）。

---

## 2. 技術スタック（変更したらこの表とコード内コメントを必ず同期）

| 項目 | 採用 | 備考 |
|---|---|---|
| Unity | **6000.3.10f1** | `scalabroom` を基盤に採用 |
| レンダーパイプライン | **URP**（Render Graph Compatibility Mode Off） | BiRP は VR で行列周りの不具合報告あり |
| Render Mode | **OpenXR Multi-Pass** | aras-p v1.1.1 は Single Pass Instanced 非対応。`GaussianSplatURPFeature` は **PC・Mobile 両 Renderer** に付与 |
| Graphics API | **Vulkan** | aras-p は D3D12/Metal/Vulkan のみ。OpenGL ES では描画できない |
| 3DGS | `org.nesnausk.gaussian-splatting`（aras-p）**v1.1.1＋改変（6 ファイル）** | `unity/package/` に同梱（MIT）。改変の一覧は `THIRD-PARTY-NOTICES.md`。採用シーンは約 15 万スプラット |
| XR / パススルー | Unity OpenXR + Meta OpenXR feature ＋ **Meta XR Core SDK / MRUK** | |
| カメラ映像 | **`PassthroughCameraAccess`**（MRUK v81+） | 旧 `WebCamTextureManager`/`PassthroughCameraUtils` は非推奨。権限は `HEADSET_CAMERA` のみ |
| ビルド | Android / ARM64 / IL2CPP | |
| PC 側 CV | **WSL2(Ubuntu-24.04)+CUDA**（hloc / pycolmap） | pycolmap は Windows ネイティブで落ちる。版は `server/setup_wsl.sh` と `constraints-cv.txt` で固定（torch 2.6.0+cu124・hloc `c13273b`） |
| PC 側 受信・返送・GUI | **Windows `run.py`** | 共有フォルダ `/mnt/c` 経由で WSL2 と疎結合。**WSL2 の `localize` は `run.py` が起動・終了する**（`vptserver/cv_launcher.py`）。利用者は `setup_windows.bat` → `start_experiment.bat` / `start_demo.bat` をダブルクリックするだけ |
| Unity MCP | Unity 公式 AI Assistant（`com.unity.ai.assistant`） | Editor 内の Unity Bridge 経由 |

詳細（バージョン要件・権限・プロジェクト設定）は `ImplementationSpec.md` §11。

---

## 3. ディレクトリ構成

```
<repo>/
├─ README.md / README.ja.md      ← プロジェクトの入口
├─ CLAUDE.md                     ← 本ファイル（作業規約）
├─ THIRD-PARTY-NOTICES.md        ← 第三者ライセンス（**商用化の制約を含む**）
├─ images/                       ← 図版
├─ unity/
│  ├─ Specification.md           ← 設計書
│  ├─ ImplementationSpec.md      ← 実装仕様書
│  ├─ OperationGuide.md          ← 運用手順書
│  ├─ LICENSE.md / docs/ / package/  ← aras-p UnityGaussianSplatting（MIT・同梱）
│  └─ projects/VPTAligned3DGS/   ← Unity プロジェクト本体
└─ server/                       ← PC オフロードサーバ（README.md あり）
```

`unity/LICENSE.md` と `unity/docs/` は**同梱している aras-p のもの**で、本プロジェクトの文書ではない。MIT 遵守のため**削除しない**。

---

## 4. 最重要ルール

1. **実装より設計が先。** 位置合わせの方式を変えるなら、コードより先に `Specification.md` を更新してユーザーと合意する。
2. **Unity の状態は MCP 経由で確認・変更する。** 「こうなっているはず」と仮定せず、MCP で実際に問い合わせてから操作する（§5）。`.unity` / `.asset` / `ProjectSettings` の手書き編集は避ける。
3. **座標系の違いを甘く見ない。** 変換規約は一箇所に集約する。**verbatim 読み込み → 反射 → 負スケール**の鎖（`ImplementationSpec.md` §2）と、**K は正方ピクセル＋中央縦クロップ**（同 §2.3）を変えない。
4. **パフォーマンス予算を意識する。** スプラット数が支配的で、Multi-Pass は描画コストがほぼ 2 倍。`.ply` は GPU バッファ 128MB 制限。
5. **破壊的操作の前に確認する。** シーンやアセットを削除・大幅改変する MCP 操作の前にはユーザーに確認する。
6. **文書と実装を同期する。** 確定値を変えたら `ImplementationSpec.md` §10 を更新し、他文書と突き合わせる。**§10 がパラメータの単一の真実。**

---

## 5. Unity MCP（公式 AI Assistant）

- Unity 側で `Edit > Project Settings > AI > Unity MCP` の **Unity Bridge が Running（緑）** であることが前提。Stopped ならユーザーに開始を依頼する。
- 動線: (1) MCP で現在のシーン／GameObject／コンポーネントの状態を**取得** → (2) 変更を**最小単位**で適用 → (3) Console ログを MCP で読み、エラー／警告を確認してから次へ。
- C# の新規作成・編集後は**コンパイルの完了とエラーの有無を MCP で確認**する。エラーを放置して次の操作に進まない。
- Play Mode の開始・停止など状態変更は、MCP の状態取得で結果を確認する。

---

## 6. 壊してはいけない不変条件

実機で事故が起きて対策した箇所。**理由は各文書にあるので、変更する前に必ず読むこと。**

| 不変条件 | 理由の所在 |
|---|---|
| **座標規約**（verbatim → 反射 → 負スケール／K の中央縦クロップ）を変えない | `ImplementationSpec.md` §2 |
| **反射は `handedness=-1` で固定**（自由推定に戻さない）。配置ゲートを外さない | `Specification.md` §6 / `ImplementationSpec.md` §5 |
| **確定は連続一致で判定する**（残差では正しさを判定できない） | `ImplementationSpec.md` §5.3 |
| **ゲートの既定値にシーン固有の量を入れない**（COLMAP の 1 unit が何 m かは再構成ごとに違う）。**品質しきい値は PC 側 `cv.json` に一本化し、Quest 側に複製しない** | `ImplementationSpec.md` §5.5 |
| **試行のリセットは Quest と PC の対で行う**（`ResetForNewTrial` と type=4 RESET） | `ImplementationSpec.md` §9.2 |
| **コードで作るマテリアルのシェーダは `Always Included Shaders` に登録する**。ギズモの描画は `CpGizmos.Draw()` で包み、状態遷移の**後ろ**に置く | `ImplementationSpec.md` §9.5-6 |
| **HUD の文言は英語のみ**（日本語はログと `[Tooltip]` に限る）。全ボタンに物理割当を用意する | `ImplementationSpec.md` §10.4 / `OperationGuide.md` §5-2 |
| **Virtual CP の編集は位置合わせ前だけ**（誤差が 0 側にバイアスするため） | `Specification.md` §11.3 |
| **`HasTarget` を疎通判定に使わない**（`ServerAlive` を使う） | `ImplementationSpec.md` §3.3 |
| **`.ply` を実行時に直接読まない**（必ず Editor でアセット変換を経る） | §7 |
| **OpenXR パススルー層からカメラ画像は取れない**（PCA を使う） | `Specification.md` §4.1 |
| **デモと実験は同じ APK の別シーン。保存ファイルを分ける**（デモは `vptalign_demo_*`。同名だと実験の試行開始の `ClearSaved` がデモの保存を消す）。**実験側の永続化 OFF は `ExperimentController.Awake` で行う**（`Start` だと復元と順序が不定）。切替（両スティック 3 秒）は作業の途中・測った記録が PC 未送達のときは受け付けない。**送信待ちがあるだけでは止めない**（起動時の行が常に残っており、PC 無しでデモに入れなくなる） | `Specification.md` §15.1 / `ImplementationSpec.md` §7.1・§14.1 |
| **位置合わせはワールド座標で保存しない**（原点は起動・電源断で変わる）。**アンカーから見た相対姿勢**で保存し、復元は複数アンカーの多数決で行う | `Specification.md` §15.2・§15.5 / `ImplementationSpec.md` §7 |
| **アンカー追従は `AlignmentManager.IsAnchored` の間だけ**姿勢を書き換える。姿勢を変える経路を足したら `_anchored=false` に落とす（落とさないと古いアンカーが新しい姿勢を上書きする） | `ImplementationSpec.md` §6.1・§7.3 |
| **デモでは合っている確信の無い姿勢を来場者に見せない**（多数決が揃うまで非表示）。来場者に残す入力は X だけ | `Specification.md` §15.4 / `ImplementationSpec.md` §14 |
| **`GSTestScene` と `DemoScene` は共通部分を二重に持つ**。リグ・`Managers`・スプラットの設定を変えたら両方を直す | `ImplementationSpec.md` §14.1 |
| **公開前提: 作者の PC に依存しない**。WSL のディストリ名・パス・ユーザー名を決め打ちしない（既定のディストリに CV 環境があるとは限らない）。WSL 側の版を上げるときは `setup_wsl.sh` の先頭と `constraints-cv.txt` を同時に直し、実機で通す | `ImplementationSpec.md` §1 / `server/README.md`「セットアップ」 |
| **`server/` の `.bat`・`.cmd` は ASCII＋CRLF、`( )` ブロックの中でパスを展開しない**（括弧入りのフォルダ名で壊れる）。日本語の案内は Python 側で出す。`setup_wsl.sh` は LF | `server/README.md`「構成」 |
| **CV の生存（ハートビート）の古さはファイルの更新時刻で測る**（中の `t` は WSL の時計で、スリープ復帰後にずれる） | `ImplementationSpec.md` §1 |
| **`localize` の中で `sys.stdin` を読んで待たない**（起動元の終了の見張りは記述子を `os.read` で読む）。hloc の DataLoader が fork した子が `sys.stdin.close()` で止まり、キーフレームが 1 枚も処理されなくなる | `ImplementationSpec.md` §1（終了の連動） |

---

## 7. 3DGS（UnityGaussianSplatting）の扱い

- `.ply` は Editor の **`Tools > Gaussian Splats > Create GaussianSplatAsset`** で GaussianSplatAsset ＋ データファイルに変換してから `GaussianSplatRenderer` に割り当てる。**実行時に `.ply` を直接読まない。**
- 品質レベルでサイズと描画負荷が変わる。Quest 向けは **Medium 以下**を起点に検証する。
- 位置合わせの結果は `GaussianSplatRenderer` ルート Transform の SRT で表す。**スプラットデータ自体は書き換えない。**
- `unity/package/` は **v1.1.1 に改変を加えたもの**を同梱している（基盤の `scalabroom` から引き継いだ 6 ファイル。合成の全体不透明度・深度の扱い・y 反転など）。一覧は `THIRD-PARTY-NOTICES.md`。**この改変を戻さない**（実機で確認した構成に含まれる）。さらに改変した場合は MIT に従い「改変した旨・改変者」を同ファイルに明記すること。

---

## 8. コーディング規約

- C#。スクリプトは `Assets/Scripts/` 配下に機能単位でフォルダ分け。位置合わせのコア（座標変換・最適化）は MonoBehaviour から分離した純粋クラスにしてテスト可能にする。
- 1 つの変更は小さく。MCP 操作・コンパイル確認・動作確認をこまめに挟む。
- マジックナンバー（スケール係数・軸反転など座標変換に関わる値）は定数化し、根拠をコメントする。
- 外部リポジトリ（UnityGaussianSplatting / Meta サンプル）のコードを取り込む場合は、**出所とライセンスをコメントに残す**。
- **`server/` の Python を編集したら**、純粋計算は `python -m tests`（全部）／`python -m tests.test_geometry` 等で検証する（`server/README.md` のテスト節）。hloc/pycolmap を使う部分は WSL2 実機でしか確認できないので、ユーザーに実行を依頼してログで確認する。WSL・ファイアウォールを操作する部分（`cv_launcher` / `setup_windows` / `setup_wsl.sh`）も同様。**ファイアウォールの変更・WSL の導入など管理者権限の操作は、確認なしに実行しない。**
- **プロトコルを変えたら C# と Python の両側を直す。** `tests/test_liveness.py` と `tests/test_events.py` が鍵名・type 定数の一致を機械的に検査する。

---

## 9. ビルド & 動作確認

- Build Profiles で Android に切替（早めにやる）。ARM64 / IL2CPP / Vulkan。
- Scene List は `GSTestScene`（先頭＝起動時）と `DemoScene` の 2 つ。APK は 1 つで、起動後に両スティック 3 秒でデモへ切り替える（`OperationGuide.md` §10）。
- 実機で確認すべき最低ライン: 両目で 3DGS が正しく描画される / パススルーが表示される / 想定フレームレートが出る / 必要権限のダイアログが出る。
- パフォーマンス低下時はスプラット数・品質レベル・Render Mode・ソート設定を順に見直す。
- ログは MCP 経由（Editor）と実機（`adb logcat`）の両面で確認する。

> **レンダラ実機検証ゲート（クリア済み・2026-06）**: aras-p レンダラの Quest 3 スタンドアロン動作には既知のリスクがあった（GitHub Issues #26/#112/#151/#205: OpenGL ES 非対応・Android でソート不動・**ヘッドポーズに合わせて貼り付かずズレる**・黒画面）。ゼロ構築では 3DGS 表示とパススルー両立が難航し #151 のズレも再現したため、**両立して動作する既存アプリ（`scalabroom`）をコピーして基盤に採用**した。§2 の構成で両眼描画・貼り付き・ソート安定・72fps 圏内を実機で確認済み。**この構成（Multi-Pass / Vulkan / 同梱の v1.1.1＋改変）を崩さないこと。**

---

## 10. やってはいけないこと

- ❌ 設計書・実装仕様書を読まずに位置合わせの実装を始める。
- ❌ MCP で現状確認せずにシーン／コンポーネントを「あるはず」の前提で変更する。
- ❌ 座標系変換をコード各所に散らばらせる。
- ❌ §6 の不変条件を、理由を読まずに変更する。
- ❌ パフォーマンス予算を無視した重い `.ply` を無検証で投入する。
- ❌ 文書だけ直してコードを直さない（またはその逆）。

---

## 11. 参考リンク

- UnityGaussianSplatting（aras-p）: https://github.com/aras-p/UnityGaussianSplatting
- Quest スタンドアロンの既知不具合: Issue [#26](https://github.com/aras-p/UnityGaussianSplatting/issues/26)（OpenGL ES 非対応）/ [#112](https://github.com/aras-p/UnityGaussianSplatting/issues/112)（Android でソート不動）/ [#151](https://github.com/aras-p/UnityGaussianSplatting/issues/151)（頭の動きでズレる）/ [#205](https://github.com/aras-p/UnityGaussianSplatting/issues/205)（黒画面・激重）
- VR 特化 fork（比較候補）: https://github.com/ninjamode/Unity-VR-Gaussian-Splatting , https://github.com/ptc-lexvandersluijs/Unity3DGS_VR
- Meta Passthrough Camera API: https://developers.meta.com/horizon/documentation/unity/unity-pca-overview/
- `PassthroughCameraAccess` への移行: https://developers.meta.com/horizon/documentation/unity/unity-pca-migration-from-webcamtexture/
- Unity-PassthroughCameraApiSamples: https://github.com/oculus-samples/Unity-PassthroughCameraApiSamples
- Unity OpenXR: Meta（Camera / Passthrough）: https://docs.unity3d.com/Packages/com.unity.xr.meta-openxr@2.2/manual/features/camera.html
- Unity MCP（公式 AI Assistant）: https://docs.unity3d.com/Packages/com.unity.ai.assistant@2.0/manual/unity-mcp-get-started.html
- hloc: https://github.com/cvg/Hierarchical-Localization
