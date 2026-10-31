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
    check("既定 scale_range は健全性チェックのみ（シーン非依存）",
          g["scale_range"] == [1e-3, 1e3])
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
    check("正スケールも |sigma| で判定", GT.should_publish(0.96, 8, 10, 0.02)[0] is True)
    # cfg で緩和すれば通る
    check("cfg で rmse 緩和", GT.should_publish(-0.96, 8, 10, 0.06, {"max_rmse": 0.1})[0] is True)


def test_scale_is_not_scene_gated():
    """回帰: シーン固有のスケールを既定ゲートで落とさない（bld2 で 16 セッション全滅した）。

    COLMAP の 1 unit が何 m かは再構成ごとに違う。既定は発散だけを落とす。
    """
    print("== scale は既定ではシーン非依存（健全性のみ）==")
    # bld2 実測: 1 unit ≈ 4.52 m, rmse 0.038 m, インライア率 0.875。旧既定 [0.5,2.0] は全滅させた
    ok, why = GT.should_publish(-4.5226, inliers=21, n=24, rmse_m=0.0381)
    check("bld2 の |scale|=4.52 を公開する (" + why + ")", ok is True)
    check("小さいシーンスケール 0.05 も公開", GT.should_publish(-0.05, 8, 10, 0.02)[0] is True)
    check("大きいシーンスケール 120 も公開", GT.should_publish(-120.0, 8, 10, 0.02)[0] is True)
    # 落とすのは発散した解だけ
    check("scale=0 は棄却", GT.should_publish(0.0, 8, 10, 0.02)[0] is False)
    check("scale=inf は棄却", GT.should_publish(float("inf"), 8, 10, 0.02)[0] is False)
    check("scale=nan は棄却", GT.should_publish(float("nan"), 8, 10, 0.02)[0] is False)
    check("cfg で明示的に締められる",
          GT.should_publish(-4.52, 8, 10, 0.02, {"scale_range": [0.5, 2.0]})[0] is False)


def test_expected_scale_overrides_range():
    """expected_scale を指定したら、それが唯一のスケール判定になる。

    かつては scale_range が先に評価され、expected_scale を渡しても
    レンジ側で落ちる罠があった（例: 期待 4.52 なのにレンジ [0.5,2.0] が先に棄却）。
    """
    print("== expected_scale はレンジより優先（2 段重ねの罠を作らない）==")
    cfg = {"expected_scale": 4.52, "scale_range": [0.5, 2.0]}
    check("期待どおりなら狭いレンジ設定でも公開",
          GT.should_publish(-4.5226, 21, 24, 0.0381, cfg)[0] is True)
    check("期待から外れれば棄却", GT.should_publish(-1.0, 21, 24, 0.0381, cfg)[0] is False)


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
    check(f"DemoController.cs を読めた ({cs.name})", bool(src))
    m1 = re.search(r'GuideNotEnoughMovement\s*=\s*"([^"]*)"', src)
    m2 = re.search(r'GuidePathTooStraight\s*=\s*"([^"]*)"', src)
    check("C# の照合文字列を検出", bool(m1 and m2))
    check(f"短い移動 → '{why1}' は '{m1.group(1) if m1 else '?'}' で始まる",
          ok1 is False and m1 is not None and why1.startswith(m1.group(1)))
    check(f"一直線 → '{why2}' は '{m2.group(1) if m2 else '?'}' で始まる",
          ok2 is False and m2 is not None and why2.startswith(m2.group(1)))


if __name__ == "__main__":
    test_gate_config()
    test_should_accept_kf()
    test_should_publish()
    test_scale_is_not_scene_gated()
    test_expected_scale_overrides_range()
    test_geometry_reason_prefixes_match_quest()
    print("\nALL GATE TESTS PASSED")
