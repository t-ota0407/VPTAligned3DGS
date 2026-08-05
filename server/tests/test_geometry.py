"""geometry の合成データ検証（numpy のみで実行可）。

実行: cd server && python -m tests.test_geometry   （または python tests/test_geometry.py）
"""
import os
import sys
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from vptserver.cv import geometry as G


def rand_rotation(rng):
    # ランダム proper rotation（QR 分解）
    A = rng.standard_normal((3, 3))
    Q, R = np.linalg.qr(A)
    Q = Q @ np.diag(np.sign(np.diag(R)))
    if np.linalg.det(Q) < 0:
        Q[:, 0] = -Q[:, 0]
    return Q


def quat_to_R(q):
    x, y, z, w = q
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w),     2 * (x * z + y * w)],
        [2 * (x * y + z * w),     1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w),     2 * (y * z + x * w),     1 - 2 * (x * x + y * y)],
    ])


def check(name, cond):
    print(("  OK  " if cond else "  FAIL") + " " + name)
    assert cond, name


def test_case(sigma_true, reflect, noise=0.0, outliers=0, seed=1):
    rng = np.random.default_rng(seed)
    n = 12
    Cc = rng.standard_normal((n, 3)) * 2.0          # COLMAP 中心
    R_true = rand_rotation(rng)
    t_true = rng.standard_normal(3) * 5.0
    # 反射ありを「負スケール」で生成（Unity の負 localScale に相当）
    s_signed = -abs(sigma_true) if reflect else abs(sigma_true)
    Cw = s_signed * (R_true @ Cc.T).T + t_true
    if noise:
        Cw = Cw + rng.standard_normal((n, 3)) * noise
    if outliers:
        oi = rng.choice(n, outliers, replace=False)
        Cw[oi] += rng.standard_normal((outliers, 3)) * 5.0   # 大外れ

    out = G.solve_sim3_ransac(Cc, Cw, thresh_m=0.05, iters=300, seed=2)
    sigma, R, t, q = out["sigma"], out["R"], out["t"], out["quat"]

    tag = f"[sigma={s_signed:+.3f} reflect={reflect} noise={noise} outliers={outliers}]"
    print(tag, f"-> recovered sigma={sigma:+.4f} rmse={out['rmse_m']:.4g} inliers={out['inliers'].sum()}/{n}")
    check(tag + " scale sign", np.sign(sigma) == np.sign(s_signed))
    check(tag + " scale mag", abs(abs(sigma) - abs(s_signed)) < 1e-2 * abs(s_signed) + 1e-3)
    check(tag + " R proper(det=+1)", abs(np.linalg.det(R) - 1.0) < 1e-6)
    # quat が R を再現
    check(tag + " quat<->R", np.allclose(quat_to_R(q), R, atol=1e-5))
    if noise == 0 and outliers == 0:
        check(tag + " R matches", np.allclose(R, R_true, atol=1e-4))
        check(tag + " t matches", np.allclose(t, t_true, atol=1e-3))
        # 全点が良インライア
        check(tag + " all inliers", out["inliers"].sum() == n)


def test_umeyama_basic():
    print("== umeyama 基本（反射なし/あり）==")
    test_case(0.05, reflect=False)      # 正スケール
    test_case(0.95, reflect=True)       # 負スケール（鏡像。実機 -0.957 に相当）
    test_case(2.0, reflect=True)


def test_robust():
    print("== ノイズ＋外れ値（RANSAC）==")
    test_case(0.9, reflect=True, noise=0.005, outliers=3)
    test_case(0.04, reflect=False, noise=0.003, outliers=2)


def test_quat_roundtrip():
    print("== R_to_quat 往復（複数回転）==")
    rng = np.random.default_rng(7)
    for i in range(200):
        R = rand_rotation(rng)
        q = G.R_to_quat(R)
        assert np.allclose(quat_to_R(q), R, atol=1e-5), i
        assert abs(np.linalg.norm(q) - 1) < 1e-6
    print("  OK  200/200 rotations roundtrip")


def test_colmap_quat():
    print("== colmap_quat_to_R 直交性 ==")
    R = G.colmap_quat_to_R(0.5, 0.5, -0.5, 0.5)
    assert np.allclose(R @ R.T, np.eye(3), atol=1e-6)
    assert abs(np.linalg.det(R) - 1) < 1e-6
    print("  OK  orthonormal, det=+1")


if __name__ == "__main__":
    test_umeyama_basic()
    test_robust()
    test_quat_roundtrip()
    test_colmap_quat()
    print("\nALL GEOMETRY TESTS PASSED")
