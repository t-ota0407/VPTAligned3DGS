"""品質ゲート（P1）の合成値検証（依存なし・Windows で実行可）。

実行: cd server && python -m tests.test_gates   （または python tests/test_gates.py）
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from vptserver.cv import gates as GT


def check(name, cond):
    print(("  OK  " if cond else "  FAIL") + " " + name)
    assert cond, name


def test_gate_config():
    print("== gate_config: 既定・上書き・冪等 ==")
    g = GT.gate_config(None)
    check("既定 min_inliers=30", g["min_inliers"] == 30)
    check("既定 ratio=0.40（PC 側は候補を通し、正しさは Quest の連続一致で判定する）",
          g["min_inlier_ratio"] == 0.40)
    check("既定 max_rmse=0.08", g["max_rmse"] == 0.08)
    check("既定 scale_range", g["scale_range"] == [0.5, 2.0])
    g2 = GT.gate_config({"min_inliers": 15, "max_rmse": 0.1, "scale_range": [0.8, 1.2]})
    check("上書き min_inliers=15", g2["min_inliers"] == 15)
    check("上書き max_rmse=0.1", g2["max_rmse"] == 0.1)
    check("上書き scale_range", g2["scale_range"] == [0.8, 1.2])
    check("未指定 ratio は既定", g2["min_inlier_ratio"] == 0.40)
    # 冪等: 解決済み dict を再解決しても不変
    check("冪等", GT.gate_config(g2) == g2)


def test_should_accept_kf():
    print("== should_accept_kf ==")
    check("None は通す(ゲート無効)", GT.should_accept_kf(None)[0] is True)
    check("30>=30 採用", GT.should_accept_kf(30)[0] is True)
    check("29<30 棄却", GT.should_accept_kf(29)[0] is False)
    check("0 棄却", GT.should_accept_kf(0)[0] is False)
    # しきい値をゆるめれば通る
    check("cfg で緩和", GT.should_accept_kf(20, {"min_inliers": 10})[0] is True)


def test_should_publish():
    print("== should_publish ==")
    ok, why = GT.should_publish(-0.96, inliers=8, n=10, rmse_m=0.02)
    check("正常系は公開 (" + why + ")", ok is True)
    check("inliers<3 棄却", GT.should_publish(-0.96, 2, 10, 0.01)[0] is False)
    check("n=0 棄却", GT.should_publish(-0.96, 0, 0, 0.01)[0] is False)
    check("インライア率<0.40 棄却", GT.should_publish(-0.96, 3, 10, 0.01)[0] is False)
    check("インライア率=0.40 公開", GT.should_publish(-0.96, 4, 10, 0.02)[0] is True)
    check("rmse 超過 棄却", GT.should_publish(-0.96, 8, 10, 0.12)[0] is False)
    check("scale 過小 棄却", GT.should_publish(-0.3, 8, 10, 0.02)[0] is False)
    check("scale 過大 棄却", GT.should_publish(-2.5, 8, 10, 0.02)[0] is False)
    check("正スケールも |sigma| で判定", GT.should_publish(0.96, 8, 10, 0.02)[0] is True)
    # cfg で緩和すれば通る
    check("cfg で rmse 緩和", GT.should_publish(-0.96, 8, 10, 0.06, {"max_rmse": 0.1})[0] is True)


if __name__ == "__main__":
    test_gate_config()
    test_should_accept_kf()
    test_should_publish()
    print("\nALL GATE TESTS PASSED")
