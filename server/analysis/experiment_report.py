#!/usr/bin/env python3
"""複数試行をまとめて 1 本の結果にする（論文に載せる形）。

`accuracy_vs_time.py` は 1 試行分の曲線を出す。10 試行あるなら 10 回実行して手で束ねる
ことになるが、それでは中央値も四分位も出せない。本スクリプトは

  1. `received/session_*/trial.json` の label と、実機ログ `exp_*.jsonl` の trial_start.label
     を突き合わせて **セッションと試行を自動で対応づける**
  2. 使えない試行を落とす（中断・トラッキング原点の移動・CP 未測量）
  3. 各試行で「収集時間 t 秒までのデータだけ」で Sim(3) を解き直し、CP 誤差を出す
  4. 試行をまたいで集約し、**中央値の曲線＋四分位帯**と、試行ごとの表を書き出す

を一度に行う。

もう 1 つの結果として **試行間の再現性**（独立な 10 試行が同じ姿勢に収束したか）も出す。
これは「安定して同じ答えに落ち着く」ことの直接の証拠で、CP 誤差とは独立した主張になる。
ただし試行の合間に Quest の原点が動くと座標系が変わるので、**同じ座標系の試行どうしでしか
比べられない**。原点が動いた境目を検出して、グループごとに再現性を出す。

使い方:
    python analysis/experiment_report.py --received received --logs logs
    python analysis/experiment_report.py --received received --logs logs --step 2 --outdir out
"""
import argparse
import csv
import json
import sys
from pathlib import Path

import numpy as np

_HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(_HERE))
sys.path.insert(0, str(_HERE.parent))

import accuracy_vs_time as avt          # noqa: E402
import check_tracking as ct             # noqa: E402
from vptserver.cv import geometry as G  # noqa: E402

# 解析に載せる試行の条件（外れたら理由つきで除外する）
GOOD_VERDICTS = ("completed", "success")


# ---------------------------------------------------------------- 読み込み

def feed(trials, surveys, seen, path):
    """1 ファイルを読み、(run, trial) ごとに試行へ、CP の実測は測量へ畳み込む。

    2 つのソースを同じ形で扱える:
      - `quest_events.jsonl` … PC が UDP で受け取ったもの（**通常はこれだけで足りる**）
      - `exp_*.jsonl`        … adb pull した端末内ログ（全イベントが入っている）
    両方あってもよい。行は (run, seq) で重複除去するので、UDP の再送で同じ行が
    何度も入っていても、端末内ログと重なっていても、二重に数えない。

    **測量（CP の実測）は試行に属さない。** 実験の前に 1 回だけ行い全試行で使い回すので、
    trial=0（試行外）として届く。試行のふるい分けに掛けると丸ごと落ちるため、先に拾う。
    """
    src = Path(path)
    for line in src.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            d = json.loads(line)
        except json.JSONDecodeError:
            continue
        run = d.get("run") or src.stem
        seq = d.get("seq")
        if seq is not None:
            key = (run, int(seq))
            if key in seen:
                continue
            seen.add(key)
        typ = d.get("type")

        # 測量は run 単位（試行の外）。旧ログの cp_measure も同じ器に入る（→ avt.survey_key）。
        if typ in avt.SURVEY_TYPES and "local" in d and "tip" in d:
            pts = surveys.setdefault((run, avt.survey_key(d)), {})
            e = pts.setdefault(d.get("name"), {"local": np.array(d["local"], float),
                                               "tips": [], "sources": set()})
            e["tips"].append(np.array(d["tip"], float))
            e["sources"].add(d.get("tip_source", "?"))
            continue

        tid = d.get("trial", 0)
        if not isinstance(tid, int) or tid <= 0:
            continue
        t = trials.setdefault((run, tid), {
            "run": run, "trial": tid, "label": None, "survey": None,
            "verdict": None, "tracking_events": 0,
        })
        if typ == "trial_start":
            t["label"] = d.get("label")
            # どの測量を使った試行か。原点が動いて測り直すと番号が変わるので、
            # 試行と測量の対応はこれでしか分からない。
            t["survey"] = avt.trial_survey_key(d)
        elif typ == "trial_end":
            # 原点の移動を検出すると completed を void_tracking で上書きするので、後の行が勝つ。
            # ただし UDP は順序を保証しないため、**「使えない」判定は取り消さない**。
            v = d.get("verdict")
            if t["verdict"] not in ("void_tracking", "aborted"):
                t["verdict"] = v
            elif v in ("void_tracking", "aborted"):
                t["verdict"] = v
            if isinstance(d.get("tracking_events"), int):
                t["tracking_events"] = max(t["tracking_events"], d["tracking_events"])
        elif typ == "tracking_origin_changed":
            t["tracking_events"] += 1


def load_all_logs(logs_dirs, run=None):
    """指定された場所すべてからイベントを読み込む（PC 側／端末側の両対応）。

    `run` を渡すと、その run（= Quest 側のログファイル名 ＝ アプリ起動 ID）の試行だけを残す。
    `received/quest_events.jsonl` は**正本なので過去の全実験が入っている**。一方
    `index_sessions` は label ごとに最新の session を採るので、絞らないと過去 run の
    trial-01 まで最新の session に紐づいてしまう。その試行は当時の測量（別の `local`）を
    引くため、同じセッションを二重に数えたうえで数 m の誤差として現れる。
    ある 1 回の実験だけを解析するときは必ず `--run` で絞る。
    """
    files = []
    for spec in logs_dirs:
        p = Path(spec)
        if p.is_dir():
            files += sorted(p.rglob("quest_events.jsonl")) + sorted(p.rglob("exp_*.jsonl"))
        elif p.exists():
            files.append(p)
    if not files:
        raise SystemExit(
            "イベントログが見つかりません。次のどちらかが要ります:\n"
            "  - received/quest_events.jsonl（run.py が UDP で受け取ったもの。通常はこれ）\n"
            "  - logs/exp_*.jsonl（adb pull した端末内ログ）")

    trials, surveys, seen = {}, {}, set()
    for f in files:
        feed(trials, surveys, seen, f)
    if run:
        known = sorted({t["run"] for t in trials.values()})
        if run not in known:
            raise SystemExit(f"--run {run} に一致する試行がありません。あるのは:\n  "
                             + "\n  ".join(known))
        trials = {k: t for k, t in trials.items() if t["run"] == run}
    for t in trials.values():
        # trial_start に survey が無い旧ログは、その試行自身の計測を指しているものとして引く。
        key = t["survey"] or ("trial", t["trial"])
        pts = surveys.get((t["run"], key), {})
        t["survey_key"] = key
        t["cp_list"] = avt.survey_cp_list(pts)
        t["tip_sources"] = set().union(*(e["sources"] for e in pts.values())) if pts else set()
    return [t for t in trials.values()], [str(f) for f in files]


def index_sessions(received_dir):
    """label -> session ディレクトリ。同じ label が複数あれば新しい方を採る。"""
    idx = {}
    for d in sorted(Path(received_dir).glob("session_*")):
        tj = d / "trial.json"
        if tj.exists():
            try:
                lbl = json.loads(tj.read_text()).get("label")
            except Exception:
                continue
            if lbl:
                idx[lbl] = d          # sorted なので後勝ち＝新しい方
    return idx


# ---------------------------------------------------------------- 試行の選別

def prepare(trials, sessions, cfg_default, min_kf=8):
    """試行に session を紐づけ、使える／使えないを判定する。"""
    rows = []
    for t in sorted(trials, key=lambda x: (x["run"], x["trial"])):
        r = dict(t)
        r["session"] = sessions.get(t["label"] or "")
        r["drop"] = None

        if r["session"] is None:
            r["drop"] = "対応する session が無い"
        elif t["verdict"] not in GOOD_VERDICTS:
            r["drop"] = f"verdict={t['verdict']}"
        elif not t["cp_list"]:
            r["drop"] = f"測量 {t['survey_key']} の CP 実測が無い"

        if r["drop"] is None:
            try:
                r["kf"] = avt.load_keyframes(r["session"])
            except SystemExit as e:
                r["kf"] = []
                r["drop"] = str(e).splitlines()[0]
        else:
            r["kf"] = []

        if r["drop"] is None:
            if len(r["kf"]) < min_kf:
                r["drop"] = f"採用キーフレームが {len(r['kf'])} 枚"
            else:
                warn = avt.tracking_warning(r["kf"])
                if warn:
                    r["drop"] = "収集中に原点が動いた"
                    r["detail"] = warn

        if r["drop"] is None:
            cfgp = Path(r["session"]) / "cv.json"
            r["cfg"] = json.loads(cfgp.read_text()) if cfgp.exists() else cfg_default
        rows.append(r)
    return rows


# ---------------------------------------------------------------- 集約

def span_s(r):
    kf = r["kf"]
    return (kf[-1]["t"] - kf[0]["t"]).total_seconds()


def time_grid(rows, step, max_time=None):
    """共通の時間軸。**いちばん長い試行に合わせる。**

    短い試行に合わせて切ると、60 秒歩いたデータの大半を捨てることになる（実際、
    21 秒で終わった 1 試行のせいで曲線が 20 秒までになった）。代わりに各時点で
    「そこまでのデータを持っている試行」だけを集約し、その数を併記する。
    """
    top = max((span_s(r) for r in rows), default=0.0)
    if max_time:
        top = min(top, max_time)
    g, t = [], step
    while t <= top + 1e-6:
        g.append(round(t, 1))
        t += step
    return g


def sweep_on_grid(r, grid):
    """1 試行を共通時間軸で評価する。各点で「その時刻までのデータ」を使う。

    その試行の収集時間を超えた時点は `has_data=False`（データが無い）とし、
    `solved=False`（データはあるが解けない）と区別する。混ぜると
    「まだ精度が足りない」と「もう歩き終えた」が同じに見えてしまう。
    """
    kf, cps, cfg = r["kf"], r["cp_list"], r["cfg"]
    t0 = kf[0]["t"]
    total = span_s(r)
    out = []
    for t in grid:
        if t > total + 1e-6:
            out.append({"time_s": t, "has_data": False, "solved": False,
                        "n_kf": len(kf), "path_m": round(avt.path_length(kf), 2),
                        "why": "収集時間を超過"})
            continue
        sub = [k for k in kf if (k["t"] - t0).total_seconds() <= t]
        res, why = avt.solve_prefix(sub, cfg)
        row = {"time_s": t, "has_data": True, "n_kf": len(sub),
               "path_m": round(avt.path_length(sub), 2),
               "solved": res is not None, "why": why}
        if res is not None:
            e = avt.cp_errors(res, cps)
            row.update({"err_median_cm": float(np.median(e)) * 100,
                        "err_mean_cm": float(e.mean()) * 100,
                        "err_max_cm": float(e.max()) * 100,
                        "sigma": float(res["sigma"]),
                        "rmse_cm": float(res["rmse_m"]) * 100})
        out.append(row)
    return out


def check_cp_sanity(r):
    """CP の測量そのものが成立しているかを見る（アラインの良し悪しとは別の話）。

    アラインが正しくても、**測り方が壊れていれば誤差は測れない**。実機で実際に起きた:
      - コントローラを動かさないまま右トリガーを 6 回引き、6 点すべてを同じ場所で記録した
        （2 秒間に 6 点。実測値の広がりが 6 cm しかない）
      - CP_04〜06 がプレースホルダ座標のまま実物に対応していなかった
    どちらも「誤差が数メートル」という同じ見え方をするので、切り分けを機械的に行う。

    測量は実験前の 1 回きりなので、ここで弾けなければ**全試行が道連れになる**。
    Quest 側でも同じ観点で入力段階の防止をしている（ControlPointMeasure の移動量チェック）。

    返り値: 警告文のリスト（空なら問題なし）。
    """
    out = []
    cps = r["cp_list"]
    if len(cps) < 2:
        return out

    tips = np.array([c["world"] for c in cps])
    local = np.array([c["local"] for c in cps])

    # 1. 実測点が動いていない。model 上で離れている点どうしが、実測では同じ場所にある。
    #    スケール（|sigma|）が分からなくても、比で見れば判定できる。
    tip_span = float(np.linalg.norm(tips.max(0) - tips.min(0)))
    model_span = float(np.linalg.norm(local.max(0) - local.min(0)))
    if model_span > 0.3 and tip_span < 0.2 * model_span:
        out.append(f"実測点の広がりが {tip_span * 100:.0f} cm しかない"
                   f"（model 上では {model_span:.1f} 単位ぶん離れている）。"
                   "コントローラを動かさずに全点を記録した可能性が高い。")

    # 2. 同一点の連続記録。隣り合う点が数 cm 以内なら、別の特徴を触ったとは考えにくい。
    close = sum(1 for i in range(1, len(tips))
                if float(np.linalg.norm(tips[i] - tips[i - 1])) < 0.05)
    if close:
        out.append(f"隣り合う {close} 組の実測点が 5 cm 以内。別々の点を触っていない可能性。")
    return out


def placeholder_suspects(cps):
    """未配置のまま残っていそうな CP を挙げる。

    Editor の初期値は原点近くの丸い数値（(-0.30,0.40,-0.30) など）になりがちで、
    実際の特徴に置いた点（原点から数単位離れる）とは桁が違う。断定はできないので
    「疑い」として出すに留める。
    """
    if len(cps) < 3:
        return []
    d = np.array([float(np.linalg.norm(c["local"])) for c in cps])
    med = float(np.median(d))
    if med <= 0.5:
        return []
    return [c["name"] for c, x in zip(cps, d) if x < 0.25 * med]


def solve_full(r):
    """その試行の全データで解いたときの結果（表の「最終値」用）。"""
    res, why = avt.solve_prefix(r["kf"], r["cfg"])
    if res is None:
        return None, why
    e = avt.cp_errors(res, r["cp_list"])
    return {"n_kf": len(r["kf"]), "path_m": avt.path_length(r["kf"]),
            "span_s": span_s(r),
            "err_median_cm": float(np.median(e)) * 100,
            "err_mean_cm": float(e.mean()) * 100,
            "err_max_cm": float(e.max()) * 100,
            "sigma": float(res["sigma"]),
            "rmse_cm": float(res["rmse_m"]) * 100}, why


def aggregate(curves, grid):
    """時刻ごとに試行をまたいで集約する。"""
    agg = []
    for i, t in enumerate(grid):
        have = [c[i] for c in curves if c[i]["has_data"]]
        vals = [x["err_median_cm"] for x in have if x["solved"]]
        agg.append({
            "time_s": t,
            "n_trials_with_data": len(have),
            "n_trials_solved": len(vals),
            "n_trials": len(curves),
            "median_cm": round(float(np.median(vals)), 2) if vals else None,
            "q25_cm": round(float(np.percentile(vals, 25)), 2) if vals else None,
            "q75_cm": round(float(np.percentile(vals, 75)), 2) if vals else None,
            "min_cm": round(float(np.min(vals)), 2) if vals else None,
            "max_cm": round(float(np.max(vals)), 2) if vals else None,
        })
    return agg


# ---------------------------------------------------------------- 再現性

def repeatability(rows):
    """試行間の再現性。**同じワールド座標系のグループ内でのみ**比較する。

    試行の合間に Quest の原点が動くと座標系が変わるので、跨いで比べると
    「原点が回った角度」を測ることになってしまい、手法の再現性にならない。
    """
    sols, labels = [], []
    for r in rows:
        Cc = np.array([k["cc"] for k in r["kf"]])
        Cw = np.array([k["cw"] for k in r["kf"]])
        sols.append(G.solve_sim3_ransac(Cc, Cw, thresh_m=0.10, handedness=-1))
        labels.append(r["label"])

    # 連続する試行を、原点が動いていない限り同じグループにまとめる
    groups, cur = [], [0]
    for i in range(1, len(sols)):
        f = ct.check_between([None, None], [sols[i - 1], sols[i]])[0]
        if f["pure_yaw"]:
            groups.append(cur); cur = [i]
        else:
            cur.append(i)
    groups.append(cur)

    out = []
    for g in groups:
        if len(g) < 2:
            out.append({"labels": [labels[i] for i in g], "pairs": 0})
            continue
        degs, dists = [], []
        for a in range(len(g)):
            for b in range(a + 1, len(g)):
                f = ct.check_between([None, None], [sols[g[a]], sols[g[b]]])[0]
                degs.append(f["deg"])
                dists.append(float(np.linalg.norm(f["t"])))
        out.append({"labels": [labels[i] for i in g], "pairs": len(degs),
                    "deg_median": float(np.median(degs)), "deg_max": float(np.max(degs)),
                    "m_median": float(np.median(dists)), "m_max": float(np.max(dists))})
    return out


# ---------------------------------------------------------------- 出力

def plot(agg, path, n_trials, cps_n):
    try:
        import matplotlib
        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
    except ImportError:
        print("  (matplotlib 未導入のため図は省略)")
        return
    ok = [a for a in agg if a["median_cm"] is not None]
    if not ok:
        print("  (解けた時点が無いため図は省略)")
        return
    t = [a["time_s"] for a in ok]
    fig, ax = plt.subplots(figsize=(5.2, 3.4))
    ax.fill_between(t, [a["q25_cm"] for a in ok], [a["q75_cm"] for a in ok],
                    alpha=0.20, lw=0, label="IQR across trials")
    ax.plot(t, [a["median_cm"] for a in ok], "-o", ms=3, label="median across trials")
    ax.set_xlabel("data collection time [s]")
    ax.set_ylabel(f"control-point error [cm]  ({cps_n} points)")
    ax.grid(alpha=0.3)
    ax.legend(fontsize=8)

    # 「まだ解けていない試行がある」区間が読み取れるように、解けた試行数を薄く重ねる
    ax2 = ax.twinx()
    ax2.plot([a["time_s"] for a in agg],
             [a["n_trials_solved"] for a in agg], color="0.6", lw=1, ls=":")
    ax2.set_ylabel(f"trials solved (of {n_trials})", color="0.5", fontsize=9)
    ax2.set_ylim(0, n_trials + 0.5)
    ax2.tick_params(labelsize=8, colors="0.5")

    fig.tight_layout()
    fig.savefig(path, dpi=200)
    print(f"  書き出し: {path}")


def main():
    ap = argparse.ArgumentParser(description="複数試行をまとめて結果を出す")
    ap.add_argument("--received", default="received", help="セッションの置き場")
    ap.add_argument("--logs", nargs="*", default=None,
                    help="イベントログの置き場（複数可）。既定は --received 直下の "
                         "quest_events.jsonl（run.py が UDP で受け取ったもの）。"
                         "adb pull した端末内ログを足したいときは logs フォルダも渡す")
    ap.add_argument("--step", type=float, default=2.0, help="時間刻み[s]")
    ap.add_argument("--max-time", type=float, default=None,
                    help="曲線の右端[s]（既定: いちばん長い試行に合わせる）")
    ap.add_argument("--min-kf", type=int, default=8,
                    help="この枚数未満しか採用されなかった試行は除外する")
    ap.add_argument("--run", default=None,
                    help="この run（Quest のログファイル名 = アプリ起動 ID、例 exp_20260802_141845）"
                         "の試行だけを解析する。指定しないと quest_events.jsonl に残っている"
                         "過去の実験まで混ざる（→ load_all_logs の説明）")
    ap.add_argument("--outdir", default="analysis_out")
    args = ap.parse_args()

    # 既定は PC 側が UDP で受け取ったログ。adb pull しなくても解析が完結する。
    log_dirs = args.logs if args.logs else [str(Path(args.received) / "quest_events.jsonl")]
    trials, used_files = load_all_logs(log_dirs, run=args.run)
    sessions = index_sessions(args.received)
    rows = prepare(trials, sessions, {}, min_kf=args.min_kf)

    print("== 読んだイベントログ ==")
    for f in used_files:
        print("  " + f)
    print()
    print("== 試行の一覧 ==")
    # 測量は実験前に 1 回なので、通常は全試行が同じ番号になる。**違っていたら**
    # 途中で測り直した＝原点が動いた印なので、列に出して見えるようにする。
    print(f"{'label':10s}{'session':22s}{'KF':>5}{'CP':>5}{'測量':>6}  判定")
    use = []
    for r in rows:
        sess = Path(r["session"]).name if r["session"] else "-"
        mark = "使う" if r["drop"] is None else f"除外: {r['drop']}"
        sv = r["survey_key"][1] if r.get("survey_key") else "-"
        print(f"{(r['label'] or '?'):10s}{sess:22s}{len(r['kf']):5d}"
              f"{len(r['cp_list']):5d}{sv:>6}  {mark}")
        if r.get("detail"):
            print(f"{'':37s}  └ {r['detail']}")
        if r["drop"] is None:
            use.append(r)

    stale = {s for r in use for s in r["tip_sources"] if s == "Restored"}
    if stale:
        print("\n!! 前回セッションの先端校正値を流用した計測があります。"
              "物理構成が変わっていれば系統誤差が乗ります。")

    if not use:
        raise SystemExit("\n使える試行がありません。")

    print(f"\n使う試行: {len(use)} / {len(rows)}")

    # --- CP 実測そのものの健全性。ここが壊れていると曲線は意味を持たない ---
    # 測量は全試行で共通（実験前に 1 回）なので、測量ごとに 1 度だけ検査する。
    # 試行ごとに回すと、同じ 1 件の警告が試行数ぶん並んで読めなくなる。
    problems, seen_surveys = [], set()
    for r in use:
        key = r["survey_key"]
        if key in seen_surveys:
            continue
        seen_surveys.add(key)
        tag = f"測量 #{key[1]}" if key[0] == "survey" else f"{r['label']}(旧形式)"
        for w in check_cp_sanity(r):
            problems.append(f"  {tag}: {w}")
    sus = placeholder_suspects(use[0]["cp_list"]) if use else []
    if sus:
        problems.append("  未配置のまま残っている疑いのある CP: " + ", ".join(sus)
                        + "（model 原点のすぐ近くにあり、他の点と桁が違う）")
    if problems:
        print("\n!! CP の実測に問題があります。**曲線を読む前にここを直してください。**")
        for p in problems:
            print("!!" + p)

    grid = time_grid(use, args.step, args.max_time)
    if not grid:
        raise SystemExit("共通の時間軸が作れません（試行が短すぎる）")
    print(f"共通の時間軸: {grid[0]:.0f}–{grid[-1]:.0f} s（{args.step:.0f}s 刻み）\n")

    curves = [sweep_on_grid(r, grid) for r in use]
    agg = aggregate(curves, grid)

    print("== 主結果: 収集時間 vs CP 誤差（試行をまたいだ集約）==")
    print("   「データ有」= その時刻まで歩いていた試行数 / 「解けた」= そのうち解が出た試行数")
    print(f"{'時間[s]':>7}{'データ有':>9}{'解けた':>8}{'中央値[cm]':>11}"
          f"{'四分位[cm]':>16}{'最小–最大[cm]':>16}")
    for a in agg:
        head = (f"{a['time_s']:7.0f}{a['n_trials_with_data']:>6d}/{a['n_trials']:<3d}"
                f"{a['n_trials_solved']:>8d}")
        if a["median_cm"] is None:
            print(head + f"{'-':>11}{'-':>16}{'-':>16}")
        else:
            print(head + f"{a['median_cm']:11.1f}"
                  f"{a['q25_cm']:8.1f}–{a['q75_cm']:<7.1f}"
                  f"{a['min_cm']:8.1f}–{a['max_cm']:<7.1f}")

    print("\n== 試行ごとの最終値（その試行の全データを使ったとき）==")
    print(f"{'label':10s}{'KF':>4}{'収集[s]':>9}{'歩行[m]':>9}{'中央値[cm]':>11}"
          f"{'平均':>8}{'最大':>8}{'scale':>9}")
    finals = []
    for r in use:
        f, why = solve_full(r)
        if f is None:
            print(f"{r['label']:10s}{len(r['kf']):4d}{span_s(r):9.0f}"
                  f"{avt.path_length(r['kf']):9.1f}   解けず: {why[:30]}")
            continue
        finals.append(f["err_median_cm"])
        print(f"{r['label']:10s}{f['n_kf']:4d}{f['span_s']:9.0f}{f['path_m']:9.1f}"
              f"{f['err_median_cm']:11.1f}{f['err_mean_cm']:8.1f}"
              f"{f['err_max_cm']:8.1f}{f['sigma']:9.3f}")
    if finals:
        print(f"{'まとめ':10s}{'':4s}{'':9s}{'':9s}{np.median(finals):11.1f}"
              f"  （試行間の中央値 / 範囲 {min(finals):.1f}–{max(finals):.1f} cm・n={len(finals)}）")

    # --- CP ごとの内訳。1 点だけ突出していれば、その点の注釈がずれている ---
    print("\n== CP ごとの誤差[cm]（各試行の全データで解いたとき）==")
    names = [c["name"] for c in use[0]["cp_list"]]
    header = f"{'label':10s}" + "".join(f"{n:>10s}" for n in names)
    print(header)
    percp = {n: [] for n in names}
    cp_rows = []            # 論文の図と表がこの表と同じ数字を使えるように CSV にも残す
    for r in use:
        res, _ = avt.solve_prefix(r["kf"], r["cfg"])
        if res is None:
            print(f"{r['label']:10s}  解けず")
            continue
        e = avt.cp_errors(res, r["cp_list"]) * 100
        for n, v in zip([c["name"] for c in r["cp_list"]], e):
            if n in percp:
                percp[n].append(float(v))
            cp_rows.append({"label": r["label"], "cp": n, "err_cm": round(float(v), 3)})
        print(f"{r['label']:10s}" + "".join(f"{v:10.1f}" for v in e))
    med = {n: (float(np.median(v)) if v else float("nan")) for n, v in percp.items()}
    print(f"{'中央値':10s}" + "".join(f"{med[n]:10.1f}" for n in names))
    if med and not all(np.isnan(list(med.values()))):
        worst = max(med, key=lambda n: med[n])
        best = min(med, key=lambda n: med[n])
        if med[worst] > 3 * max(med[best], 1e-3):
            print(f"  → {worst} だけが突出しています（{med[worst]:.0f} cm 対 {med[best]:.0f} cm）。"
                  "その点の注釈が実物とずれている可能性が高い。")

    print("\n== 試行間の再現性（独立な試行が同じ姿勢に落ち着いたか）==")
    for grp in repeatability(use):
        names = ", ".join(grp["labels"])
        if grp["pairs"] == 0:
            print(f"  [{names}] 単独（比較相手なし）")
        else:
            print(f"  [{names}]  {grp['pairs']} 組: "
                  f"回転 中央 {grp['deg_median']:.2f}° 最大 {grp['deg_max']:.2f}° / "
                  f"位置 中央 {grp['m_median'] * 100:.1f} cm 最大 {grp['m_max'] * 100:.1f} cm")
    print("  ※ グループの切れ目は Quest の原点が動いた箇所。跨いだ比較は"
          "「原点が回った角度」になるので意味を持たない。")

    out = Path(args.outdir); out.mkdir(parents=True, exist_ok=True)
    with open(out / "curve_aggregate.csv", "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=list(agg[0].keys()))
        w.writeheader(); w.writerows(agg)
    with open(out / "cp_errors.csv", "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=["label", "cp", "err_cm"])
        w.writeheader(); w.writerows(cp_rows)
    with open(out / "curve_per_trial.csv", "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["label", "time_s", "has_data", "n_kf", "path_m", "solved",
                    "err_median_cm", "err_mean_cm", "err_max_cm", "sigma", "rmse_cm", "why"])
        for r, c in zip(use, curves):
            for x in c:
                if not x["has_data"]:
                    continue
                w.writerow([r["label"], x["time_s"], x["has_data"], x["n_kf"], x["path_m"], x["solved"],
                            round(x.get("err_median_cm", float("nan")), 2) if x["solved"] else "",
                            round(x.get("err_mean_cm", float("nan")), 2) if x["solved"] else "",
                            round(x.get("err_max_cm", float("nan")), 2) if x["solved"] else "",
                            round(x.get("sigma", float("nan")), 4) if x["solved"] else "",
                            round(x.get("rmse_cm", float("nan")), 2) if x["solved"] else "",
                            x["why"]])
    print(f"\n  書き出し: {out / 'curve_aggregate.csv'}")
    print(f"  書き出し: {out / 'curve_per_trial.csv'}")
    plot(agg, out / "curve_aggregate.png", len(use), len(use[0]["cp_list"]))


if __name__ == "__main__":
    main()
