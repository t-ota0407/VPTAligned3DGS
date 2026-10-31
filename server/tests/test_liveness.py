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

from tests._check import check, finish  # noqa: E402

from vptserver import liveness  # noqa: E402

CS_UDP = (Path(__file__).resolve().parents[2] /
          "unity/projects/VPTAligned3DGS/Assets/Scripts/UDPManager.cs")
CS_PKT = CS_UDP.with_name("VpPacket.cs")   # パケット形式と type 定数


def write_hb(path, d, age=0.0):
    """ハートビートを書き、**ファイルの更新時刻**を age 秒前にする（run.py は更新時刻で古さを測る）。

    中の "t" も同じだけ古くする。"t" は WSL の時計なので run.py は使わないが、
    更新時刻が取れないときの代わりとして同じ値にしておく。
    """
    t = time.time() - age
    d = dict(d, t=t)
    Path(path).write_text(json.dumps(d))
    os.utime(path, (t, t))


def test_cv_status():
    print("== CV ハートビートの鮮度判定 ==")
    with tempfile.TemporaryDirectory() as d:
        s = liveness.read_cv_status(d)
        check(s["cv_alive"] is False and s["cv_age"] == -1.0,
              "ハートビート無し → cv_alive=False, age=-1")

        hb = Path(d) / "cv_heartbeat.json"
        hb.write_text(json.dumps({"t": time.time(), "pid": 1,
                                  "scene": "scene_data/example1", "window": 5}))
        s = liveness.read_cv_status(d)
        check(s["cv_alive"] is True and s["cv_age"] < 1.0, "直近のハートビート → 稼働中")
        check(s.get("cv_scene") == "scene_data/example1", "scene が伝わる")

        write_hb(hb, {"pid": 1}, age=liveness.CV_STALE_S + 5)
        s = liveness.read_cv_status(d)
        check(s["cv_alive"] is False and s["cv_age"] > liveness.CV_STALE_S,
              f"{liveness.CV_STALE_S}s より古い → 停止扱い")

        # 処理中は hloc が長時間ブロックしてハートビートを更新できない。
        # busy を見て猶予を延ばさないと「CV 停止」と誤表示してしまう。
        write_hb(hb, {"pid": 1, "busy": True, "busy_n": 5}, age=30)
        s = liveness.read_cv_status(d)
        check(s["cv_alive"] is True and s["cv_busy"] is True and s["cv_busy_n"] == 5,
              "処理中(busy)なら 30 秒古くても稼働扱い")
        write_hb(hb, {"pid": 1, "busy": False}, age=30)
        s = liveness.read_cv_status(d)
        check(s["cv_alive"] is False, "busy でなければ 30 秒古いと停止扱い")

        # WSL2 の時計が Windows より遅れている（スリープ復帰後に起きる）。中の "t" は 120 秒前でも、
        # ファイルは今書かれた → 生きている。"t" で測っていた頃は「CV 停止」と誤判定した
        hb.write_text(json.dumps({"t": time.time() - 120, "pid": 1}))
        s = liveness.read_cv_status(d)
        check(s["cv_alive"] is True and s["cv_age"] < 1.0,
              "WSL の時計が 2 分遅れていても、ファイルの更新時刻で測るので稼働扱い")

        # 処理済み枚数。デモは「送信を止めてから PC が全部処理し終えたか」を kf と比べて判定する
        hb.write_text(json.dumps({"t": time.time(), "pid": 1, "total_ok": 9, "done": 12}))
        s = liveness.read_cv_status(d)
        check(s["cv_ok"] == 9 and s["cv_done"] == 12, "処理済み枚数(done)が cv_done として伝わる")
        hb.write_text(json.dumps({"t": time.time(), "pid": 1}))
        s = liveness.read_cv_status(d)
        check(s["cv_done"] == 0, "done が無い古いハートビートでも cv_done=0（壊れない）")

        hb.write_text("これは JSON ではない")
        s = liveness.read_cv_status(d)
        check(s["cv_alive"] is False, "壊れたハートビート → 例外を出さず停止扱い")


def test_cv_status_base_fallback():
    """RESET 直後、CV が前のセッションを処理中で新しいセッションにハートビートが無いとき。

    以前は「ハートビート無し＝停止」と伝わり、Quest は正常に処理中の CV を止まったと誤判定して
    再位置合わせを 10 秒で諦めた（実機で発生）。ベースのハートビートから「生きていて、
    別のセッションを処理中」（cv_on）を伝える。枚数は前のセッションのものなので伝えない。
    """
    print("== セッションにハートビートが無いとき（ベースへ切り替え） ==")
    with tempfile.TemporaryDirectory() as base:
        sess = Path(base) / "session_new"
        sess.mkdir()
        s = liveness.read_cv_status(sess, base)
        check(s["cv_alive"] is False and s["cv_age"] == -1.0, "どこにも無い → ハートビート無し（cv_age=-1）")

        (Path(base) / "cv_heartbeat.json").write_text(json.dumps(
            {"t": time.time(), "pid": 1, "scene": "x", "session": "session_old", "busy": True, "busy_n": 25}))
        s = liveness.read_cv_status(sess, base)
        check(s["cv_alive"] is True and s["cv_on"] == "session_old" and s["cv_busy"] is True,
              "ベースが新しい → 生きていて、前のセッション(session_old)を処理中と伝わる")
        check("cv_ok" not in s and "cv_done" not in s,
              "前のセッションの枚数は伝えない（新しいセッションの枚数と取り違えさせない）")
        check(liveness.read_cv_status(sess)["cv_age"] == -1.0, "base_dir を渡さなければ従来どおり")

        write_hb(Path(base) / "cv_heartbeat.json",
                 {"pid": 1, "session": "session_old", "busy": False}, age=liveness.CV_STALE_S + 5)
        s = liveness.read_cv_status(sess, base)
        check(s["cv_alive"] is False and s["cv_age"] > liveness.CV_STALE_S, "ベースも古い → 停止扱い")

        (sess / "cv_heartbeat.json").write_text(json.dumps({"t": time.time(), "pid": 1, "total_ok": 3, "done": 4}))
        s = liveness.read_cv_status(sess, base)
        check(s["cv_alive"] is True and s["cv_done"] == 4 and "cv_on" not in s,
              "セッションにハートビートがあればそちらを使う（cv_on は付けない）")


def test_pong_payload():
    print("== PONG ペイロード ==")
    payload = liveness.build_pong_payload(7, "session_20260729_120000", 12,
                                     {"cv_alive": True, "cv_age": 0.4})
    d = json.loads(payload.decode("utf-8"))
    check(d["seq"] == 7, "seq をそのまま返す（RTT 計算に必要）")
    check(d["session"] == "session_20260729_120000", "session を返す")
    check(d["kf"] == 12, "PC 側の受信枚数を返す")
    check(d["cv_alive"] is True and d["cv_age"] == 0.4, "CV 状態を返す")

    d2 = json.loads(liveness.build_pong_payload(0, None, 0, None).decode("utf-8"))
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

    py_keys = set(json.loads(liveness.build_pong_payload(
        1, "s", 0, {"cv_alive": True, "cv_age": 0.0,
                    "cv_scene": "x", "cv_window": 3, "cv_ok": 12, "cv_done": 14, "cv_on": "session_x",
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
    src = CS_PKT.read_text(encoding="utf-8")
    for name, want in [("TYPE_META", P.TYPE_META), ("TYPE_IMG", P.TYPE_IMG),
                       ("TYPE_RESULT", P.TYPE_RESULT), ("TYPE_RESET", P.TYPE_RESET),
                       ("TYPE_PING", P.TYPE_PING), ("TYPE_PONG", P.TYPE_PONG),
                       ("TYPE_EVENT", P.TYPE_EVENT)]:
        m = re.search(rf"const\s+byte\s+{name}\s*=\s*(\d+)", src)
        got = int(m.group(1)) if m else None
        check(got == want, f"{name}: C#={got} Python={want}")
    m = re.search(r"HeaderSize\s*=\s*(\d+)", src)
    check(m is not None and int(m.group(1)) == P.HEADER_SIZE,
          f"ヘッダ長: C#={m.group(1) if m else None} Python={P.HEADER_SIZE}")


if __name__ == "__main__":
    test_cv_status()
    test_cv_status_base_fallback()
    test_pong_payload()
    test_cs_python_key_parity()
    test_protocol_constants()
    finish("ALL LIVENESS TESTS PASSED")
