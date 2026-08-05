"""キーフレームをディスクに保存する。"""
import json
import os

from .keyframe import Keyframe


def save_keyframe(kf: Keyframe, out_dir: str) -> str:
    """req_XXXXXX.jpg と .json を out_dir に保存し、ベースパスを返す。"""
    os.makedirs(out_dir, exist_ok=True)
    base = os.path.join(out_dir, f"req_{kf.req:06d}")
    with open(base + ".jpg", "wb") as f:
        f.write(kf.image_jpg)
    with open(base + ".json", "w", encoding="utf-8") as f:
        json.dump(kf.meta, f, ensure_ascii=False, indent=2)
    return base
