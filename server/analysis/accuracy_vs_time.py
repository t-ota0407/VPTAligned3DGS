#!/usr/bin/env python3
"""収集時間と位置合わせ精度の関係を出す（評価の主結果）。

なぜこの指標か:
  「ロックまでの時間」は品質ゲートの閾値の関数でしかなく、閾値を変えれば数字も変わる。
  測っているのは手法ではなく設定になってしまう。
  一方「何秒データを集めたら、どれくらい合うか」は手法そのものの性質で、
  読者が知りたいこと（どれだけ見回せばよいか）にも直接答える。

仕組み:
  1 回の歩行データから曲線全体を再構成する。キーフレームごとのローカライズ結果
  （`kf_localized.jsonl`）が保存してあるので、「最初の t 秒分だけ」で Sim(3) を
  解き直す操作を t を変えて繰り返せばよい。hloc の再実行は不要。

誤差の定義（循環しない）:
  コントロールポイントには 2 つの独立した座標がある。
    - local … Editor で 3DGS モデル上に定義した座標（アラインに依存しない）
    - tip   … 校正済みコントローラ先端で実測した物理位置（ワールド座標）
  候補変換 T に対する誤差は |T(local) - tip|。T の推定には一切使っていないので
  評価として成立する。

前提:
  tip は **実験の前に 1 回だけ測る（測量）**。model 側（Editor で配置）も実物側（部屋）も
  試行では変わらないので、試行ごとに測り直す必要は無い（→ CLAUDE.md §13）。
  ただし tip はワールド座標なので、キーフレームと**同じワールド原点**でなければならない。
  測量から試行までの間にヘッドセットを外す・再センタリングすると原点が飛び、測量は無効になる
  （Quest 側の TrackingOriginWatcher が検出して測り直させる）。

使い方:
    python analysis/accuracy_vs_time.py received/session_20260730_120000 logs/exp_xxx.jsonl
    python analysis/accuracy_vs_time.py <session> <jsonl> --trial 1 --outdir out
"""
import argparse
import csv
import json
import sys
from datetime import datetime
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from vptserver.cv import geometry as G, gates  # noqa: E402


# ---------------------------------------------------------------- 読み込み

def parse_ts(s):
    """Quest が送る ISO8601（'2026-07-29T14:32:28.5289130Z'）を datetime に。

    小数部が 7 桁（.NET の "O" 書式）なので、Python の fromisoformat が
    受け付けるよう 6 桁へ丸める。
    """
    if not s:
        return None
    s = s.rstrip("Z")
    if "." in s:
        head, frac = s.split(".", 1)
        s = head + "." + frac[:6]
    try:
        return datetime.fromisoformat(s)
    except ValueError:
        return None


def load_keyframes(session_dir):
    """kf_localized.jsonl を読み、採用されたキーフレームを時刻順に返す。"""
    p = Path(session_dir) / "kf_localized.jsonl"
    if not p.exists():
        raise SystemExit(
            f"{p} がありません。\n"
            "  localize.watch が結果を保存するようになってから収集したセッションが必要です。\n"
            "  （古いセッションでは記録されていません）")
    rows = []
    for line in p.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            d = json.loads(line)
        except json.JSONDecodeError:
            continue
        if not d.get("accepted") or "center_colmap" not in d or "center_world" not in d:
            continue
        t = parse_ts(d.get("ts"))
        if t is None:
            continue
        rows.append({"t": t, "cc": np.array(d["center_colmap"], float),
                     "cw": np.array(d["center_world"], float),
                     "inliers": d.get("num_inliers"), "name": d.get("name")})
    rows.sort(key=lambda r: r["t"])
    return rows


def iter_jsonl(path):
    """JSONL を 1 行ずつ dict で返す。壊れた行は飛ばす。"""
    for line in Path(path).read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            yield json.loads(line)
        except json.JSONDecodeError:
            continue


# CP の実測行の型。実験前に 1 回の測量（cp_survey）が現行。
# 旧ログは試行ごとの計測（cp_measure）なので、読めるように残す。
SURVEY_TYPES = ("cp_survey", "cp_measure")


def survey_key(d):
    """その CP 実測行がどの測量に属するか。

    新形式 cp_survey は survey 番号を持つ。**試行番号では引けない**——測量は実験の前に
    1 回だけ行われ、trial=0（試行外）として記録されるため。
    旧形式 cp_measure は試行ごとに測っていたので、試行番号をそのまま測量キーとして扱う。
    """
    if d.get("type") == "cp_survey":
        return ("survey", int(d.get("survey") or 0))
    return ("trial", int(d.get("trial") or 0))


def trial_survey_key(d):
    """trial_start が指す測量キー。

    ExperimentController は試行開始時に「そのとき有効だった測量の通し番号」を
    trial_start.survey に載せる。原点が動いて測り直した場合は番号が変わるので、
    どの試行がどの測量を使ったかはこれでしか分からない。
    survey を持たない旧ログは、その試行自身の計測を指しているものとして扱う。
    """
    s = d.get("survey")
    if isinstance(s, int) and s > 0:
        return ("survey", s)
    return ("trial", int(d.get("trial") or 0))


def collect_surveys(events):
    """イベント列から測量を集める。

    返り値: (surveys, of_trial)
      surveys  … 測量キー -> {CP 名: {"local": arr, "tips": [arr, ...], "sources": set()}}
      of_trial … 試行番号 -> 測量キー（trial_start から）
    同じ CP を測り直していれば tips が複数入るので、使う側で平均する（手の震えを平滑化）。
    """
    surveys, of_trial = {}, {}
    for d in events:
        typ = d.get("type")
        if typ == "trial_start":
            of_trial[int(d.get("trial") or 0)] = trial_survey_key(d)
        elif typ in SURVEY_TYPES and "local" in d and "tip" in d:
            pts = surveys.setdefault(survey_key(d), {})
            e = pts.setdefault(d.get("name"), {"local": np.array(d["local"], float),
                                               "tips": [], "sources": set()})
            e["tips"].append(np.array(d["tip"], float))
            e["sources"].add(d.get("tip_source", "?"))
    return surveys, of_trial


def survey_cp_list(points):
    """collect_surveys の 1 測量ぶんを、評価で使う形（world = tips の平均）に直す。"""
    return [{"name": n, "local": e["local"], "world": np.mean(e["tips"], axis=0),
             "n": len(e["tips"])}
            for n, e in sorted(points.items())]


def load_control_points(jsonl_path, trial=None, survey=None):
    """実機ログから CP の (model 座標, 実測ワールド座標) を取り出す。

    CP の測量は **実験の前に 1 回**だけ行い、全試行で使い回す。よって既定では
    「そのログにある測量」をそのまま返す（複数あれば最新）。
    --trial を指定すると、その試行の trial_start が記録している測量を引く
    （原点が動いて測り直した場合、試行によって参照先が変わるため）。
    """
    surveys, of_trial = collect_surveys(iter_jsonl(jsonl_path))
    if not surveys:
        return []

    if survey is not None:
        key = ("survey", int(survey))
    elif trial is not None:
        key = of_trial.get(int(trial))
        if key is None:
            raise SystemExit(f"試行 {trial} の trial_start がログにありません。"
                             "--survey で測量を直接指定できます。")
    else:
        key = sorted(surveys)[-1]
        if len(surveys) > 1:
            print(f"[warn] 測量が {len(surveys)} 件あります（{sorted(surveys)}）。"
                  f"最新 {key} を使います。--survey / --trial で指定できます。",
                  file=sys.stderr)

    pts = surveys.get(key)
    if not pts:
        raise SystemExit(f"測量 {key} の CP 実測がログにありません（あるのは {sorted(surveys)}）")
    return survey_cp_list(pts)


# ---------------------------------------------------------------- 評価

def solve_prefix(rows, cfg, window=0, handedness=-1):
    """与えられたキーフレーム群で Sim(3) を解く。ゲートを通らなければ None。"""
    if len(rows) < int(cfg.get("min", 5)):
        return None, "枚数不足"
    use = rows[-window:] if window and len(rows) > window else rows
    Cc = np.array([r["cc"] for r in use])
    Cw = np.array([r["cw"] for r in use])
    try:
        res = G.solve_sim3_ransac(Cc, Cw, thresh_m=0.10, iters=300, handedness=handedness)
    except ValueError as e:
        return None, str(e)
    ok, why = gates.should_publish(res["sigma"], int(res["inliers"].sum()), len(Cc),
                                   res["rmse_m"], cfg, cond=res["cond"])
    return (res if ok else None), why


def cp_errors(res, cps):
    """CP ごとの誤差[m]。res は solve_sim3_ransac の返り値。"""
    errs = []
    for c in cps:
        pred = res["sigma"] * (res["R"] @ c["local"]) + res["t"]
        errs.append(float(np.linalg.norm(pred - c["world"])))
    return np.array(errs)


def tracking_warning(rows, tail_n=4):
    """収集中に Quest のワールド原点が動いていないか。動いていれば警告文、無ければ None。

    原点が飛ぶと、そこから先のキーフレームは別座標系になる。RANSAC は多数派（古い側）を
    採るので rmse もインライア率も悪化せず、**曲線は「よく合っている」ように見えたまま
    実世界からはメートル単位でずれる**。数値からは見抜けないので、末尾の残差だけを見る。
    """
    if len(rows) <= tail_n + 4:
        return None
    Cc = np.array([r["cc"] for r in rows])
    Cw = np.array([r["cw"] for r in rows])
    res = G.solve_sim3_ransac(Cc, Cw, thresh_m=0.10, handedness=-1)
    q = res["quat"]
    x, y, z, w = (float(v) for v in q)
    R = np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                  [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                  [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])
    e = np.linalg.norm((res["sigma"] * (R @ Cc.T)).T + res["t"] - Cw, axis=1)
    tail, body = float(np.median(e[-tail_n:])), float(np.median(e[:-tail_n]))
    if tail > 0.20 and tail > 5.0 * max(body, 1e-3):
        return (f"終盤 {tail_n} 枚の残差が {tail * 100:.0f} cm（他は {body * 100:.1f} cm）。"
                "収集中にトラッキング原点が動いています。この試行のデータは使えません。")
    return None


def path_length(rows):
    if len(rows) < 2:
        return 0.0
    p = np.array([r["cw"] for r in rows])
    return float(np.linalg.norm(np.diff(p, axis=0), axis=1).sum())


def sweep(rows, cps, cfg, step=2.0, window=0):
    """収集時間を刻んで、各時点での誤差を求める。"""
    t0 = rows[0]["t"]
    total = (rows[-1]["t"] - t0).total_seconds()
    out = []
    t = step
    while t <= total + step:
        sub = [r for r in rows if (r["t"] - t0).total_seconds() <= t]
        res, why = solve_prefix(sub, cfg, window=window)
        row = {"time_s": round(min(t, total), 1), "n_kf": len(sub),
               "path_m": round(path_length(sub), 2), "solved": res is not None,
               "why": why}
        if res is not None:
            e = cp_errors(res, cps)
            row.update({
                "err_mean_cm": round(float(e.mean()) * 100, 2),
                "err_median_cm": round(float(np.median(e)) * 100, 2),
                "err_max_cm": round(float(e.max()) * 100, 2),
                "sigma": round(float(res["sigma"]), 4),
                "rmse_cm": round(float(res["rmse_m"]) * 100, 2),
                "inliers": int(res["inliers"].sum()),
                "baseline_m": round(res["cond"]["baseline"], 2),
                "extent2_m": round(res["cond"]["extent2"], 2),
            })
        out.append(row)
        t += step
    return out


# ---------------------------------------------------------------- 出力

def plot(rows, path, cps_n):
    try:
        import matplotlib
        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
    except ImportError:
        print("  (matplotlib 未導入のため図は省略)")
        return
    ok = [r for r in rows if r["solved"]]
    if not ok:
        print("  (解けた時点が無いため図は省略)")
        return
    t = [r["time_s"] for r in ok]
    fig, ax = plt.subplots(figsize=(5.2, 3.4))
    ax.plot(t, [r["err_median_cm"] for r in ok], "-o", ms=3, label="median")
    ax.fill_between(t, [r["err_mean_cm"] for r in ok], [r["err_max_cm"] for r in ok],
                    alpha=0.18, label="mean–max")
    # 解けなかった時間帯を灰色で示す（「まだ足りない」区間が見える）
    for r in rows:
        if not r["solved"]:
            ax.axvspan(r["time_s"] - 1, r["time_s"] + 1, color="0.85", lw=0, zorder=0)
    ax.set_xlabel("data collection time [s]")
    ax.set_ylabel(f"control-point error [cm]  (n={cps_n})")
    ax.grid(alpha=0.3)
    ax.legend(fontsize=8)
    fig.tight_layout()
    fig.savefig(path, dpi=200)
    print(f"  書き出し: {path}")


def main():
    ap = argparse.ArgumentParser(description="収集時間 vs 位置合わせ精度")
    ap.add_argument("session", help="received/session_YYYYmmdd_HHMMSS")
    ap.add_argument("jsonl", help="実機ログ exp_*.jsonl / quest_events.jsonl（CP 測量を含むもの）")
    ap.add_argument("--trial", type=int, default=None,
                    help="この試行が使った測量を引く（trial_start.survey 経由）")
    ap.add_argument("--survey", type=int, default=None, help="測量を番号で直接指定")
    ap.add_argument("--step", type=float, default=2.0, help="時間刻み[s]")
    ap.add_argument("--window", type=int, default=0,
                    help="直近 N 枚だけ使う（0=全部。運用の window を再現したいとき）")
    ap.add_argument("--outdir", default="analysis_out")
    args = ap.parse_args()

    rows = load_keyframes(args.session)
    cps = load_control_points(args.jsonl, args.trial, args.survey)
    if not rows:
        raise SystemExit("採用されたキーフレームがありません")
    if not cps:
        raise SystemExit("CP の実測値がありません。実験の前に 1 回、各 CP の実物を"
                         "コントローラ先端で触って測量してください（cp_survey に local/tip が要ります）。")

    cfgp = Path(args.session) / "cv.json"
    cfg = json.loads(cfgp.read_text()) if cfgp.exists() else {}

    # 使えないデータで曲線を引かせない。原点が動いた試行は、数値上はむしろ綺麗に見える。
    warn = tracking_warning(rows)
    if warn:
        print("!! " + warn)
        print("!! 複数セッションの相互チェックは analysis/check_tracking.py で行えます。")
        print()

    total = (rows[-1]["t"] - rows[0]["t"]).total_seconds()
    print(f"キーフレーム {len(rows)} 枚 / {total:.1f} 秒 / 歩行 {path_length(rows):.1f} m")
    print(f"コントロールポイント {len(cps)} 点: "
          + ", ".join(f"{c['name']}(n={c['n']})" for c in cps))
    print()

    table = sweep(rows, cps, cfg, step=args.step, window=args.window)

    print(f"{'時間[s]':>7}{'KF':>5}{'歩行[m]':>8}{'中央値[cm]':>11}{'平均':>8}{'最大':>8}"
          f"{'scale':>8}{'状態':>16}")
    for r in table:
        if r["solved"]:
            print(f"{r['time_s']:7.0f}{r['n_kf']:5d}{r['path_m']:8.1f}"
                  f"{r['err_median_cm']:11.1f}{r['err_mean_cm']:8.1f}{r['err_max_cm']:8.1f}"
                  f"{r['sigma']:8.3f}{'':>16}")
        else:
            print(f"{r['time_s']:7.0f}{r['n_kf']:5d}{r['path_m']:8.1f}"
                  f"{'-':>11}{'-':>8}{'-':>8}{'-':>8}   {r['why'][:14]}")

    out = Path(args.outdir); out.mkdir(parents=True, exist_ok=True)
    fields = ["time_s", "n_kf", "path_m", "solved", "err_mean_cm", "err_median_cm",
              "err_max_cm", "sigma", "rmse_cm", "inliers", "baseline_m", "extent2_m", "why"]
    with open(out / "accuracy_vs_time.csv", "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=fields, extrasaction="ignore")
        w.writeheader(); w.writerows(table)
    print(f"\n  書き出し: {out / 'accuracy_vs_time.csv'}")
    plot(table, out / "accuracy_vs_time.png", len(cps))

    ok = [r for r in table if r["solved"]]
    if ok:
        first = ok[0]
        print(f"\n最初に解けた時点: {first['time_s']:.0f}s（KF {first['n_kf']} 枚 / "
              f"歩行 {first['path_m']:.1f} m）中央値 {first['err_median_cm']:.1f} cm")
        for thr in (10.0, 5.0):
            hit = next((r for r in ok if r["err_median_cm"] <= thr), None)
            print(f"中央値 {thr:.0f}cm 以下に到達: "
                  + (f"{hit['time_s']:.0f}s（歩行 {hit['path_m']:.1f} m）" if hit else "未到達"))
        last = ok[-1]
        print(f"最終（{last['time_s']:.0f}s）: 中央値 {last['err_median_cm']:.1f}cm / "
              f"平均 {last['err_mean_cm']:.1f}cm / 最大 {last['err_max_cm']:.1f}cm")


if __name__ == "__main__":
    main()
