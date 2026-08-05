#!/usr/bin/env python3
"""トラッキング原点が動いた試行を見つける（データが使えるかどうかの判定）。

なぜ要るか:
  自動位置合わせは「Quest のワールド座標系は固定」という前提の上に成り立っている。
  実際には recenter・トラッキングロスからの復帰・空間の再ローカライズで、原点が
  **ヨー回転＋水平移動** の形で飛ぶ。飛んでも残差やインライア率は悪化しないので、
  数値だけ見ていると「よく合った解」に見えたまま、実世界からはメートル単位でずれる。

  実機で 3 試行を突き合わせたとき、試行ごとの解が互いに
      スケール 1.000 / 回転軸 (0.00, 1.00, 0.00) / 鉛直移動 0.00 m
  だけ違っていた。推定誤差ならこうはならない（誤差はいちばん弱く拘束される傾き方向に出る。
  ヨーと水平位置は逆に最もよく決まる）。つまり原点そのものが回っていた。

2 種類の異常を見る:
  A. 試行内 … キーフレーム残差が終盤で単調に増える。収集の途中で原点が動いた。
               解は残った側に引っ張られ、実験前に測量した CP はもう一方の座標系の値になる。
  B. 試行間 … 解どうしが「純ヨー回転＋水平移動・スケール 1」で食い違う。
               試行の合間に原点が動いた。この場合、測量は**全試行**に対して古くなる。

使い方:
    python analysis/check_tracking.py received/session_20260730_215016 ...
    python analysis/check_tracking.py received/session_*        # まとめて
"""
import argparse
import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from vptserver.cv import geometry as G  # noqa: E402

# --- 判定のしきい値（根拠はモジュール冒頭）---
TAIL_N = 4              # 末尾この枚数を「終盤」とみなす
TAIL_FACTOR = 5.0       # 終盤の残差中央値が全体中央値のこの倍を超えたら異常
TAIL_ABS_M = 0.20       # かつ絶対値でこれ[m]を超えること（微小な悪化を騒がない）
YAW_AXIS_DOT = 0.97     # 回転軸が鉛直とみなせる内積
YAW_MIN_DEG = 3.0       # これ未満の食い違いは推定誤差の範囲
SCALE_TOL = 0.05        # |scale-1| がこれ以内なら「原点が動いただけ」
VERT_TOL_M = 0.10       # 鉛直移動がこれ以内なら水平移動のみ


def quat_to_R(q):
    x, y, z, w = q
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                     [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                     [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


def load_session(d):
    """kf_localized.jsonl から採用キーフレームを撮影順に読む。"""
    p = Path(d) / "kf_localized.jsonl"
    if not p.exists():
        return None
    rows = []
    for line in p.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            r = json.loads(line)
        except json.JSONDecodeError:
            continue
        if r.get("accepted") and "center_colmap" in r and "center_world" in r:
            rows.append(r)
    if len(rows) < 5:
        return None
    label = ""
    tj = Path(d) / "trial.json"
    if tj.exists():
        try:
            label = json.loads(tj.read_text()).get("label", "")
        except Exception:
            pass
    return {
        "dir": Path(d),
        "label": label,
        "Cc": np.array([r["center_colmap"] for r in rows], float),
        "Cw": np.array([r["center_world"] for r in rows], float),
        "names": [r.get("name") for r in rows],
    }


def residuals(res, Cc, Cw):
    pred = (res["sigma"] * (quat_to_R(res["quat"]) @ Cc.T)).T + res["t"]
    return np.linalg.norm(pred - Cw, axis=1)


def check_within(s):
    """試行内の異常（終盤で残差が跳ね上がる）。返り値: (ok, message, res, resid)。"""
    res = G.solve_sim3_ransac(s["Cc"], s["Cw"], thresh_m=0.10, handedness=-1)
    e = residuals(res, s["Cc"], s["Cw"])
    if len(e) <= TAIL_N + 2:
        return True, "枚数が少なく判定不能", res, e
    tail = float(np.median(e[-TAIL_N:]))
    body = float(np.median(e[:-TAIL_N]))
    bad = tail > TAIL_ABS_M and tail > TAIL_FACTOR * max(body, 1e-3)
    msg = (f"終盤 {TAIL_N} 枚の残差 中央 {tail * 100:.0f} cm / それ以外 {body * 100:.1f} cm")
    return (not bad), msg, res, e


def check_between(sessions, sols):
    """試行間の食い違いが「純ヨー回転＋水平移動」になっていないか。"""
    findings = []
    for i in range(len(sessions)):
        for j in range(i + 1, len(sessions)):
            ri, rj = sols[i], sols[j]
            Ri, Rj = quat_to_R(ri["quat"]), quat_to_R(rj["quat"])
            # world_j = (Tj o Ti^-1)(world_i)
            sc = rj["sigma"] / ri["sigma"]
            Rrel = Rj @ Ri.T
            trel = rj["t"] - sc * Rrel @ ri["t"]
            ang = float(np.degrees(np.arccos(np.clip((np.trace(Rrel) - 1) / 2, -1, 1))))
            ax = np.array([Rrel[2, 1] - Rrel[1, 2], Rrel[0, 2] - Rrel[2, 0],
                           Rrel[1, 0] - Rrel[0, 1]])
            n = np.linalg.norm(ax)
            ax = ax / n if n > 1e-9 else np.array([0.0, 1.0, 0.0])
            vertical = abs(float(ax[1]))
            pure_yaw = (ang >= YAW_MIN_DEG and vertical >= YAW_AXIS_DOT
                        and abs(sc - 1.0) <= SCALE_TOL and abs(float(trel[1])) <= VERT_TOL_M)
            findings.append({
                "i": i, "j": j, "deg": ang, "axis_vertical": vertical,
                "scale": sc, "t": trel, "pure_yaw": pure_yaw,
            })
    return findings


def main():
    ap = argparse.ArgumentParser(description="トラッキング原点の異常を検出する")
    ap.add_argument("sessions", nargs="+", help="received/session_* （複数可）")
    args = ap.parse_args()

    sessions, sols = [], []
    print("== 試行内のチェック（収集中に原点が動いていないか）==")
    for d in args.sessions:
        s = load_session(d)
        if s is None:
            print(f"  {Path(d).name:28s} スキップ（採用キーフレームが 5 枚未満）")
            continue
        ok, msg, res, e = check_within(s)
        mark = "OK  " if ok else "異常"
        print(f"  {mark} {Path(d).name:28s} {s['label']:10s} "
              f"KF {len(e):3d}  rmse {res['rmse_m'] * 100:5.1f} cm  {msg}")
        if not ok:
            worst = "  ".join(f"{v * 100:.0f}" for v in e[-TAIL_N - 2:])
            print(f"       └ 末尾の残差[cm]: {worst}  "
                  "→ 収集の途中で原点が動いた。この試行は使えない。")
        sessions.append(s)
        sols.append(res)

    if len(sessions) < 2:
        return

    print()
    print("== 試行間のチェック（合間に原点が動いていないか）==")
    print("   解どうしの差が「スケール1・鉛直軸まわりの回転・水平移動」なら、")
    print("   推定誤差ではなくワールド座標系そのものが動いている。")
    bad = 0
    for f in check_between(sessions, sols):
        a, b = Path(sessions[f["i"]]["dir"]).name[-6:], Path(sessions[f["j"]]["dir"]).name[-6:]
        tag = "原点が回った" if f["pure_yaw"] else ""
        if f["pure_yaw"]:
            bad += 1
        print(f"  {a} -> {b}  回転 {f['deg']:6.1f}°  軸の鉛直成分 {f['axis_vertical']:.3f}  "
              f"スケール {f['scale']:.4f}  水平移動 {np.linalg.norm(f['t'][[0, 2]]):.2f} m  "
              f"鉛直 {f['t'][1]:+.2f} m  {tag}")

    print()
    if bad:
        print(f"→ {bad} 組で原点の移動を検出。**それらの試行は互いに別の座標系で測っている。**")
        print("   ヘッドセットを外さない／同じガードリアン空間を維持する／")
        print("   Meta ボタン長押し（再センタリング）を押さない、で再取得してください。")
        print("   Quest 側は TrackingOriginWatcher が検出して試行を自動破棄します。")
    else:
        print("→ 原点の移動は検出されませんでした。試行間の差は推定誤差の範囲です。")


if __name__ == "__main__":
    main()
