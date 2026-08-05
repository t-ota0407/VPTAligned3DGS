"""受信したキーフレーム（画像＋K＋姿勢＋時刻）の表現。"""
from dataclasses import dataclass
import numpy as np


def k_for_image_from_meta(m) -> np.ndarray:
    """META(JSON dict) から送信画像(w,h)に対応する内部行列 K を返す。

    Meta PCA は正方ピクセル。低解像度(例 1280x960)はセンサ(例 1280x1280)の中央クロップ
    なので、素朴な fy*=h/sh は誤り。非クロップ軸から一様スケールを決め、fx,fy は同一スケール、
    cx,cy は中央クロップのオフセットを引いてからスケールする（Specification §8）。
    """
    w, h = float(m["w"]), float(m["h"])
    sw, sh = float(m["sw"]), float(m["sh"])
    scale = (w / sw) if (w / h) >= (sw / sh) else (h / sh)
    tex_w = w / scale
    tex_h = h / scale
    crop_left = (sw - tex_w) / 2.0
    crop_top = (sh - tex_h) / 2.0
    fx = m["fx"] * scale
    fy = m["fy"] * scale
    cx = (m["cx"] - crop_left) * scale
    cy = (m["cy"] - crop_top) * scale
    return np.array([[fx, 0, cx], [0, fy, cy], [0, 0, 1]], dtype=float)


@dataclass
class Keyframe:
    req: int
    meta: dict           # Quest から届く JSON（w,h,sw,sh,fx,fy,cx,cy,px..qw,ts,n,len）
    image_jpg: bytes     # JPEG バイト列

    @property
    def K_sensor(self) -> np.ndarray:
        """センサ解像度基準の内部行列 K。"""
        m = self.meta
        return np.array([[m["fx"], 0, m["cx"]],
                         [0, m["fy"], m["cy"]],
                         [0, 0, 1]], dtype=float)

    def K_for_image(self) -> np.ndarray:
        """送信画像(w,h)に対応する内部行列 K。CV(PnP)はこれを使う（実体は module 関数）。"""
        return k_for_image_from_meta(self.meta)

    @property
    def position(self) -> np.ndarray:
        m = self.meta
        return np.array([m["px"], m["py"], m["pz"]], dtype=float)

    @property
    def quaternion(self) -> np.ndarray:
        """カメラのワールド姿勢クォータニオン (x,y,z,w)。Unity 左手系。"""
        m = self.meta
        return np.array([m["qx"], m["qy"], m["qz"], m["qw"]], dtype=float)
