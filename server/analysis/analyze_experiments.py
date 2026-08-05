#!/usr/bin/env python3
"""実機ログ（JSONL）から論文 Table 1 の数値を出す。

入力: ExperimentLogger が書く exp_<時刻>.jsonl。実機からの取り出しは
    adb pull /sdcard/Android/data/<パッケージ名>/files/experiments/ ./logs/

出力:
    - 標準出力に要約（CP 測量の健全性、model 側 CP の VR 編集、先端校正、E2 のコールドスタート収束）
    - <outdir>/cp_survey.csv         : 測量した CP（1 行 1 点）
    - <outdir>/model_cp_edits.csv    : VR 内で置き直した model 側 CP（あれば）
    - <outdir>/trials.csv            : E2 の試行ごとの成否・収束時間・KF 数
    - <outdir>/table1.tex            : 論文の表にそのまま貼れる LaTeX
    - <outdir>/survey_check.png      : 測量時点の見かけ誤差の箱ひげ図（診断用・matplotlib があれば）

使い方:
    python analysis/analyze_experiments.py logs/
    python analysis/analyze_experiments.py logs/ --outdir out --unit cm

設計上の注意:
- **E1（オーバーレイ精度）の数値はこのスクリプトでは出さない。** CP の測量は実験前に
  1 回だけ行うので、測量時に記録される err_m は「そのとき適用されていた姿勢」に対する
  見かけの誤差でしかなく、どの試行の精度でもない。論文の E1 は experiment_report.py が
  保存済みキーフレームから試行ごとに姿勢を解き直して算出する。ここでは測量そのものが
  成立しているか（点の取り違え・先端校正の流用）を確認するだけ。
- 1 ファイル = 1 アプリ起動。ファイル内は trial 番号で試行が区切られる
  （ExperimentController が RESET と合わせて振る）。trial=0 は試行外の記録なので除外する。
  測量は試行の外で行うため trial=0 で記録される（除外対象は E2 の集計だけ）。
- E2 の収束時間は「最初のキーフレーム送信 → 最初の auto_aligned」。
  trial_end に time_to_lock_s として入っているが、古いログや中断ログのために
  イベント列からも復元できるようにしてある（両方あれば trial_end を優先）。
- 成功判定は trial_end.verdict=="success"。timeout は失敗、aborted は集計から除外する
  （実験者都合の中断を失敗として数えると成功率が実態より低く出るため）。
"""
import argparse
import csv
import json
import statistics
import sys
from pathlib import Path


# ---------------------------------------------------------------- 読み込み

def load_records(paths):
    """JSONL 群を読み、(ファイル名, レコード) の列にする。壊れた行は飛ばす。"""
    out = []
    for p in paths:
        n_bad = 0
        with open(p, encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    out.append((p.name, json.loads(line)))
                except json.JSONDecodeError:
                    n_bad += 1
        if n_bad:
            print(f"[warn] {p.name}: 壊れた行を {n_bad} 行スキップ", file=sys.stderr)
    return out


def find_logs(target):
    """ファイルでもディレクトリでも受ける。"""
    t = Path(target)
    if t.is_file():
        return [t]
    if t.is_dir():
        return sorted(t.rglob("exp_*.jsonl")) or sorted(t.rglob("*.jsonl"))
    raise SystemExit(f"入力が見つかりません: {target}")


# ---------------------------------------------------------------- E1

def collect_overlay(records):
    """CP の測量記録を集める。1 行 1 点。

    **ここで集まる err_m は評価結果ではない。** 測量は実験の前に 1 回だけ行うので、
    err_m は「測量した時点でたまたま適用されていた姿勢」に対する見かけの誤差でしかなく、
    どの試行の精度でもない（未アラインなら -1）。実物を取り違えたときに気づくための
    診断値として集めるだけで、論文の数字は experiment_report.py が
    保存済みキーフレームから姿勢を解き直して算出する。

    旧形式の cp_measure（試行ごとに計測していた頃のログ）も読む。
    """
    rows = []
    for fname, r in records:
        typ = r.get("type")
        if typ not in ("cp_survey", "cp_measure"):
            continue
        rows.append({
            "file": fname,
            "survey": r.get("survey") if typ == "cp_survey" else None,
            "trial": r.get("trial", 0),
            "name": r.get("name"),
            "err_m": r.get("err_m"),
            "tip_residual_mm": r.get("tip_residual_mm"),
            # "Calibrated"=そのセッションで校正 / "Restored"=前回値の流用 / "Manual"=手動指定
            "tip_source": r.get("tip_source"),
            "t": r.get("t"),
        })
    return rows


def collect_model_edits(records):
    """model 側 CP を VR の中で置き直した記録（ControlPointEdit）。

    ここで見張るのは 2 つだけ。どちらも**測定を静かに無効にする**類の事故なので、
    数値を出す前に気づけるようにする。

    1. **位置合わせ済みで編集した。** オーバーレイが実物に重なって見えている状態で
       モデル側の点を動かすと、無意識に実物へ寄せてしまい誤差が 0 側にバイアスする。
       実機側でブロックしてあるが、ゲートを緩めたビルドで撮ったログを取り違えないよう
       ここでも確認する。
    2. **測量のあとに編集して測り直していない。** 解析は local を cp_survey の行から
       取る（記録した瞬間のスナップショット）ので、後から動かすとシーンとログが黙って
       食い違う。実機は編集時に測量を無効化するが、その回のログで確かめる。
    """
    out = []
    for fname, r in records:
        t = r.get("type")
        if t == "cp_model_edit" and r.get("action") == "start":
            out.append({"file": fname, "kind": "start", "t": r.get("t"),
                        "aligned": bool(r.get("aligned")), "name": None, "moved_m": None})
        elif t == "cp_model_edit" and r.get("action") == "place":
            out.append({"file": fname, "kind": "place", "t": r.get("t"), "aligned": None,
                        "name": r.get("name"), "moved_m": r.get("moved_m"),
                        "tip_source": r.get("tip_source")})
        elif t == "cp_model_edit_revert":
            out.append({"file": fname, "kind": "revert", "t": r.get("t"), "aligned": None,
                        "name": r.get("name"), "moved_m": None})
    return out


def edits_after_survey(records):
    """「測量 → 編集 → 測り直していない」ファイルを挙げる（ファイル内の順序で判定）。"""
    bad = []
    for fname in dict.fromkeys(f for f, _ in records):
        seq = [r for f, r in records if f == fname]
        last_survey = max((i for i, r in enumerate(seq)
                           if r.get("type") == "cp_survey_done"), default=-1)
        if last_survey < 0:
            continue
        # 開始しただけ（1 点も置かずに終えた）なら無害。実際に動かした行だけを数える。
        n = sum(1 for r in seq[last_survey + 1:]
                if (r.get("type") == "cp_model_edit" and r.get("action") == "place")
                or r.get("type") == "cp_model_edit_revert")
        if n:
            bad.append((fname, n))
    return bad


def collect_calibrations(records):
    """先端校正の記録。セッション間のばらつきは誤差予算の一部なので、まとめて見る。"""
    out = []
    for fname, r in records:
        if r.get("type") == "tip_calib_done":
            out.append({
                "file": fname,
                "residual_mm": r.get("residual_mm"),
                "drift_mm": r.get("drift_mm"),
                "spread_deg": r.get("spread_deg"),
                "n": r.get("n"),
                "offset_mm": None if r.get("ox") is None else
                             1000.0 * (r["ox"] ** 2 + r["oy"] ** 2 + r["oz"] ** 2) ** 0.5,
            })
    return out


def summarize_overlay(rows, unit="cm"):
    """測量の構成と、測量時点の見かけ誤差（診断値）をまとめる。→ collect_overlay の注記。"""
    k = 100.0 if unit == "cm" else 1000.0
    # 未アラインで測った点は err_m = -1。誤差ではないので統計から外す。
    errs = [r["err_m"] * k for r in rows
            if isinstance(r.get("err_m"), (int, float)) and r["err_m"] >= 0]
    if not errs:
        return None
    errs_sorted = sorted(errs)
    return {
        "n": len(errs),
        "mean": statistics.mean(errs),
        "median": statistics.median(errs),
        "max": max(errs),
        "min": min(errs),
        "sd": statistics.stdev(errs) if len(errs) > 1 else 0.0,
        "p95": errs_sorted[max(0, int(round(0.95 * (len(errs) - 1))))],
        "unit": unit,
        "n_surveys": len({(r["file"], r["survey"], r["trial"]) for r in rows}),
        "n_points": len({r["name"] for r in rows}),
        # 姿勢が未適用のまま測った点。err_m は出せないが、測量そのものは成立している。
        "n_unposed": sum(1 for r in rows
                         if isinstance(r.get("err_m"), (int, float)) and r["err_m"] < 0),
        # 前回セッションの校正値を流用した測定。系統誤差が乗っている可能性があり、
        # ログからは検出できないので、件数を必ず出す。
        "restored_tip": sum(1 for r in rows if r.get("tip_source") == "Restored"),
    }


def summarize_per_point(rows, unit="cm"):
    """点ごとの見かけ誤差。特定の CP だけ大きい（＝実物を取り違えた）ケースを見つけるため。"""
    k = 100.0 if unit == "cm" else 1000.0
    by = {}
    for r in rows:
        if not isinstance(r.get("err_m"), (int, float)) or r["err_m"] < 0:
            continue
        by.setdefault(r["name"], []).append(r["err_m"] * k)
    return {name: {"n": len(v), "mean": statistics.mean(v), "median": statistics.median(v), "max": max(v)}
            for name, v in sorted(by.items())}


# ---------------------------------------------------------------- E2

def collect_trials(records):
    """試行ごとに開始・収束・終了をまとめる。"""
    trials = {}   # (file, trial) -> dict

    def slot(fname, r):
        key = (fname, r.get("trial", 0))
        return trials.setdefault(key, {
            "file": fname, "trial": key[1], "mode": r.get("mode", ""),
            "label": None, "t_start": None, "t_first_kf": None, "t_lock": None,
            "kf_at_lock": None, "kf_total": 0, "verdict": None,
            "time_to_lock_s": None, "rmse": None, "inliers": None, "n": None,
            "sigma": None, "n_sim3_rx": 0, "n_rejected": 0,
        })

    for fname, r in records:
        if r.get("trial", 0) == 0:
            continue                       # 試行外の記録
        t = slot(fname, r)
        typ = r.get("type")
        if typ == "trial_start":
            t["t_start"] = r.get("t")
            t["label"] = r.get("label")
            t["mode"] = r.get("mode", t["mode"])
        elif typ == "kf_sent":
            if t["t_first_kf"] is None:
                t["t_first_kf"] = r.get("t")
            t["kf_total"] = max(t["kf_total"], r.get("req", 0) or 0)
        elif typ == "auto_aligned":
            if t["t_lock"] is None:
                t["t_lock"] = r.get("t")
                t["kf_at_lock"] = r.get("kf_sent")
                t["rmse"] = r.get("rmse")
                t["inliers"] = r.get("inliers")
                t["n"] = r.get("n")
                t["sigma"] = r.get("sigma")
        elif typ == "sim3_received":
            t["n_sim3_rx"] += 1
        elif typ == "sim3_gate":
            if r.get("accepted") is False:
                t["n_rejected"] += 1
        elif typ == "trial_end":
            t["verdict"] = r.get("verdict")
            if isinstance(r.get("time_to_lock_s"), (int, float)) and r["time_to_lock_s"] >= 0:
                t["time_to_lock_s"] = r["time_to_lock_s"]
            if r.get("kf_at_lock"):
                t["kf_at_lock"] = r["kf_at_lock"]
            if r.get("kf_total"):
                t["kf_total"] = max(t["kf_total"], r["kf_total"])

    # trial_end が無い（アプリが落ちた等）試行のために、イベント列から収束時間を復元する
    for t in trials.values():
        if t["time_to_lock_s"] is None and t["t_lock"] is not None and t["t_first_kf"] is not None:
            t["time_to_lock_s"] = round(t["t_lock"] - t["t_first_kf"], 3)
        if t["verdict"] is None:
            t["verdict"] = "completed" if t["kf_total"] else "incomplete"

    return [trials[k] for k in sorted(trials)]


def summarize_coldstart(trials, mode_filter=""):
    """収集試行の要約。aborted / incomplete は分母から外す（実験者都合の中断のため）。

    **void_tracking も分母から外れる**（下の counted に含めない）。これは Quest のワールド
    原点が動いた試行で、位置合わせ自体は成立していても座標系が別物になっているため
    測定として成立しない。ExperimentController が検出時に trial_end を上書きするので、
    verdict を見るだけで除外できる（→ CLAUDE.md §13 の退化 6）。

    評価の主結果は analysis/accuracy_vs_time.py の曲線。ここは補助的に
    「何試行を完了し、そのうち何回は自動確定まで至ったか」を見るためのもの。"""
    ts = [t for t in trials if (not mode_filter or mode_filter in (t["mode"] or ""))]
    counted = [t for t in ts if t["verdict"] in ("completed", "success", "timeout")]
    if not counted:
        return None
    # 「確定に至ったか」は auto_aligned が記録されたかで判定する（verdict ではない）
    ok = [t for t in counted if t["t_lock"] is not None]
    times = [t["time_to_lock_s"] for t in ok if isinstance(t["time_to_lock_s"], (int, float))]
    kfs = [t["kf_at_lock"] for t in ok if isinstance(t["kf_at_lock"], (int, float))]
    return {
        "n_trials": len(counted),
        "n_success": len(ok),
        "n_excluded": len(ts) - len(counted),
        "time_median": statistics.median(times) if times else None,
        "time_mean": statistics.mean(times) if times else None,
        "time_min": min(times) if times else None,
        "time_max": max(times) if times else None,
        "kf_mean": statistics.mean(kfs) if kfs else None,
        "kf_median": statistics.median(kfs) if kfs else None,
    }


# ---------------------------------------------------------------- 出力

def write_csv(path, rows, fields):
    if not rows:
        return
    with open(path, "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=fields, extrasaction="ignore")
        w.writeheader()
        w.writerows(rows)
    print(f"  書き出し: {path}")


def write_table1(path, cs, unit):
    """論文の Table 1 の中身。未取得の指標は X.X のまま残す（埋め忘れを目立たせる）。

    オーバーレイ誤差の行は**わざと空のまま**にしてある。測量時の err_m は
    「測量した瞬間に適用されていた姿勢」に対する見かけの値でしかなく、試行の精度ではない
    （→ collect_overlay の注記）。この行は experiment_report.py の出力（試行ごとに
    姿勢を解き直した CP 誤差の中央値）を見て手で埋める。
    """
    def f(v, d=1):
        return f"{v:.{d}f}" if isinstance(v, (int, float)) else "X.X"

    lines = [
        r"% analysis/analyze_experiments.py が生成。数値の出所はこのスクリプトの出力。",
        r"% ただし Overlay error の行だけは experiment_report.py の出力から手で埋めること。",
        r"\begin{table}[t]",
        r"  \caption{Quantitative results.}",
        r"  \label{tab:results}",
        r"  \begin{tabular}{lr}",
        r"    \toprule",
        r"    Metric & Value \\",
        r"    \midrule",
    ]
    lines.append(r"    % ↓ experiment_report.py の「== CP ごとの誤差 ==」から手で埋める")
    lines.append(r"    Overlay error, mean / median / max & X.X / X.X / X.X\,cm \\")
    if cs:
        lines.append(f"    Cold-start success rate & {cs['n_success']} / {cs['n_trials']} \\\\")
        lines.append(f"    Time to first lock, median & {f(cs['time_median'])}\\,s \\\\")
        lines.append(f"    Keyframes to first lock, mean & {f(cs['kf_mean'])} \\\\")
    else:
        lines += [r"    Cold-start success rate & XX / 10 \\",
                  r"    Time to first lock, median & X.X\,s \\",
                  r"    Keyframes to first lock, mean & X.X \\"]
    lines += [r"    \bottomrule", r"  \end{tabular}", r"\end{table}", ""]
    Path(path).write_text("\n".join(lines), encoding="utf-8")
    print(f"  書き出し: {path}")


def plot_overlay(path, rows, unit):
    try:
        import matplotlib
        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
    except ImportError:
        print("  (matplotlib 未導入のため図は省略)")
        return
    k = 100.0 if unit == "cm" else 1000.0
    by = {}
    for r in rows:
        if isinstance(r.get("err_m"), (int, float)) and r["err_m"] >= 0:
            by.setdefault(r["name"], []).append(r["err_m"] * k)
    if not by:
        return
    names = sorted(by)
    fig, ax = plt.subplots(figsize=(1.1 * len(names) + 2, 3.4))
    data = [by[n] for n in names]
    try:                                  # matplotlib 3.9+ で labels → tick_labels に改名
        ax.boxplot(data, tick_labels=names, showmeans=True)
    except TypeError:
        ax.boxplot(data, labels=names, showmeans=True)
    # **論文の図ではない。** 測量時点の姿勢に対する見かけの誤差で、試行の精度ではない
    # （→ collect_overlay の注記）。軸ラベルでもそう名乗らせる。
    ax.set_ylabel(f"apparent error at survey time [{unit}]")
    ax.set_xlabel("control point")
    ax.grid(axis="y", alpha=0.3)
    fig.tight_layout()
    fig.savefig(path, dpi=200)
    print(f"  書き出し: {path}")


# ---------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser(description="実機ログから Table 1 の数値を出す")
    ap.add_argument("logs", help="exp_*.jsonl のあるディレクトリ、または単一ファイル")
    ap.add_argument("--outdir", default="analysis_out")
    ap.add_argument("--unit", choices=["cm", "mm"], default="cm")
    ap.add_argument("--e2-mode", default="",
                    help="集計対象の mode 名で絞る（既定は全試行）")
    args = ap.parse_args()

    paths = find_logs(args.logs)
    if not paths:
        raise SystemExit(f"ログが見つかりません: {args.logs}")
    print(f"ログ {len(paths)} ファイルを読み込み: {', '.join(p.name for p in paths)}")

    records = load_records(paths)
    print(f"レコード {len(records)} 件\n")

    outdir = Path(args.outdir)
    outdir.mkdir(parents=True, exist_ok=True)

    # ---- CP の測量（E1 の入力。精度そのものは experiment_report.py が出す）----
    cp_rows = collect_overlay(records)
    ov = summarize_overlay(cp_rows, args.unit)
    u = args.unit
    print("=== CP 測量の確認（E1 の精度は experiment_report.py が算出）===")
    if cp_rows:
        n_surveys = len({(r["file"], r["survey"], r["trial"]) for r in cp_rows})
        n_points = len({r["name"] for r in cp_rows})
        print(f"  測量 {n_surveys} 回 / 記録 {len(cp_rows)} 点（CP {n_points} 種）")
        if n_surveys > 1:
            # 実験の途中で測り直したということは、ワールド原点が動いた可能性が高い
            # （TrackingOriginWatcher の検出で測量が無効化される）。試行との対応に注意。
            print("  [注意] 測量が複数回あります。試行がどの測量に紐づくかは "
                  "trial_start.survey を見てください（experiment_report.py の 測量 列）。")
        restored = sum(1 for r in cp_rows if r.get("tip_source") == "Restored")
        if restored:
            print(f"  [注意] 前回セッションの校正値を流用した点が {restored} 件あります。"
                  f"その間に物理構成（コントローラ・治具）が変わっていた場合、系統誤差が乗ります。")
        if ov:
            # 参考値。測量した時点でたまたま適用されていた姿勢に対する見かけの誤差で、
            # 試行の精度ではない。大きく外れた点だけ「実物を取り違えた」目印になる。
            print(f"  測量時点の見かけ誤差（診断値・{ov['n']} 点）: "
                  f"平均 {ov['mean']:.2f}{u} / 中央値 {ov['median']:.2f}{u} / 最大 {ov['max']:.2f}{u}")
            print("  点ごと:")
            for name, s in summarize_per_point(cp_rows, u).items():
                print(f"    {name}: n={s['n']:3d} 平均 {s['mean']:5.2f}{u} "
                      f"中央値 {s['median']:5.2f}{u} 最大 {s['max']:5.2f}{u}")
        if not ov or ov["n_unposed"]:
            n_unposed = ov["n_unposed"] if ov else len(cp_rows)
            print(f"  （未アラインのまま測量した点が {n_unposed} 件。見かけ誤差は出ませんが、"
                  f"測量そのものは成立しています）")
    else:
        print("  cp_survey の記録がありません（実験前の測量が未実施）")
    print()

    # ---- model 側 CP の VR 編集（測量の前段。あれば健全性だけ見る）----
    edits = collect_model_edits(records)
    if edits:
        places = [e for e in edits if e["kind"] == "place"]
        moved = [e["moved_m"] for e in places if isinstance(e["moved_m"], (int, float))]
        print("=== model 側 CP の VR 内編集 ===")
        print(f"  編集セッション {sum(1 for e in edits if e['kind'] == 'start')} 回 / "
              f"置き直し {len(places)} 点 / 差し戻し {sum(1 for e in edits if e['kind'] == 'revert')} 点")
        if moved:
            k = 100.0 if u == "cm" else 1.0
            print(f"  ビルド値からの移動: 中央値 {statistics.median(moved) * k:.1f}{u} / "
                  f"最大 {max(moved) * k:.1f}{u}")
            if max(moved) > 1.0:
                print("  [注意] 1m 以上動かした点があります。"
                      "別の特徴に触っていないか（点の取り違え）確認してください。")
        if any(e["kind"] == "start" and e["aligned"] for e in edits):
            print("  [警告] **位置合わせ済みの状態で編集しています。** オーバーレイが重なって"
                  "見える状態でモデル側の点を動かすと、無意識に実物へ寄せてしまい誤差が 0 側に"
                  "バイアスします。このログの精度は報告に使えません。")
        after = edits_after_survey(records)
        if after:
            print("  [警告] **測量のあとに編集しています**（測り直しがありません）: "
                  + ", ".join(f"{f}（{n} 点）" for f, n in after))
            print("         解析は local を cp_survey の行から取る（記録時のスナップショット）ので、"
                  "編集後の座標とログが食い違います。測量からやり直してください。")
        rest = sum(1 for e in places if e.get("tip_source") == "Restored")
        if rest:
            print(f"  [注意] 前回セッションの先端校正値で置いた点が {rest} 件あります。")
        print()

    # ---- 先端校正 ----
    calibs = collect_calibrations(records)
    if calibs:
        res = [c["residual_mm"] for c in calibs if isinstance(c["residual_mm"], (int, float))]
        drf = [c["drift_mm"] for c in calibs if isinstance(c["drift_mm"], (int, float)) and c["drift_mm"] >= 0]
        print("=== 先端校正 ===")
        print(f"  校正回数 {len(calibs)}")
        if res:
            print(f"  残差 RMS: 平均 {statistics.mean(res):.2f}mm / 最大 {max(res):.2f}mm"
                  + (f" / SD {statistics.stdev(res):.2f}mm" if len(res) > 1 else ""))
        if drf:
            # セッションをまたいだオフセットのばらつき。これ自体が誤差予算の一部。
            print(f"  前回値との差: 中央値 {statistics.median(drf):.2f}mm / 最大 {max(drf):.2f}mm")
            if max(drf) > 5.0:
                print("  [注意] 差が 5mm を超えた校正があります。"
                      "コントローラや治具が入れ替わっていないか確認してください。")
        print()

    # ---- E2 ----
    trials = collect_trials(records)
    cs = summarize_coldstart(trials, args.e2_mode)
    print("=== 収集試行の要約（主結果は accuracy_vs_time.py の曲線）===")
    if cs:
        print(f"  自動確定に至った試行 {cs['n_success']} / {cs['n_trials']}"
              + (f"（中断等 {cs['n_excluded']} 件は分母から除外）" if cs["n_excluded"] else ""))
        if cs["time_median"] is not None:
            print(f"  収束時間: 中央値 {cs['time_median']:.2f}s / 平均 {cs['time_mean']:.2f}s "
                  f"/ 範囲 {cs['time_min']:.2f}–{cs['time_max']:.2f}s")
            print(f"  ロックまでのキーフレーム: 平均 {cs['kf_mean']:.1f} / 中央値 {cs['kf_median']:.1f}")
        print("  試行の内訳:")
        for t in trials:
            if args.e2_mode and args.e2_mode not in (t["mode"] or ""):
                continue
            tt = f"{t['time_to_lock_s']:.2f}s" if isinstance(t["time_to_lock_s"], (int, float)) else "-"
            print(f"    #{t['trial']:2d} {t['label'] or '':10s} {t['verdict']:10s} "
                  f"収束 {tt:>8s}  KF {t['kf_at_lock'] or '-'}  棄却 {t['n_rejected']}")
    else:
        print("  trial の記録がありません")
    print()

    # ---- 出力 ----
    print("=== 出力 ===")
    write_csv(outdir / "cp_survey.csv", cp_rows,
              ["file", "survey", "trial", "name", "err_m", "tip_residual_mm", "tip_source", "t"])
    write_csv(outdir / "tip_calibrations.csv", calibs,
              ["file", "residual_mm", "drift_mm", "spread_deg", "n", "offset_mm"])
    if edits:
        write_csv(outdir / "model_cp_edits.csv", edits,
                  ["file", "kind", "t", "name", "moved_m", "aligned", "tip_source"])
    write_csv(outdir / "trials.csv", trials,
              ["file", "trial", "mode", "label", "verdict", "time_to_lock_s", "kf_at_lock",
               "kf_total", "rmse", "inliers", "n", "sigma", "n_sim3_rx", "n_rejected"])
    write_table1(outdir / "table1.tex", cs, args.unit)
    plot_overlay(outdir / "survey_check.png", cp_rows, args.unit)


if __name__ == "__main__":
    main()
