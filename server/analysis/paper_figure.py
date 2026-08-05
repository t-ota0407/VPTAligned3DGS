#!/usr/bin/env python3
"""論文に貼る図を書き出す（experiment_report.py の CSV から作り直す）。

experiment_report.py も PNG を吐くが、あれは作業中に形を確かめるためのもので、
本文に貼るには字が小さすぎ・ラスタ・体裁が段組みに合わない。ここでは

  - ベクタ（PDF）で出す。extended-abst は pdflatex なので PDF がそのまま貼れる
  - acmart の 1 段幅（約 3.3 in）に収まる寸法と、本文と釣り合う字の大きさにする
  - フォントを Type 42 で埋め込む（ACM の PDF 要件。既定の Type 3 は弾かれる）

を行う。数値は一切ここで計算しない。曲線は experiment_report.py の CSV、
CP ごとの内訳も同じ CSV から読む。図と本文の数字がずれないようにするため。

使い方:
    python analysis/experiment_report.py --received received --run exp_XXXX --outdir analysis_out
    python analysis/paper_figure.py --indir analysis_out --outdir ../extended-abst/figures
"""
import argparse
import csv
from collections import defaultdict
from pathlib import Path

import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402

# ACM は Type 3 フォントの入った PDF を受け付けない。42 = TrueType 埋め込み。
matplotlib.rcParams["pdf.fonttype"] = 42
matplotlib.rcParams["ps.fonttype"] = 42
# 図の中の文字は本文（9pt）より一回り小さく取る。図が本文の中で場所を取りすぎない
# ようにするため。ただし 7pt を下回ると印刷で目盛りが読めなくなるので、そこを下限に
# する（右軸の補助ラベルなど、すでに 7pt のものはこれ以上縮めない）。
matplotlib.rcParams["font.size"] = 8
matplotlib.rcParams["axes.labelsize"] = 9
matplotlib.rcParams["xtick.labelsize"] = 8
matplotlib.rcParams["ytick.labelsize"] = 8
matplotlib.rcParams["legend.fontsize"] = 7.5

COL_W = 3.33   # acmtog の 1 段幅 [in]
FULL_W = 7.0   # acmtog の 2 段ぶち抜き幅（figure*）[in]

# 図はヒューマニスト・サンセリフで組む（Noto Sans 系）。小さい級数でも字面が開いていて、
# 数字の 1/7、英字の I/l が判別しやすい。
# **TrueType が手に入る候補を先に置く。** OpenType/CFF（TeX Gyre 系など）を
# fonttype=42 で埋めると PDF 内で "CID Type 0C" になり poppler が型の不一致を警告する。
# 表示は正しく ACM の Type 3 禁止にも触れないが、入稿チェッカに引っかかる芽を残さない。
# 素の Noto Sans（Latin）が無い環境では、Latin が Source Sans 系で作られている
# Noto Sans CJK か、同系統の Lato に落とす。いずれもサンセリフのまま印象が変わらない。
SANS_STACK = ["Noto Sans", "Noto Sans Display", "Source Sans Pro",
              "Noto Sans CJK JP", "Lato", "Liberation Sans", "DejaVu Sans"]

# セリフで組みたいとき用（acmart 本文の Libertine に寄せる）。--serif で選べる。
SERIF_STACK = ["Linux Libertine O", "Linux Libertine", "Libertine",
               "Liberation Serif", "TeX Gyre Termes", "DejaVu Serif"]

# 配色は 2 色だけに絞る。赤＝その点の中央値、ティール＝全体平均、どちらも「代表値」で、
# 点や箱（無彩色）から前に出したいのはこの 2 本だけ。色を増やすほど、どれが主役か
# 読者が決められなくなる。
C_POINT = "#33507a"     # 試行ごとの生データ
C_MEDIAN = "#c0392b"    # 中央値
C_MEAN = "#1f8a7a"      # 全体平均
C_BOX_EDGE = "0.45"
C_BOX_FILL = "#eef1f5"


def use_font(family="sans"):
    """図の書体を決める。既定はサンセリフ（Noto Sans 系）。"""
    if family == "serif":
        matplotlib.rcParams["font.family"] = "serif"
        matplotlib.rcParams["font.serif"] = SERIF_STACK
        matplotlib.rcParams["mathtext.fontset"] = "stix"
    else:
        matplotlib.rcParams["font.family"] = "sans-serif"
        matplotlib.rcParams["font.sans-serif"] = SANS_STACK
        matplotlib.rcParams["mathtext.fontset"] = "dejavusans"


def beeswarm(values, step=0.052, min_gap=0.18):
    """値を縦位置に保ったまま、横に押しのけて重なりを解く（ビースウォーム）。

    乱数ジタだと、たまたま 2 点が重なって「10 点あるのに 9 点に見える」ことが起きる。
    n=10 では点の個数そのものが主張の一部（10 試行がここに重なっている）なので、
    数えられる配置でなければ意味がない。決定的に置くので図が再現する利点もある。

    `step` は 1 段ずらす幅（x 軸のカテゴリ間隔＝1 に対する比）。**正規化はしない。**
    群ごとに幅いっぱいへ広げると、ばらつきが小さい群ほど横に広がって見え、箱の細さと
    矛盾した印象を与える。段数がそのまま「同じ高さに何点あるか」を表すようにする。
    `min_gap` は同じ高さとみなす縦の間隔[cm]（点の直径ぶん）。
    """
    v = np.asarray(values, float)
    placed, off = [], np.zeros(len(v))
    for i in np.argsort(v):
        taken = {s for j, s in placed if abs(v[j] - v[i]) < min_gap}
        k = 0
        while True:                           # 0, +1, -1, +2, -2, … の順に空きを探す
            slot = 0 if k == 0 else ((k + 1) // 2) * (1 if k % 2 else -1)
            if slot not in taken:
                break
            k += 1
        off[i] = slot
        placed.append((i, slot))
    return off * step


def fnum(s):
    return float(s) if s not in ("", None) else None


def load_aggregate(p):
    rows = []
    for d in csv.DictReader(open(p, encoding="utf-8")):
        rows.append({k: (fnum(v) if k != "why" else v) for k, v in d.items()})
    return rows


def load_per_trial(p):
    rows = list(csv.DictReader(open(p, encoding="utf-8")))
    return rows


def draw_curve(ax, agg, headroom=1.55, legend_loc="upper left", right_label=None):
    """収集時間 vs CP 誤差。右軸に「その時刻で解が出た試行数」。

    右軸を必ず添えるのは、左の曲線が始まる前の区間を「精度が悪い」ではなく
    「まだ何も出していない」と読ませるため。ゲートが解を出さない間、システムは
    誤った姿勢ではなく無回答を返している。

    **縦軸は 0 から取る。** この曲線は全体で 1.2 cm の幅しかないので、自動スケールに
    任せると平坦なものが急峻な変化に見え、実際には無い収束を主張してしまう。
    """
    ok = [a for a in agg if a["median_cm"] is not None]
    t = [a["time_s"] for a in ok]
    top = max(a["q75_cm"] for a in ok)

    ax.fill_between(t, [a["q25_cm"] for a in ok], [a["q75_cm"] for a in ok],
                    color="#4c72b0", alpha=0.22, lw=0, label="interquartile range")
    ax.plot(t, [a["median_cm"] for a in ok], "-o", color="#2f5597", ms=2.6, lw=1.4,
            label="median over 10 trials")
    ax.set_xlabel("data collection time [s]")
    ax.set_ylabel("check-point error [cm]")
    ax.set_xlim(0, max(a["time_s"] for a in agg) + 2)
    ax.set_ylim(0, top * headroom)
    ax.grid(alpha=0.25, lw=0.5)
    ax.set_axisbelow(True)
    for s in ("top", "right"):
        ax.spines[s].set_visible(False)

    ax2 = ax.twinx()
    n_tr = max(a["n_trials"] for a in agg)
    ax2.plot([a["time_s"] for a in agg], [a["n_trials_solved"] for a in agg],
             color="0.55", lw=1.0, ls=":", label="trials with a solution")
    ax2.set_ylabel(right_label or f"trials with a solution (of {n_tr:.0f})",
                   color="0.45", fontsize=7)
    # 左軸と同じ倍率の余白を取るが、目盛りは実際に取りうる 0–n_tr までしか打たない
    # （余白ぶんまで目盛りを振ると「10 試行のうち 14」という読めない軸になる）。
    ax2.set_ylim(0, n_tr * headroom)
    ax2.set_yticks(list(range(0, int(n_tr) + 1, max(1, int(n_tr) // 5))))
    ax2.tick_params(labelsize=7, colors="0.45")
    ax2.spines["top"].set_visible(False)
    ax2.spines["right"].set_color("0.7")

    h1, l1 = ax.get_legend_handles_labels()
    h2, l2 = ax2.get_legend_handles_labels()
    ax.legend(h1 + h2, l1 + l2, loc=legend_loc, frameon=False, handlelength=1.8,
              borderaxespad=0.3, labelspacing=0.3)
    return ax2


def plot_curve(agg, out):
    fig, ax = plt.subplots(figsize=(COL_W, 2.15))
    draw_curve(ax, agg)
    fig.tight_layout(pad=0.25)
    fig.savefig(out, bbox_inches="tight", pad_inches=0.01)
    print("  書き出し:", out)


def plot_combined(agg, samples, out, layout="wide"):
    """時間の効きと誤差の内訳を並べた 2 パネル（**本文では使っていない**）。

    収集時間の曲線は結果として平坦で、最初に解が許容された時点でもう最終水準にある。
    平坦な曲線に図を 1 枚使うより本文の数値で足りると判断したので、本文の図は
    `plot_cp`（CP 別の箱ひげ）だけにした。この関数は時間軸を見せたくなったとき用に残す。
    """
    if layout == "wide":
        fig, axes = plt.subplots(1, 2, figsize=(FULL_W, 2.25),
                                 gridspec_kw=dict(width_ratios=[1.25, 1.0], wspace=0.42))
        draw_curve(axes[0], agg)
    else:
        fig, axes = plt.subplots(2, 1, figsize=(COL_W, 3.9),
                                 gridspec_kw=dict(height_ratios=[1.0, 1.0], hspace=0.62))
        # 1 段幅では凡例の置き場も右軸の文字数も足りない。凡例は曲線の下（空いている
        # 帯）へ、右軸の見出しは段の幅に収まる長さへ詰める。
        draw_curve(axes[0], agg, headroom=1.32, legend_loc="lower right",
                   right_label="trials solved")
    draw_cp(axes[1], samples)
    axes[0].set_title("(a) error vs. collection time", fontsize=7, pad=4)
    axes[1].set_title("(b) error per check point", fontsize=7, pad=4)
    fig.savefig(out, bbox_inches="tight", pad_inches=0.01)
    print("  書き出し:", out)


def cp_samples(indir):
    """CP ごとの誤差を「試行ぶんの生の値のまま」集める。

    要約統計に潰さないのは、図で 10 個の点をそのまま描くため。n=10 の箱ひげは
    四分位もひげも数点で決まるので、箱だけ出すと読者は分布の形を確かめようがない。
    """
    p = Path(indir) / "cp_errors.csv"
    if not p.exists():
        return None
    by = defaultdict(list)
    for d in csv.DictReader(open(p, encoding="utf-8")):
        by[d["cp"]].append(float(d["err_cm"]))
    return {k: sorted(v) for k, v in sorted(by.items())}


def draw_cp(ax, samples, seed=0, legend=True):
    """CP ごとの誤差の分布。箱ひげ＋その上に試行ごとの生データ＋全体平均の水平線。

    読ませたいのは 2 つ。**点ごとに誤差の水準が 1 桁違うこと**（1.3〜9.7 cm）と、
    **同じ点なら 10 試行がほとんど重なること**。後者は箱の潰れ具合として出るが、
    n=10 では箱ひげの形そのものが数点で決まってしまうので、生の点を必ず重ねる。

    点は横方向にだけ散らす（縦は誤差そのものなので動かせない）。散らす幅は箱より
    狭くして、どの箱に属する点かを取り違えないようにする。

    **凡例は図の中に置く。** 図の読み方（箱が何か・点が何か・破線が何か）は
    キャプションで説明すると長くなるうえ、読者が図と本文を往復することになる。
    キャプションには凡例で言えないこと（この図から何が読めるか）だけを残す。

    平均線の色は青の点・赤の中央値のどちらとも色相をずらしたティール。中央値は箱の中の
    短い実線、平均は図幅いっぱいの破線なので、色が転んでも取り違えようがない。
    """
    names = list(samples)
    data = [samples[n] for n in names]
    pos = range(len(names))
    grand_mean = float(np.mean([v for d in data for v in d]))

    # 平均線はいちばん奥。箱にも点にも被せない（背景の基準線として読ませる）。
    ax.axhline(grand_mean, color=C_MEAN, ls=(0, (4, 2.4)), lw=1.0, zorder=1)

    bp = ax.boxplot(data, positions=list(pos), widths=0.52, showfliers=False,
                    showcaps=False, patch_artist=True,
                    medianprops=dict(color=C_MEDIAN, lw=1.5, solid_capstyle="butt"),
                    boxprops=dict(facecolor=C_BOX_FILL, edgecolor=C_BOX_EDGE, lw=0.7),
                    whiskerprops=dict(color=C_BOX_EDGE, lw=0.7))
    for b in bp["boxes"]:
        b.set_zorder(2)
    for w in bp["whiskers"]:
        w.set_zorder(2)
    for m in bp["medians"]:
        m.set_zorder(5)

    for i, v in enumerate(data):
        ax.scatter(i + beeswarm(v), v, s=5.0, facecolor=C_POINT, lw=0,
                   alpha=0.85, zorder=4)

    ax.set_xticks(list(pos))
    ax.set_xticklabels(names)          # CP_01 … CP_06。本文・ログと同じ名前で呼ぶ
    ax.set_xlabel("check point")
    ax.set_ylabel("check-point error [cm]")
    ax.set_ylim(0, np.ceil(max(max(v) for v in data) / 2) * 2)
    ax.set_xlim(-0.60, len(names) - 0.40)
    ax.yaxis.set_major_locator(matplotlib.ticker.MultipleLocator(2))
    ax.grid(axis="y", color="0.90", lw=0.6)
    ax.set_axisbelow(True)
    for s in ("top", "right"):
        ax.spines[s].set_visible(False)
    for s in ("left", "bottom"):
        ax.spines[s].set_color("0.5")
        ax.spines[s].set_linewidth(0.7)
    ax.tick_params(length=2.5, width=0.7, color="0.5", pad=2)

    if legend:
        from matplotlib.lines import Line2D
        from matplotlib.patches import Rectangle
        handles = [
            Line2D([], [], ls="none", marker="o", ms=2.4, color=C_POINT,
                   label="trial"),
            Rectangle((0, 0), 1, 1, fc=C_BOX_FILL, ec=C_BOX_EDGE, lw=0.7,
                      label="quartiles / range"),
            Line2D([], [], color=C_MEDIAN, lw=1.5, label="median"),
            Line2D([], [], color=C_MEAN, ls=(0, (4, 2.4)), lw=1.0,
                   label=f"overall mean, {grand_mean:.1f} cm"),
        ]
        # 凡例は軸の外（上）に 2 行 2 列で置く。中に置くと箱の並びの上に居座って、
        # 「どこまでがデータでどこからが説明か」の境目が濁る。1 行 4 列にすると
        # 段幅では字を詰めるしかなくなるので、行を増やして級数を保つ。
        ax.legend(handles=handles, loc="lower left", bbox_to_anchor=(0, 1.01, 1, 0.14),
                  mode="expand", ncol=2, frameon=False,
                  handlelength=1.6, handletextpad=0.6, labelspacing=0.35,
                  columnspacing=1.2, borderaxespad=0)


def plot_cp(samples, out):
    # 高さは字を一回り小さくしたぶんだけ詰める（2.1 → 1.85 in）。箱の縦の潰れ具合が
    # 主張なので、これ以上低くすると 10 点の重なりが読めなくなる。
    fig, ax = plt.subplots(figsize=(COL_W, 1.85))
    draw_cp(ax, samples)
    fig.tight_layout(pad=0.25)
    fig.savefig(out, bbox_inches="tight", pad_inches=0.01)
    print("  書き出し:", out)


def main():
    ap = argparse.ArgumentParser(description="論文用の図を書き出す")
    ap.add_argument("--indir", default="analysis_out", help="experiment_report.py の出力")
    ap.add_argument("--outdir", default="../extended-abst/figures")
    ap.add_argument("--font", choices=("sans", "serif"), default="sans",
                    help="図の書体。既定は sans（Noto Sans 系）")
    args = ap.parse_args()

    use_font(args.font)

    out = Path(args.outdir)
    out.mkdir(parents=True, exist_ok=True)

    samples = cp_samples(args.indir)
    if not samples:
        raise SystemExit("cp_errors.csv がありません。experiment_report.py を新しい版で回してください。")

    # 本文の図。CP 別の誤差分布（箱ひげ＋生データ）。
    plot_cp(samples, out / "results.pdf")

    # 参考図（本文では使っていない）。収集時間の効き。
    agg = load_aggregate(Path(args.indir) / "curve_aggregate.csv")
    plot_curve(agg, out / "accuracy.pdf")
    plot_combined(agg, samples, out / "results_time_and_cp.pdf", layout="col")


if __name__ == "__main__":
    main()
