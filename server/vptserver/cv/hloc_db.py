"""オフライン DB 構築（M3 step3 / hloc 経路）。シーンごとに一度だけ実行（GPU 推奨）。

やること:
  1. DB 画像（scene_data/<name>/images/）に **SuperPoint** 局所特徴を抽出。
  2. 既存 COLMAP モデル（sparse/0）の共visibility から DB 同士のペアを作る。
  3. SuperPoint+LightGlue で DB-DB をマッチ。
  4. **既存 COLMAP の姿勢を再利用して** SuperPoint 点群を再三角測量
     → ローカライズ用 SfM モデル（`.ply` と同一座標系）。
  5. 検索用に **NetVLAD** 大域記述子を DB 画像に抽出。

出力は scene_data/<name>/hloc/ に置く（オンラインの localizer が読む）。

なぜ再三角測量が要るか: 既存 points3D は SIFT 由来でキーポイントが SuperPoint と対応しない。
クエリ(パススルー)を SuperPoint+LightGlue で堅牢に照合するには、DB 側も SuperPoint の
3D 点が必要。姿勢は既存 COLMAP のものをそのまま使うので座標系は不変（=3DGS と同じ）。

実行:
  cd server
  pip install -r requirements.txt -r requirements-cv.txt
  pip install torch torchvision           # CUDA 版は pytorch.org 参照
  # hloc は PyPI に無い。GitHub からサブモジュール込みで導入:
  git clone --recursive https://github.com/cvg/Hierarchical-Localization.git
  cd Hierarchical-Localization && python -m pip install -e . && cd ..
  python -m vptserver.cv.hloc_db --scene scene_data/example1

注意: hloc/torch は本サンドボックスでは検証不可。GPU PC で実行し、最後のサマリ
（登録画像数・三角測量点数）が妥当か確認する。API はバージョン差がありうるので、
失敗したらエラーメッセージを共有のこと。
"""
import argparse
import logging
from pathlib import Path

# DB の設定名・出力名は localize と完全に一致させる（→ vptserver/scenes.py）
from ..scenes import FEATURE_CONF, MATCHER_CONF, RETRIEVAL_CONF, SUBMODEL_DIRNAME

logging.basicConfig(level=logging.INFO, format="[hloc_db] %(message)s")
log = logging.getLogger("hloc_db")

COVIS_NUM = 20                            # 三角測量用に各画像の共visibility上位


def _require_hloc():
    try:
        import hloc  # noqa: F401
    except Exception as e:
        raise SystemExit(
            "hloc が見つかりません。GPU PC で次を実行してください:\n"
            "  pip install torch torchvision   # CUDA 版は pytorch.org\n"
            "  pip install hloc\n"
            f"(詳細: {e})"
        )


def build(scene_dir: str, covis_num: int = COVIS_NUM):
    _require_hloc()
    from hloc import (extract_features, match_features, triangulation,
                      pairs_from_covisibility)

    scene = Path(scene_dir)
    images = scene / "images"
    ref_sfm = scene / "sparse" / "0"          # 既存 COLMAP（姿勢の真実）
    out = scene / "hloc"
    out.mkdir(parents=True, exist_ok=True)

    assert images.is_dir(), f"images/ が無い: {images}"
    assert (ref_sfm / "images.bin").exists(), f"sparse/0 が無い: {ref_sfm}"

    # レガシーモデル（rigs.bin 無し）は新形式へ自動移行（pycolmap の .rigs クラッシュ回避）。
    if not (ref_sfm / "rigs.bin").exists():
        from .migrate_model import migrate
        log.info("レガシー COLMAP モデル検出（rigs.bin 無し）→ 新形式へ自動移行…")
        ref_sfm = migrate(ref_sfm, out / "ref_migrated")
        if not (ref_sfm / "rigs.bin").exists():
            raise SystemExit("モデル移行に失敗（rigs.bin 未生成）。migrate_model のログを確認してください。")

    feat_conf = extract_features.confs[FEATURE_CONF]
    match_conf = match_features.confs[MATCHER_CONF]
    retr_conf = extract_features.confs[RETRIEVAL_CONF]

    # 1. DB 局所特徴（SuperPoint）
    log.info("1/5 SuperPoint 抽出 (DB %d 枚)…", len(list(images.glob('*'))))
    feature_path = extract_features.main(feat_conf, images, out)

    # 2. 共visibility ペア（既存モデルの track から）
    log.info("2/5 共visibility ペア生成 (num=%d)…", covis_num)
    sfm_pairs = out / f"pairs-covis{covis_num}.txt"
    pairs_from_covisibility.main(ref_sfm, sfm_pairs, num_matched=covis_num)

    # 3. DB-DB マッチ（LightGlue）
    log.info("3/5 LightGlue マッチ…")
    match_path = match_features.main(match_conf, sfm_pairs, feat_conf["output"], out)

    # 4. 既存姿勢で再三角測量 → SuperPoint モデル（座標系は sparse/0 と同一）
    log.info("4/5 再三角測量（既存 COLMAP 姿勢を再利用）…")
    model = triangulation.main(
        out / SUBMODEL_DIRNAME, ref_sfm, images, sfm_pairs, feature_path, match_path)

    # 5. 検索用 NetVLAD 大域記述子
    log.info("5/5 NetVLAD 大域記述子…")
    extract_features.main(retr_conf, images, out)

    # サマリ
    try:
        nreg = model.num_reg_images()
        npts = model.num_points3D()
    except Exception:
        nreg = npts = "?"
    log.info("完了: 登録画像=%s, 三角測量点=%s", nreg, npts)
    log.info("出力: %s", out)
    log.info("（健全性の目安: 登録画像が DB のほぼ全数、点数が数万以上なら良好）")
    return out


def main():
    ap = argparse.ArgumentParser(description="hloc 再ローカライズ DB のオフライン構築")
    ap.add_argument("--scene", required=True, help="scene_data/<name>（images/ と sparse/0 を含む）")
    ap.add_argument("--covis", type=int, default=COVIS_NUM, help="三角測量用の共visibility数")
    args = ap.parse_args()
    build(args.scene, args.covis)


if __name__ == "__main__":
    main()
