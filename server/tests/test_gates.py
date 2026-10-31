"""品質ゲート（P1）の合成値検証（依存なし・Windows で実行可）。

実行: cd server && python -m tests.test_gates   （または python tests/test_gates.py）
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from tests._check import check, finish  # noqa: E402
from vptserver.cv import gates as GT


def test_gate_config():
    print("== gate_config: 既定・上書き・冪等 ==")
    g = GT.gate_config(None)
    check(g["min_inliers"] == 30, "既定 min_inliers=30")
    check(g["min_inlier_ratio"] == 0.40,
          "既定 ratio=0.40（PC 側は候補を通し、正しさは Quest の連続一致で判定する）")
    check(g["max_rmse"] == 0.08, "既定 max_rmse=0.08")
    check(g["scale_range"] == [1e-3, 1e3],
          "既定 scale_range は健全性チェックのみ（シーン非依存）")
    g2 = GT.gate_config({"min_inliers": 15, "max_rmse": 0.1, "scale_range": [0.8, 1.2]})
    check(g2["min_inliers"] == 15, "上書き min_inliers=15")
    check(g2["max_rmse"] == 0.1, "上書き max_rmse=0.1")
    check(g2["scale_range"] == [0.8, 1.2], "上書き scale_range")
    check(g2["min_inlier_ratio"] == 0.40, "未指定 ratio は既定")
    # 冪等: 解決済み dict を再解決しても不変
    check(GT.gate_config(g2) == g2, "冪等")


def test_should_accept_kf():
    print("== should_accept_kf ==")
    check(GT.should_accept_kf(None)[0] is True, "None は通す(ゲート無効)")
    check(GT.should_accept_kf(30)[0] is True, "30>=30 採用")
    check(GT.should_accept_kf(29)[0] is False, "29<30 棄却")
    check(GT.should_accept_kf(0)[0] is False, "0 棄却")
    # しきい値をゆるめれば通る
    check(GT.should_accept_kf(20, {"min_inliers": 10})[0] is True, "cfg で緩和")


def test_should_publish():
    print("== should_publish ==")
    ok, why = GT.should_publish(-0.96, inliers=8, n=10, rmse_m=0.02)
    check(ok is True, "正常系は公開 (" + why + ")")
    check(GT.should_publish(-0.96, 2, 10, 0.01)[0] is False, "inliers<3 棄却")
    check(GT.should_publish(-0.96, 0, 0, 0.01)[0] is False, "n=0 棄却")
    check(GT.should_publish(-0.96, 3, 10, 0.01)[0] is False, "インライア率<0.40 棄却")
    check(GT.should_publish(-0.96, 4, 10, 0.02)[0] is True, "インライア率=0.40 公開")
    check(GT.should_publish(-0.96, 8, 10, 0.12)[0] is False, "rmse 超過 棄却")
    check(GT.should_publish(0.96, 8, 10, 0.02)[0] is True, "正スケールも |sigma| で判定")
    # cfg で緩和すれば通る
    check(GT.should_publish(-0.96, 8, 10, 0.06, {"max_rmse": 0.1})[0] is True, "cfg で rmse 緩和")


def test_scale_is_not_scene_gated():
    """回帰: シーン固有のスケールを既定ゲートで落とさない（bld2 で 16 セッション全滅した）。

    COLMAP の 1 unit が何 m かは再構成ごとに違う。既定は発散だけを落とす。
    """
    print("== scale は既定ではシーン非依存（健全性のみ）==")
    # bld2 実測: 1 unit ≈ 4.52 m, rmse 0.038 m, インライア率 0.875。旧既定 [0.5,2.0] は全滅させた
    ok, why = GT.should_publish(-4.5226, inliers=21, n=24, rmse_m=0.0381)
    check(ok is True, "bld2 の |scale|=4.52 を公開する (" + why + ")")
    check(GT.should_publish(-0.05, 8, 10, 0.02)[0] is True, "小さいシーンスケール 0.05 も公開")
    check(GT.should_publish(-120.0, 8, 10, 0.02)[0] is True, "大きいシーンスケール 120 も公開")
    # 落とすのは発散した解だけ
    check(GT.should_publish(0.0, 8, 10, 0.02)[0] is False, "scale=0 は棄却")
    check(GT.should_publish(float("inf"), 8, 10, 0.02)[0] is False, "scale=inf は棄却")
    check(GT.should_publish(float("nan"), 8, 10, 0.02)[0] is False, "scale=nan は棄却")
    check(GT.should_publish(-4.52, 8, 10, 0.02, {"scale_range": [0.5, 2.0]})[0] is False,
          "cfg で明示的に締められる")


def test_expected_scale_overrides_range():
    """expected_scale を指定したら、それが唯一のスケール判定になる。

    かつては scale_range が先に評価され、expected_scale を渡しても
    レンジ側で落ちる罠があった（例: 期待 4.52 なのにレンジ [0.5,2.0] が先に棄却）。
    """
    print("== expected_scale はレンジより優先（2 段重ねの罠を作らない）==")
    cfg = {"expected_scale": 4.52, "scale_range": [0.5, 2.0]}
    check(GT.should_publish(-4.5226, 21, 24, 0.0381, cfg)[0] is True,
          "期待どおりなら狭いレンジ設定でも公開")
    check(GT.should_publish(-1.0, 21, 24, 0.0381, cfg)[0] is False, "期待から外れれば棄却")


def test_geometry_reason_prefixes_match_quest():
    """配置ゲートの棄却理由の先頭が、Quest のデモが照合している文字列と一致するか。

    デモの再位置合わせは PC の棄却理由（PONG の cv_why）の先頭で歩き方の指示を選ぶ
    （DemoController.GuideNotEnoughMovement / GuidePathTooStraight）。
    片側だけ文言を変えると、指示が黙って汎用文に落ちる。
    """
    print("== 配置ゲートの理由文と Quest 側の照合文字列 ==")
    import re
    from pathlib import Path
    ok1, why1 = GT.check_geometry({"baseline": 0.3, "extent2": 0.3})
    ok2, why2 = GT.check_geometry({"baseline": 3.0, "extent2": 0.05})
    cs = (Path(__file__).resolve().parents[2] / "unity" / "projects" / "VPTAligned3DGS" / "Assets"
          / "Scripts" / "Demo" / "DemoController.cs")
    src = cs.read_text(encoding="utf-8") if cs.exists() else ""
    check(bool(src), f"DemoController.cs を読めた ({cs.name})")
    m1 = re.search(r'GuideNotEnoughMovement\s*=\s*"([^"]*)"', src)
    m2 = re.search(r'GuidePathTooStraight\s*=\s*"([^"]*)"', src)
    check(bool(m1 and m2), "C# の照合文字列を検出")
    check(ok1 is False and m1 is not None and why1.startswith(m1.group(1)),
          f"短い移動 → '{why1}' は '{m1.group(1) if m1 else '?'}' で始まる")
    check(ok2 is False and m2 is not None and why2.startswith(m2.group(1)),
          f"一直線 → '{why2}' は '{m2.group(1) if m2 else '?'}' で始まる")


if __name__ == "__main__":
    test_gate_config()
    test_should_accept_kf()
    test_should_publish()
    test_scale_is_not_scene_gated()
    test_expected_scale_overrides_range()
    test_geometry_reason_prefixes_match_quest()
    finish("ALL GATE TESTS PASSED")
