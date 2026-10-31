"""品質ゲート（P1）: キーフレーム採否・Sim(3) 公開可否の純粋関数（依存なし・テスト可能）。

2 段のゲートで悪い解が良い状態を上書きするのを防ぐ:
  1. should_accept_kf : PnP インライア数で個々のキーフレームを Sim(3) 蓄積に入れるか判定。
  2. should_publish   : 解いた Sim(3) を Quest へ返送（result_sim3.json 書き出し）してよいか判定。

しきい値は cv.json（run.py が書き watch がライブ反映）から与える。欠損は既定値。
既定値は DevelopmentPlan.md P1 / ImplementationSpec.md §13（pnp.minInliers=30 等）と一致。
確定したら ImplementationSpec.md §13 に反映すること。
"""

import math

# しきい値の既定（cv.json に無い場合に使用）
# 役割分担（ここを間違えるとロックしなくなる）:
#   PC 側のゲート  … 「明らかにおかしい解」を落とすだけ。候補は通す。
#   Quest 側の連続一致 … 「正しいか」を決める。独立な解が一致するかで判断する。
# PC 側を厳しくすると候補が一つも届かず、連続一致の判定まで到達しない。
# 実機で 26 枚のキーフレームが採用され配置ゲートも通っているのに 1 件も公開されない、
# という状態になったため、公開側は緩めて Quest 側の一致判定に任せる設計へ寄せた。
DEFAULT_GATES = {
    "min_inliers": 30,        # KF 採用の PnP インライア下限（§13 pnp.minInliers）
    "min_inlier_ratio": 0.40, # 解公開に必要なインライア率（inliers/n）
    "max_rmse": 0.08,         # 解公開に許す world 残差 RMSE 上限[m]
                              # 部屋規模で 24 点を使うと残差は 5cm を超えうる。
                              # 厳しくしても正しさは上がらず、候補が消えるだけ。
    # |sigma| の**健全性**チェック。ソルバが発散した解（0・無限大・NaN）だけを落とす。
    # ここをシーンの実スケール付近に締めてはいけない。
    #   COLMAP の単位は再構成ごとに決まる任意単位で、1 unit が何 m かはシーン固有。
    #   かつて既定は [0.5, 2.0] だったが、これは「このシーンは 1 unit ≈ 1 m」という
    #   シーン固有の前提を汎用ゲートの顔で埋め込んでいたに等しい。実際 bld2（1 unit ≈ 4.52 m）で
    #   rmse 0.038m・インライア率 0.88 の正しい解が 16 セッション全滅し、一度もロックしなかった。
    # スケールの正しさは expected_scale（任意）で見る。既定では見ない（§5.4）。
    "scale_range": [1e-3, 1e3],
    # --- スケールの期待値（シーン固有・既定は無効）---
    # COLMAP の任意単位と実メートルの比は、その再構成に固有の固定値。
    # ただし **正しさのためには不要**（下の配置ゲート＋反射固定＋連続一致で誤りは 0 になる）。
    # シーン固有の知識を推定に注入すると「メトリックスケールを推定した」という主張が
    # 弱くなるため、評価実験では無効のままにする。デバッグ時に棄却理由を明示したいときだけ使う。
    # 指定した場合は **これがスケール判定の本体**になり、上の健全性レンジは飛ばす
    # （両方を通す作りにすると、レンジ側が先に落として expected_scale が効かない罠になる）。
    "expected_scale": None,   # 例: 0.96
    "scale_tol": 0.10,        # 期待値からの許容相対誤差（±10%）
    # --- 配置の立体性（退化の検出。ここが正しさを担保する本体）---
    # 反射を固定しても「一直線に歩いた」「その場で首を振っただけ」の場合は
    # 直線まわりの回転や並進が拘束されない。残差では検出できないので配置そのものを見る。
    #
    # 閾値の根拠（実機の雑音に合わせた運用模擬。ロック率と確定誤差の両方で評価）:
    #   0.4m / 0.15m … ほぼ直線でも確定してしまい 57% 誤り
    #   1.5m / 0.40m … 部屋を横切る(6m)で 94% ロック・誤差中央 16cm
    #                  部屋を回る(9m)で 100% ロック・誤差中央 10cm
    #                  3m 以下の歩行・直線・その場は 0%（確定させない）
    #   1.0m / 0.80m … 9m 大きく回る歩き方しか通らず、実運用でロックしなくなる（厳しすぎ）
    # **精度は「どれだけ部屋を歩いたか」でほぼ決まる。** ゲートにできるのは
    # 「足りていないときに確定させない」ことだけなので、閾値を締めても精度は上がらず
    # ロックしなくなるだけ。足りない歩行を弾き、あとは歩き方の指示で担保する。
    "min_baseline_m": 1.50,       # 最大 2 点間距離の下限（動かなすぎを弾く）
    "min_second_extent_m": 0.40,  # 第 2 主成分方向の広がり下限（＝一直線でないこと）
}


def gate_config(cfg):
    """cv.json 相当の dict からゲート設定を解決する（欠損は既定・冪等）。"""
    g = dict(DEFAULT_GATES)
    if cfg:
        for k in ("min_inliers", "min_inlier_ratio", "max_rmse",
                  "expected_scale", "scale_tol", "min_baseline_m", "min_second_extent_m"):
            v = cfg.get(k)
            if v is not None:
                g[k] = v
        sr = cfg.get("scale_range")
        if sr and len(sr) == 2:
            g["scale_range"] = [float(sr[0]), float(sr[1])]
    g["min_inliers"] = int(g["min_inliers"])
    g["min_inlier_ratio"] = float(g["min_inlier_ratio"])
    g["max_rmse"] = float(g["max_rmse"])
    g["scale_tol"] = float(g["scale_tol"])
    g["min_baseline_m"] = float(g["min_baseline_m"])
    g["min_second_extent_m"] = float(g["min_second_extent_m"])
    if g["expected_scale"] is not None:
        g["expected_scale"] = abs(float(g["expected_scale"]))
    return g


def check_geometry(cond, cfg=None):
    """カメラ中心の配置が Sim(3) を決められるだけ立体的か。

    cond は geometry.center_conditioning() の返り値。
    反射を固定しても直らない退化（一直線に歩いた等）をここで捨てる。
    残差はこの種の退化を検出できないので、配置そのものを見る必要がある。

    返り値: (ok: bool, reason: str)。
    """
    g = gate_config(cfg)
    if not cond:
        return True, "no layout info (gate disabled)"
    base = float(cond.get("baseline", 0.0))
    e2 = float(cond.get("extent2", 0.0))
    if base < g["min_baseline_m"]:
        return False, f"not enough movement: span {base:.2f} m (< {g['min_baseline_m']} m)"
    if e2 < g["min_second_extent_m"]:
        return False, (f"path too straight: spread {e2:.2f} m "
                       f"(< {g['min_second_extent_m']} m)")
    return True, f"layout ok: span {base:.2f} m, spread {e2:.2f} m"


def should_accept_kf(num_inliers, cfg=None):
    """このキーフレームを Sim(3) 蓄積に入れてよいか。

    num_inliers=None（PnP ログから取得できず不明）は通す（後方互換・呼び出し側で WARN 済み）。
    返り値: (ok: bool, reason: str)。
    """
    g = gate_config(cfg)
    if num_inliers is None:
        return True, "inliers unknown (gate disabled)"
    if num_inliers < g["min_inliers"]:
        return False, f"inliers={num_inliers}(<{g['min_inliers']})"
    return True, f"inliers {num_inliers} (>= {g['min_inliers']})"


def should_publish(sigma, inliers, n, rmse_m, cfg=None, cond=None):
    """解いた Sim(3) を Quest へ返送してよいか。返り値: (ok: bool, reason: str)。

    cond（geometry.center_conditioning の結果）を渡すと配置の立体性も併せて判定する。
    """
    g = gate_config(cfg)
    lo, hi = g["scale_range"]
    if n <= 0 or inliers < 3:
        return False, f"inliers={inliers}/{n}(<3)"
    ratio = inliers / n
    if ratio < g["min_inlier_ratio"]:
        return False, f"inlier ratio {ratio:.2f} (< {g['min_inlier_ratio']})"
    if rmse_m > g["max_rmse"]:
        return False, f"rmse={rmse_m:.4g}m(>{g['max_rmse']}m)"

    # スケールの判定は 1 段だけにする（2 段重ねると厳しいほうが黙って勝ち、
    # expected_scale を指定しても健全性レンジ側で先に落ちる罠になる）。
    exp = g["expected_scale"]
    asig = abs(sigma)
    if exp is not None and exp > 0:
        # 期待スケールが分かっているなら、これが最も安価で強力な判定になる。
        dev = abs(asig - exp) / exp
        if dev > g["scale_tol"]:
            return False, (f"|scale| {asig:.3f} deviates {dev * 100:.0f}% "
                           f"from expected {exp:.3f}")
    elif not (math.isfinite(asig) and lo <= asig <= hi):
        # 既定はシーン非依存の健全性チェックのみ（発散・NaN を落とすだけ）。
        return False, f"|scale| {asig:.3g} not finite/plausible (outside [{lo:g}, {hi:g}])"

    if cond is not None:
        ok, why = check_geometry(cond, cfg)
        if not ok:
            return False, why

    return True, f"ratio {ratio:.2f}, rmse {rmse_m:.4g} m, scale {sigma:+.3f}"
