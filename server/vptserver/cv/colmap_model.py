"""COLMAP sparse モデル（cameras/images/points3D, .bin）の読み込み。

numpy のみ依存（pycolmap 不要）。再ローカライズの DB として `sparse/0` を読む。
座標系: COLMAP は右手系・カメラは z 前方/y 下。各画像の姿勢は world→cam（X_cam = R·X_world + t）。
ここで world = COLMAP 再構成フレーム = 3DGS(.ply) モデルフレームの数値（aras-p は verbatim 読み込み）。
"""
import os
import struct
import numpy as np

# COLMAP camera model id -> (name, num_params)
CAMERA_MODELS = {
    0: ("SIMPLE_PINHOLE", 3), 1: ("PINHOLE", 4), 2: ("SIMPLE_RADIAL", 4),
    3: ("RADIAL", 5), 4: ("OPENCV", 8), 5: ("OPENCV_FISHEYE", 8),
    6: ("FULL_OPENCV", 12), 7: ("FOV", 5), 8: ("SIMPLE_RADIAL_FISHEYE", 4),
    9: ("RADIAL_FISHEYE", 5), 10: ("THIN_PRISM_FISHEYE", 12),
}


class Camera:
    def __init__(self, cam_id, model, width, height, params):
        self.id = cam_id
        self.model = model
        self.width = width
        self.height = height
        self.params = np.asarray(params, float)

    def K(self):
        """内部行列 K（PINHOLE/SIMPLE_PINHOLE のみ厳密。歪みモデルは焦点/主点のみ抽出）。"""
        p = self.params
        if self.model == "PINHOLE":
            fx, fy, cx, cy = p
        elif self.model == "SIMPLE_PINHOLE":
            fx = fy = p[0]; cx, cy = p[1], p[2]
        elif self.model in ("OPENCV", "FULL_OPENCV", "OPENCV_FISHEYE"):
            fx, fy, cx, cy = p[0], p[1], p[2], p[3]
        elif self.model in ("SIMPLE_RADIAL", "RADIAL", "SIMPLE_RADIAL_FISHEYE", "RADIAL_FISHEYE"):
            fx = fy = p[0]; cx, cy = p[1], p[2]
        else:
            fx = fy = p[0]; cx, cy = p[1], p[2]
        return np.array([[fx, 0, cx], [0, fy, cy], [0, 0, 1]], float)


class Image:
    def __init__(self, image_id, qvec, tvec, cam_id, name):
        self.id = image_id
        self.qvec = np.asarray(qvec, float)   # (qw, qx, qy, qz)  world->cam
        self.tvec = np.asarray(tvec, float)   # world->cam
        self.camera_id = cam_id
        self.name = name

    def R(self):
        from .geometry import colmap_quat_to_R
        return colmap_quat_to_R(*self.qvec)

    def center(self):
        """カメラ中心（COLMAP world 座標）= -R^T t。"""
        return -self.R().T @ self.tvec


class Point3D:
    __slots__ = ("id", "xyz", "rgb", "error", "track")
    def __init__(self, pid, xyz, rgb, error, track):
        self.id = pid
        self.xyz = np.asarray(xyz, float)
        self.rgb = rgb
        self.error = error
        self.track = track  # list[(image_id, point2D_idx)]


def _read(fmt, f):
    return struct.unpack(fmt, f.read(struct.calcsize(fmt)))


def read_cameras_bin(path):
    cams = {}
    with open(path, "rb") as f:
        n = _read("<Q", f)[0]
        for _ in range(n):
            cam_id, model_id, w, h = _read("<iiQQ", f)
            name, npar = CAMERA_MODELS.get(model_id, ("UNKNOWN", 0))
            params = _read("<%dd" % npar, f) if npar else ()
            cams[cam_id] = Camera(cam_id, name, w, h, params)
    return cams


def read_images_bin(path):
    images = {}
    with open(path, "rb") as f:
        n = _read("<Q", f)[0]
        for _ in range(n):
            image_id = _read("<I", f)[0]
            qvec = _read("<dddd", f)        # qw,qx,qy,qz
            tvec = _read("<ddd", f)
            cam_id = _read("<I", f)[0]
            name = b""
            while True:
                c = f.read(1)
                if c == b"\x00":
                    break
                name += c
            n2d = _read("<Q", f)[0]
            f.read(n2d * (8 + 8 + 8))       # x,y(2 double) + point3D_id(int64) は今は不要
            images[image_id] = Image(image_id, qvec, tvec, cam_id, name.decode())
    return images


def read_points3D_bin(path, with_tracks=False):
    pts = {}
    with open(path, "rb") as f:
        n = _read("<Q", f)[0]
        for _ in range(n):
            pid = _read("<Q", f)[0]
            xyz = _read("<ddd", f)
            rgb = _read("<BBB", f)
            err = _read("<d", f)[0]
            tl = _read("<Q", f)[0]
            if with_tracks:
                track = [_read("<II", f) for _ in range(tl)]
            else:
                f.read(tl * 8)
                track = None
            pts[pid] = Point3D(pid, xyz, rgb, err, track)
    return pts


class ColmapModel:
    def __init__(self, cameras, images, points3D):
        self.cameras = cameras
        self.images = images
        self.points3D = points3D

    @classmethod
    def load(cls, sparse_dir, with_tracks=False):
        """sparse_dir に cameras.bin / images.bin / points3D.bin がある前提で読み込む。"""
        cams = read_cameras_bin(os.path.join(sparse_dir, "cameras.bin"))
        imgs = read_images_bin(os.path.join(sparse_dir, "images.bin"))
        pts = read_points3D_bin(os.path.join(sparse_dir, "points3D.bin"), with_tracks)
        return cls(cams, imgs, pts)

    def image_centers(self):
        """{name: center(world)} を返す（健全性確認・可視化用）。"""
        return {im.name: im.center() for im in self.images.values()}

    def points_xyz(self):
        return np.array([p.xyz for p in self.points3D.values()], float)

    def summary(self):
        cam = next(iter(self.cameras.values()))
        return (f"cameras={len(self.cameras)} (model={cam.model} {cam.width}x{cam.height}) "
                f"images={len(self.images)} points3D={len(self.points3D)}")
