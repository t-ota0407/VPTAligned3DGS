"""確定条件（配置ゲート・一致閾値）を実測で決めるためのシミュレータ。

使い方:
    python analysis/tune_gates.py now     # 現在の既定値で評価
    python analysis/tune_gates.py sweep   # 閾値を掃引して比較

シーンや再ローカライズの精度が変わったら、これで閾値を引き直すこと。

評価軸は 2 つ:
  ロック率  … 60 秒相当（最大 24 キーフレーム）以内に確定できた割合
  確定誤差  … 確定した解の、部屋の隅での位置ずれ

「ロックしない」も「間違ってロックする」も失敗なので、両方を同時に見る必要がある。
雑音は実機の観測（残差 RMSE 1.4〜3.5cm）に合わせる。外れ値率は PnP の
min_inliers ゲートを通過した後の値なので低め（5%）にとる。
"""
import sys
from itertools import product
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from vptserver.cv import geometry as G, gates  # noqa: E402

BB = np.array([[x, y, z] for x in (-5.8, 5.8) for y in (-3.5, 3.5) for z in (-6.8, 6.8)], float)


def shift(a, b):
    return np.linalg.norm((a[0] * (a[1] @ BB.T).T + a[2])
                          - (b[0] * (b[1] @ BB.T).T + b[2]), axis=1).max()


def make(seed, nmax, span_m, curv, pnp_noise, outfrac):
    """span_m: 歩いた距離。curv: 曲がり具合[rad]。"""
    r = np.random.default_rng(seed)
    A = r.normal(size=(3, 3)); Q, _ = np.linalg.qr(A)
    if np.linalg.det(Q) < 0:
        Q[:, 0] *= -1
    sigma, R, t = -0.957, Q, r.normal(scale=2.0, size=3)

    rad = span_m / max(curv, 1e-3)
    th = np.linspace(0, curv, nmax)
    Cw_true = np.c_[rad * np.cos(th) - rad, 1.60 + r.normal(0, .04, nmax), rad * np.sin(th)]
    Cw_true += r.normal(0, .05, (nmax, 3))
    Cw = Cw_true + r.normal(scale=0.005, size=(nmax, 3))
    Cc = ((R.T / sigma) @ (Cw_true - t).T).T + r.normal(scale=pnp_noise / abs(sigma), size=(nmax, 3))
    no = int(round(nmax * outfrac))
    if no:
        Cc[r.choice(nmax, no, replace=False)] += r.normal(scale=1.5, size=(no, 3))
    return sigma, R, t, Cc, Cw


def trial(seed, span_m, curv, cfg, agree_n, agree_m,
          nmax=24, every=3, minkf=5, pnp_noise=0.015, outfrac=0.05):
    sigma, R, t, Cc, Cw = make(seed, nmax, span_m, curv, pnp_noise, outfrac)
    prev, agree = None, 0
    for n in range(minkf, nmax + 1, every):
        est = G.solve_sim3_ransac(Cc[:n], Cw[:n], thresh_m=0.10, iters=100, handedness=-1)
        ok, _ = gates.should_publish(est["sigma"], int(est["inliers"].sum()), n,
                                     est["rmse_m"], cfg, cond=est["cond"])
        if not ok:
            continue
        cur = (est["sigma"], est["R"], est["t"])
        agree = (agree + 1) if (prev is not None and shift(prev, cur) <= agree_m) else 1
        prev = cur
        if agree >= agree_n:
            err = np.linalg.norm((sigma * (R @ BB.T).T + t)
                                 - (cur[0] * (cur[1] @ BB.T).T + cur[2]), axis=1).max()
            return True, err, n
    return False, None, None


# 実験者が取りうる歩き方（歩いた距離, 曲がり具合[rad]）と、その出現しやすさ
WALKS = [
    ("その場 (0.5m, ほぼ直線)", 0.5, 0.2),
    ("短く直線 (3m)",           3.0, 0.15),
    ("短くゆるい弧 (3m)",       3.0, 0.8),
    ("部屋を横切る (6m, 弧)",   6.0, 1.2),
    ("部屋を回る (9m, 大弧)",   9.0, 2.2),
]


def evaluate(cfg, agree_n, agree_m, T=40):
    rows = []
    for label, span, curv in WALKS:
        locked, errs, ns = 0, [], []
        for s in range(T):
            ok, err, n = trial(s, span, curv, cfg, agree_n, agree_m)
            if ok:
                locked += 1; errs.append(err); ns.append(n)
        med = np.median(errs) * 100 if errs else float("nan")
        p90 = np.percentile(errs, 90) * 100 if errs else float("nan")
        bad = (np.array(errs) > 0.20).mean() * 100 if errs else 0.0
        rows.append((label, locked / T * 100, med, p90, bad,
                     np.median(ns) if ns else float("nan")))
    return rows


def show(title, cfg, agree_n, agree_m, T=40):
    print(f"\n=== {title} ===")
    print(f"{'歩き方':<24}{'ロック率':>9}{'誤差中央':>9}{'誤差90%':>9}{'>20cm':>8}{'KF数':>6}")
    for label, lr, med, p90, bad, nn in evaluate(cfg, agree_n, agree_m, T):
        print(f"{label:<24}{lr:8.0f}%{med:8.1f}{p90:9.1f}{bad:7.0f}%{nn:6.0f}")


if __name__ == "__main__":
    mode = sys.argv[1] if len(sys.argv) > 1 else "now"
    if mode == "now":
        g = gates.DEFAULT_GATES
        show(f"現在の既定値: 間隔{g['min_baseline_m']}m / 第2広がり{g['min_second_extent_m']}m / 一致15cm",
             {}, 2, 0.15)
    elif mode == "loose":
        show("案A: 第2広がり0.35m / 一致15cm",
             {"min_baseline_m": 1.0, "min_second_extent_m": 0.35}, 2, 0.15)
    elif mode == "mid":
        show("案B: 第2広がり0.35m / 一致10cm / 一致3回",
             {"min_baseline_m": 1.0, "min_second_extent_m": 0.35}, 3, 0.10)
    elif mode == "sweep":
        print(f"{'第2広がり':>9}{'一致':>7}{'一致回数':>7}   "
              f"{'ロック率(良い歩き方)':>20}{'ロック率(悪い歩き方)':>20}{'誤差>20cm':>10}")
        for e2, am, an in product([0.25, 0.35, 0.50], [0.10, 0.15, 0.25], [2, 3]):
            cfg = {"min_baseline_m": 1.0, "min_second_extent_m": e2}
            rows = evaluate(cfg, an, am, T=30)
            good = np.mean([r[1] for r in rows[3:]])      # 部屋を横切る/回る
            badw = np.mean([r[1] for r in rows[:2]])      # その場/直線
            errs = [r[4] for r in rows[3:] if not np.isnan(r[4])]
            print(f"{e2:9.2f}{am*100:6.0f}cm{an:7d}   {good:19.0f}%{badw:19.0f}%{np.mean(errs):9.0f}%")
