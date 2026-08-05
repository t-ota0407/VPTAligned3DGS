"""疎通確認（PING/PONG）と CV ハートビートの検証。

ここで守りたいこと:
  1. CV ハートビートの鮮度判定が正しい（無い / 新しい / 古い の 3 状態）。
  2. PONG の JSON が Quest 側の期待どおりの鍵を持つ。
  3. **C# と Python の鍵名が食い違っていない。** ここが drift すると、Quest 側は
     JsonUtility の既定値（cv_alive=false 等）を黙って使い続け、「CV 未起動」と誤表示する。
     実験の直前に気づく類のバグなので、C# のソースを読んで機械的に突き合わせる。

実行: python -m tests.test_liveness
"""
import json
import os
import re
import sys
import tempfile
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import importlib.util

_spec = importlib.util.spec_from_file_location(
    "runmod", Path(__file__).resolve().parents[1] / "run.py")
run = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(run)

CS_UDP = (Path(__file__).resolve().parents[2] /
          "unity/projects/VPTAligned3DGS/Assets/Scripts/UDPManager.cs")

_fails = []


def check(cond, label):
    print(("  OK   " if cond else "  FAIL ") + label)
    if not cond:
        _fails.append(label)


def test_cv_status():
    print("== CV ハートビートの鮮度判定 ==")
    with tempfile.TemporaryDirectory() as d:
        s = run.read_cv_status(d)
        check(s["cv_alive"] is False and s["cv_age"] == -1.0,
              "ハートビート無し → cv_alive=False, age=-1")

        hb = Path(d) / "cv_heartbeat.json"
        hb.write_text(json.dumps({"t": time.time(), "pid": 1,
                                  "scene": "scene_data/example1", "window": 5}))
        s = run.read_cv_status(d)
        check(s["cv_alive"] is True and s["cv_age"] < 1.0, "直近のハートビート → 稼働中")
        check(s.get("cv_scene") == "scene_data/example1", "scene が伝わる")

        hb.write_text(json.dumps({"t": time.time() - (run.CV_STALE_S + 5), "pid": 1}))
        s = run.read_cv_status(d)
        check(s["cv_alive"] is False and s["cv_age"] > run.CV_STALE_S,
              f"{run.CV_STALE_S}s より古い → 停止扱い")

        # 処理中は hloc が長時間ブロックしてハートビートを更新できない。
        # busy を見て猶予を延ばさないと「CV 停止」と誤表示してしまう。
        hb.write_text(json.dumps({"t": time.time() - 30, "pid": 1, "busy": True, "busy_n": 5}))
        s = run.read_cv_status(d)
        check(s["cv_alive"] is True and s["cv_busy"] is True and s["cv_busy_n"] == 5,
              "処理中(busy)なら 30 秒古くても稼働扱い")
        hb.write_text(json.dumps({"t": time.time() - 30, "pid": 1, "busy": False}))
        s = run.read_cv_status(d)
        check(s["cv_alive"] is False, "busy でなければ 30 秒古いと停止扱い")

        hb.write_text("これは JSON ではない")
        s = run.read_cv_status(d)
        check(s["cv_alive"] is False, "壊れたハートビート → 例外を出さず停止扱い")


def test_pong_payload():
    print("== PONG ペイロード ==")
    payload = run.build_pong_payload(7, "session_20260729_120000", 12,
                                     {"cv_alive": True, "cv_age": 0.4})
    d = json.loads(payload.decode("utf-8"))
    check(d["seq"] == 7, "seq をそのまま返す（RTT 計算に必要）")
    check(d["session"] == "session_20260729_120000", "session を返す")
    check(d["kf"] == 12, "PC 側の受信枚数を返す")
    check(d["cv_alive"] is True and d["cv_age"] == 0.4, "CV 状態を返す")

    d2 = json.loads(run.build_pong_payload(0, None, 0, None).decode("utf-8"))
    check(d2["session"] == "" and d2["kf"] == 0, "session=None でも壊れない")


def test_cs_python_key_parity():
    """C# の PongMsg フィールド名が、Python が実際に送る鍵と一致するか。"""
    print("== C# / Python の鍵名の一致 ==")
    if not CS_UDP.exists():
        check(False, f"UDPManager.cs が見つからない: {CS_UDP}")
        return

    src = CS_UDP.read_text(encoding="utf-8")
    m = re.search(r"class\s+PongMsg\s*\{(.*?)\}", src, re.S)
    if not m:
        check(False, "UDPManager.cs に PongMsg クラスが見つからない")
        return

    # "public int seq;" / "public float cv_age = -1f;" 等からフィールド名を拾う
    cs_fields = set()
    for line in m.group(1).splitlines():
        fm = re.match(r"\s*public\s+\w+\s+(\w+)\s*(=[^;]*)?;", line)
        if fm:
            cs_fields.add(fm.group(1))

    py_keys = set(json.loads(run.build_pong_payload(
        1, "s", 0, {"cv_alive": True, "cv_age": 0.0,
                    "cv_scene": "x", "cv_window": 3, "cv_ok": 12,
                    "cv_pub": False, "cv_why": "rmse 超過",
                    "cv_busy": True, "cv_busy_n": 5},
        ev_ack=7, ev_n=9).decode()).keys())

    check(bool(cs_fields), f"C# 側のフィールドを検出: {sorted(cs_fields)}")
    missing = py_keys - cs_fields
    check(not missing, f"Python が送る鍵はすべて C# にある（欠け: {sorted(missing) or 'なし'}）")
    extra = cs_fields - py_keys
    check(not extra, f"C# の余分なフィールドが無い（余り: {sorted(extra) or 'なし'}）")


def test_protocol_constants():
    """C# 側の type 定数が Python と一致するか（ここがずれると無言で無視される）。"""
    print("== プロトコル type 定数の一致 ==")
    from vptserver import protocol as P
    src = CS_UDP.read_text(encoding="utf-8")
    for name, want in [("TYPE_RESULT", P.TYPE_RESULT), ("TYPE_RESET", P.TYPE_RESET),
                       ("TYPE_PING", P.TYPE_PING), ("TYPE_PONG", P.TYPE_PONG),
                       ("TYPE_EVENT", P.TYPE_EVENT)]:
        m = re.search(rf"const\s+byte\s+{name}\s*=\s*(\d+)", src)
        got = int(m.group(1)) if m else None
        check(got == want, f"{name}: C#={got} Python={want}")


if __name__ == "__main__":
    test_cv_status()
    test_pong_payload()
    test_cs_python_key_parity()
    test_protocol_constants()
    print()
    if _fails:
        print(f"{len(_fails)} FAILED:")
        for f in _fails:
            print("  -", f)
        sys.exit(1)
    print("ALL LIVENESS TESTS PASSED")
