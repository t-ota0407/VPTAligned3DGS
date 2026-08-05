"""レガシー COLMAP モデル（rigs/frames 無し）を新形式へ移行する。

背景: 古い COLMAP で作った sparse モデルには rigs.bin/frames.bin が無い。新しい
pycolmap(3.13/4.x) がそれを読むと、読み込み直後の `.rigs` アクセスで Windows fatal
exception (access violation) を起こすことがある（hloc/triangulation.py の
create_db_from_model 38行目 `reconstruction.rigs.items()`）。

COLMAP 公式 I/O は「rigs/frames が無いモデルは読み込み時に trivial な rig/frame を
自動生成する」backward-compat 設計（doc/format.rst）。よって **一度読んで書き出す**と
rigs.bin/frames.bin が実体化し、以後は新形式の通常パスで読めて落ちない。
重要: 座標系・点・姿勢は不変なので、既存の .ply（3DGS）との対応はそのまま保たれる。

使い方:
    python -m vptserver.cv.migrate_model scene_data/example1/sparse/0 scene_data/example1/sparse_migrated
"""
import argparse
import sys
from pathlib import Path


def migrate(src, dst):
    """src(レガシー sparse) を読み、rigs.bin/frames.bin 込みで dst に書き出す。dst を返す。"""
    try:
        import pycolmap
    except Exception as e:
        raise SystemExit(f"pycolmap が必要です（pip install pycolmap==3.13.0）。詳細: {e}")

    src = Path(src)
    dst = Path(dst)
    dst.mkdir(parents=True, exist_ok=True)

    # 読み込み（C++ I/O が trivial rig/frame を自動生成）。
    # 注意: .rigs（Python アクセサ）には触らない（落ちる経路）。安全なものだけ参照。
    rec = pycolmap.Reconstruction(str(src))
    print(f"[migrate] loaded: cameras={len(rec.cameras)} images={len(rec.images)} "
          f"points3D={len(rec.points3D)}")

    rec.write(str(dst))   # cameras/images/points3D + rigs/frames を書き出し（C++ 経路）

    has_rigs = (dst / "rigs.bin").exists()
    has_frames = (dst / "frames.bin").exists()
    print(f"[migrate] written -> {dst}  rigs.bin={has_rigs} frames.bin={has_frames}")
    if not (has_rigs and has_frames):
        print("[migrate] WARNING: rigs.bin/frames.bin が生成されませんでした。"
              "pycolmap の自動生成が効いていない可能性があります。"
              "→ COLMAP CLI の model_converter での移行、または本PCでの再構築を検討してください。",
              file=sys.stderr)
    return dst


def main():
    ap = argparse.ArgumentParser(description="レガシー COLMAP モデルを新形式(rigs/frames付き)へ移行")
    ap.add_argument("src", help="入力 sparse モデルのディレクトリ（cameras/images/points3D.bin）")
    ap.add_argument("dst", help="出力ディレクトリ")
    a = ap.parse_args()
    migrate(a.src, a.dst)


if __name__ == "__main__":
    main()
