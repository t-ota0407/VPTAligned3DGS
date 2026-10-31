"""シーン（server/scene_data/<name>/）の再ローカライズ DB ができているかの判定。

Windows 側（run.py のシーン一覧・setup_windows）と WSL2 側（localize のシーン自動検出・hloc_db）の
**両方がここを使う**。以前は 3 か所に別々の判定があり、基準が食い違っていた
（DB を作りかけて止まったシーンが run.py の一覧には出るのに、localize はそれを読めずに落ちる）。
標準ライブラリだけで動くこと（Windows 側には hloc / pycolmap が無い）。
"""
from pathlib import Path

# DB の中身を決める hloc の設定名。DB の構築（hloc_db）とオンライン（localize）で**完全に一致**させる（混在禁止）
FEATURE_CONF = "superpoint_max"          # 局所特徴（高解像で取りこぼし減）
MATCHER_CONF = "superpoint+lightglue"    # マッチャ
RETRIEVAL_CONF = "netvlad"               # 大域記述子（画像検索）

# hloc_db の出力名
SUBMODEL_DIRNAME = "sfm_superpoint+lightglue"   # 三角測量済みモデル（.ply と同じ座標系）
NETVLAD_FILE = "global-feats-netvlad.h5"         # hloc_db の最後の段階の出力（これがあれば DB は完成）


def scene_state(scene_dir):
    """"built"（DB あり）/ "partial"（DB を作りかけて止まった）/ "raw"（DB 未作成）/ "invalid"（COLMAP が無い）。"""
    d = Path(scene_dir)
    h = d / "hloc"
    if (h / SUBMODEL_DIRNAME).is_dir() and (h / NETVLAD_FILE).is_file():
        return "built"
    if not (d / "images").is_dir() or not (d / "sparse" / "0" / "images.bin").is_file():
        return "invalid"
    return "partial" if h.is_dir() else "raw"


def list_built_scenes(scene_root="scene_data"):
    """DB ができているシーンの一覧。

    WSL2(Linux) の localize も読むので、パスは forward slash（as_posix）で統一する。
    """
    root = Path(scene_root)
    if not root.is_dir():
        return []
    return [d.as_posix() for d in sorted(root.iterdir())
            if d.is_dir() and scene_state(d) == "built"]


def autodetect_scene(scene_root="scene_data"):
    """DB ができているシーンが 1 つだけなら返す（曖昧なら None）。"""
    b = list_built_scenes(scene_root)
    return b[0] if len(b) == 1 else None
