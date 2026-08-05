"""Sim(3) の一意性・退化検出・スケールゲートの検証。

守りたいこと（実機で「採用1件で終わったが位置合わせが間違っている」を再現した原因）:

  1. **反射を自由推定すると共面配置で解が一意に決まらない。**
     3 点は必ず同一平面上に乗る。反射を許すと「その平面に関する鏡映を掛けた解」が
     完全に同じ残差を与えるため、残差では 2 解を区別できずコインフリップになる。
     handedness=-1（反射固定）でこれが消えることを確認する。

  2. **反射を固定しても共線配置（一直線に歩いた）は直らない。**
     直線まわりの回転が拘束されないため。配置ゲートが弾くことを確認する。

  3. 既知スケールからの逸脱ゲートが、実機で観測された異常値を棄却すること。

実行: python -m tests.test_alignment_gates
"""
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from vptserver.cv import geometry as G
from vptserver.cv import gates

_fails = []


def check(cond, label):
    print(("  OK   " if cond else "  FAIL ") + label)
    if not cond:
        _fails.append(label)


def _rand_sim3(rng, sigma=-0.957):
    A = rng.normal(size=(3, 3))
    Q, _ = np.linalg.qr(A)
    if np.linalg.det(Q) < 0:
        Q[:, 0] *= -1                     # proper rotation
    return sigma, Q, rng.normal(scale=2.0, size=3)


def _layout(rng, n, kind):
    if kind == "coplanar":                # 頭の高さを変えずに部屋を歩いた状況
        return np.c_[rng.uniform(-3, 3, n), np.zeros(n), rng.uniform(-3, 3, n)]
    if kind == "collinear":               # 一直線に歩いた状況
        u = rng.normal(size=3); u /= np.linalg.norm(u)
        return np.outer(rng.uniform(-3, 3, n), u)
    return rng.uniform(-3, 3, (n, 3))     # 立体的


def _failure_rate(kind, n, handedness, trials=60, noise=0.004, seed=3):
    """真の変換とどれだけ違う解になるかを、任意点の写り先で測る。"""
    rng = np.random.default_rng(seed)
    bad = 0
    last = None
    for _ in range(trials):
        sigma, R, t = _rand_sim3(rng)
        Cw_true = _layout(rng, n, kind)
        Cw = Cw_true + rng.normal(scale=noise, size=(n, 3))
        Cc = ((R.T / sigma) @ (Cw_true - t).T).T
        est = G.solve_sim3_ransac(Cc, Cw, thresh_m=0.10, iters=50, handedness=handedness)
        last = est
        probe = rng.normal(scale=3.0, size=(10, 3))
        p_true = sigma * (R @ probe.T).T + t
        p_est = est["sigma"] * (est["R"] @ probe.T).T + est["t"]
        if np.abs(p_true - p_est).max() > 0.10:
            bad += 1
    return bad / trials, last


def test_reflection_ambiguity():
    print("== 反射の自由推定は共面配置で一意に決まらない ==")
    free, _ = _failure_rate("coplanar", 6, None)
    fixed, _ = _failure_rate("coplanar", 6, -1)
    print(f"     共面6点: 自由推定 {free*100:.0f}% / 反射固定 {fixed*100:.0f}% が誤り")
    check(free > 0.25, f"自由推定は共面で高頻度に失敗する（実測 {free*100:.0f}%）")
    check(fixed <= 0.05, f"反射固定なら共面でも失敗しない（実測 {fixed*100:.0f}%）")

    free3, _ = _failure_rate("coplanar", 3, None)
    check(free3 > 0.25, f"3点でも同様に失敗する（実測 {free3*100:.0f}%）")


def test_handedness_sign():
    print("== 反射固定は必ず負スケールを返す ==")
    rng = np.random.default_rng(11)
    signs = []
    for _ in range(40):
        sigma, R, t = _rand_sim3(rng)
        Cw_true = _layout(rng, 6, "coplanar")
        Cc = ((R.T / sigma) @ (Cw_true - t).T).T
        est = G.solve_sim3_ransac(Cc, Cw_true, thresh_m=0.10, iters=30, handedness=-1)
        signs.append(est["sigma"] < 0)
    check(all(signs), "handedness=-1 では常に sigma<0")

    # +1 を渡せば必ず正（自由推定に戻す逃げ道が効くことの確認）
    sigma, R, t = _rand_sim3(rng, sigma=+0.9)
    Cw_true = _layout(rng, 6, "generic")
    Cc = ((R.T / sigma) @ (Cw_true - t).T).T
    est = G.solve_sim3_ransac(Cc, Cw_true, thresh_m=0.10, iters=30, handedness=+1)
    check(est["sigma"] > 0, "handedness=+1 では常に sigma>0")


def test_collinear_not_fixed_by_handedness():
    print("== 共線配置は反射固定では直らない。配置ゲートで弾く ==")
    fixed, last = _failure_rate("collinear", 6, -1, trials=40)
    print(f"     共線6点: 反射固定でも {fixed*100:.0f}% が誤り")
    check(fixed > 0.5, "共線配置は反射を固定しても失敗する（別の退化）")

    ok, why = gates.check_geometry(last["cond"])
    check(not ok, f"配置ゲートが共線を棄却する: {why}")

    _, good = _failure_rate("generic", 6, -1, trials=1)
    ok2, why2 = gates.check_geometry(good["cond"])
    check(ok2, f"立体的な配置は通す: {why2}")


def test_conditioning_metric():
    print("== center_conditioning の基本性質 ==")
    line = np.c_[np.linspace(0, 3, 8), np.zeros(8), np.zeros(8)]
    c = G.center_conditioning(line)
    check(c["baseline"] > 2.9 and c["extent2"] < 1e-6, "一直線: 第2広がりがほぼ0")

    plane = np.c_[np.linspace(0, 3, 8), np.zeros(8), np.linspace(0, 2, 8) ** 1.5]
    c = G.center_conditioning(plane)
    check(c["extent2"] > 0.1, "平面上に散らばれば第2広がりが出る")

    tiny = np.array([[0, 0, 0], [0.05, 0, 0], [0, 0.05, 0]], float)
    ok, why = gates.check_geometry(G.center_conditioning(tiny))
    check(not ok, f"移動量が小さすぎる配置を棄却: {why}")


def test_expected_scale_gate():
    print("== 既知スケールからの逸脱ゲート（実機で観測された値の再現）==")
    cfg = {"expected_scale": 0.96, "scale_tol": 0.10}
    for sig, want_ok, label in [(-0.96, True, "正常"),
                                (-1.02, True, "許容内 +6%"),
                                (+0.71, False, "鏡像（実機で観測）"),
                                (-1.79, False, "退化（実機で観測）"),
                                (-0.67, False, "退化（実機で観測）")]:
        ok, why = gates.should_publish(sig, 5, 6, 0.02, cfg)
        check(ok == want_ok, f"sigma={sig:+.2f} {label} → {'採用' if ok else '棄却'} ({why})")

    ok, _ = gates.should_publish(-1.79, 5, 6, 0.02, {})   # 未設定なら従来どおり通る
    check(ok, "expected_scale 未設定なら従来の緩い範囲で判定する（後方互換）")


def test_publish_uses_geometry():
    print("== should_publish に配置を渡すと退化を弾く ==")
    line = np.c_[np.linspace(0, 3, 6), np.zeros(6), np.zeros(6)]
    ok, why = gates.should_publish(-0.96, 6, 6, 0.01, {}, cond=G.center_conditioning(line))
    check(not ok, f"共線配置は他が良くても棄却: {why}")

    good = np.array([[0, 0, 0], [2, 0, 0.3], [1, 0.4, 2], [0.2, 0, 1.5],
                     [2.2, 0.5, 1.8], [1.1, 0.2, 0.9]], float)
    ok, why = gates.should_publish(-0.96, 6, 6, 0.01, {}, cond=G.center_conditioning(good))
    check(ok, f"立体的な配置は通す: {why}")


if __name__ == "__main__":
    test_reflection_ambiguity()
    test_handedness_sign()
    test_collinear_not_fixed_by_handedness()
    test_conditioning_metric()
    test_expected_scale_gate()
    test_publish_uses_geometry()
    print()
    if _fails:
        print(f"{len(_fails)} FAILED:")
        for f in _fails:
            print("  -", f)
        sys.exit(1)
    print("ALL ALIGNMENT GATE TESTS PASSED")
