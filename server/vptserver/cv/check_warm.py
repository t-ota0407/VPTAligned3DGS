"""モデルと DB を読み込んだままの経路（warm_hloc）が、hloc の main を呼ぶ以前の経路と同じ結果を出すかを確かめる。

保存済みのキーフレーム（session_* フォルダ）を同じ区切りで両方の経路に流し、検索ペア・照合・推定姿勢・
PnP インライア数を突き合わせ、かかった時間も並べる。warm_hloc.py か hloc の版を変えたら実行する。
WSL2 の CV 環境で（作業ファイルは一時フォルダに書き、シーンやセッションには書かない）:

  cd /mnt/c/<repo への WSL2 パス>/server
  python -m vptserver.cv.check_warm --scene scene_data/irt_short2 --keyframes received/session_20261002_232845
"""
import argparse
import shutil
import sys
import tempfile
import time
from pathlib import Path

import h5py
import numpy as np
import pycolmap

from .localize import Localizer, read_keyframe


def _groups(path):
    out = {}
    with h5py.File(str(path), "r") as fd:
        for name in fd:
            out[name] = {k: v.__array__() for k, v in fd[name].items()}
    return out


def _flat_groups(path):
    """照合の h5 は「クエリ名/DB 名」の 2 階層（names_to_pair の区切りが "/"）。末端のグループを集める。"""
    out = {}
    with h5py.File(str(path), "r") as fd:
        def visit(name, obj):
            if isinstance(obj, h5py.Group) and any(isinstance(v, h5py.Dataset) for v in obj.values()):
                out[name] = {k: v.__array__() for k, v in obj.items()}
        fd.visititems(visit)
    return out


def main():
    ap = argparse.ArgumentParser(description="warm_hloc と hloc の main の結果を突き合わせる")
    ap.add_argument("--scene", required=True)
    ap.add_argument("--keyframes", required=True, help="req_*.json / .jpg のある session フォルダ")
    ap.add_argument("--batches", default="1,4,8", help="区切り（枚数）。残りは最後にまとめる")
    a = ap.parse_args()

    session = Path(a.keyframes)
    kfs = []
    for m in sorted(session.glob("req_*.json")):
        try:
            nm, intr, _c, _meta = read_keyframe(m)
            kfs.append((nm, intr))
        except (OSError, ValueError):
            pass
    sizes = [int(x) for x in a.batches.split(",") if x.strip()]
    batches, i = [], 0
    for n in sizes:
        if i < len(kfs):
            batches.append(kfs[i:i + n])
            i += n
    if i < len(kfs):
        batches.append(kfs[i:])
    print(f"keyframes: {len(kfs)}  batches: {[len(b) for b in batches]}", flush=True)

    loc = Localizer(a.scene)
    if loc.warm is None:
        sys.exit("warm 経路を用意できませんでした（上のログを見てください）")
    warm = loc.warm
    tmp = Path(tempfile.mkdtemp(prefix="vpt_check_warm_"))
    ok = True
    try:
        for bi, batch in enumerate(batches):
            names = [b[0] for b in batch]
            intr = {b[0]: b[1] for b in batch}
            res, dt, work = {}, {}, {}
            for path in ("plain", "warm"):
                work[path] = tmp / path / f"b{bi}"
                loc.warm = warm if path == "warm" else None
                # PnP の RANSAC（COLMAP）の乱数はスレッドごとに続きから使われるので、そろえないと
                # 同じ入力でも経路ごとに結果がわずかに変わる（以前の経路どうしでも同じ）
                pycolmap.set_random_seed(0)
                t0 = time.time()
                res[path] = loc.localize(session, names, intr, work[path])
                dt[path] = time.time() - t0
            loc.warm = warm
            p, w = res["plain"], res["warm"]
            same_pairs = (work["plain"] / "loc_pairs.txt").read_text() == (work["warm"] / "loc_pairs.txt").read_text()
            mp, mw = _flat_groups(work["plain"] / "loc_matches.h5"), _flat_groups(work["warm"] / "loc_matches.h5")
            m_same = set(mp) == set(mw) and all(np.array_equal(mp[k]["matches0"], mw[k]["matches0"]) for k in mp)
            fp = _groups(work["plain"] / (loc.feat_conf["output"] + ".h5"))
            fw = _groups(work["warm"] / (loc.feat_conf["output"] + ".h5"))
            f_same = all(np.array_equal(fp[n][k], fw[n][k]) for n in names for k in fp[n])
            dpos = max((float(np.abs(p[n]["center"] - w[n]["center"]).max()) for n in p if n in w), default=0.0)
            inl_same = set(p) == set(w) and all(p[n]["num_inliers"] == w[n]["num_inliers"] for n in p)
            line_ok = same_pairs and m_same and f_same and inl_same and dpos < 1e-6
            ok &= line_ok
            print(f"batch {bi} ({len(names):2d}): plain {dt['plain']:5.1f} s  warm {dt['warm']:5.1f} s  | "
                  f"features {'=' if f_same else 'DIFF'}  pairs {'=' if same_pairs else 'DIFF'}  "
                  f"matches {'=' if m_same else 'DIFF'}  inliers {'=' if inl_same else 'DIFF'}  "
                  f"max center diff {dpos:.2e}  -> {'OK' if line_ok else 'DIFFERENT'}", flush=True)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    print("ALL SAME" if ok else "SOME RESULTS DIFFER")
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
