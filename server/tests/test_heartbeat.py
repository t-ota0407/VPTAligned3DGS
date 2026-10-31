"""CV ワーカの生存信号（vptserver/cv/heartbeat.py）の検証。hloc 不要・Windows で実行可。

ここで守りたいこと:
  1. **重い処理でメインループが止まっていても、生存信号が更新され続ける**（別スレッド）。
     メインループからしか書かなかった頃は、遅い PC で正常な処理中を「CV 停止」と誤判定された。
  2. セッションのハートビートには進み具合、ベースのハートビートには生存と処理中のセッション名だけが載る。
  3. run.py がそれを読んで、PONG に正しく載せる（セッションに無いときはベースから cv_on）。

実行: python -m tests.test_heartbeat
"""
import json
import sys
import tempfile
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import importlib.util

from vptserver.cv.heartbeat import Heartbeat, HEARTBEAT_NAME

_spec = importlib.util.spec_from_file_location(
    "runmod", Path(__file__).resolve().parents[1] / "run.py")
run = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(run)

_fails = []


def check(cond, label):
    print(("  OK   " if cond else "  FAIL ") + label)
    if not cond:
        _fails.append(label)


def _read(p):
    return json.loads(Path(p).read_text(encoding="utf-8"))


def test_contents():
    print("== 書く中身（セッション / ベース） ==")
    with tempfile.TemporaryDirectory() as base:
        sess = Path(base) / "session_a"
        sess.mkdir()
        hb = Heartbeat(base, "scene_data/x")
        hb.reset_session(sess)
        hb.update(busy=True, busy_n=3, total_ok=7, done=9, window=7,
                  solve_published=True, solve_why="ratio 0.9")
        hb.write_now()
        s = _read(sess / HEARTBEAT_NAME)
        b = _read(Path(base) / HEARTBEAT_NAME)
        check(s["total_ok"] == 7 and s["done"] == 9 and s["busy"] is True and s["solve_why"] == "ratio 0.9",
              "セッション: 進み具合と採否理由が載る")
        check(b["session"] == "session_a" and b["busy"] is True and b["busy_n"] == 3,
              "ベース: 処理中のセッション名と処理中フラグが載る")
        check("total_ok" not in b and "done" not in b,
              "ベース: 枚数は載らない（別セッションの枚数と取り違えさせない）")

        hb.reset_session(Path(base) / "session_b")
        (Path(base) / "session_b").mkdir()
        hb.write_now()
        s2 = _read(Path(base) / "session_b" / HEARTBEAT_NAME)
        check("total_ok" not in s2 and s2.get("busy") is not True,
              "セッション切替で前のセッションの進み具合を持ち越さない")

    print("== セッション＝ベース（--flat 等） ==")
    with tempfile.TemporaryDirectory() as base:
        hb = Heartbeat(base, "x")
        hb.reset_session(base)
        hb.update(total_ok=2, done=2)
        hb.write_now()
        d = _read(Path(base) / HEARTBEAT_NAME)
        check(d.get("done") == 2, "同じ場所なら進み具合つきの 1 枚だけ（ベース用で上書きしない）")


def test_thread_keeps_alive_while_main_blocked():
    print("== メインループが止まっていても生存信号が更新され続ける ==")
    with tempfile.TemporaryDirectory() as base:
        sess = Path(base) / "session_a"
        sess.mkdir()
        hb = Heartbeat(base, "x", interval_s=0.2)
        hb.reset_session(sess)
        hb.update(busy=True, busy_n=25)
        hb.start()
        try:
            time.sleep(0.5)
            t1 = _read(sess / HEARTBEAT_NAME)["t"]
            time.sleep(1.0)            # ← メインループが hloc で 1 秒止まっている想定（何も書かない）
            t2 = _read(sess / HEARTBEAT_NAME)["t"]
            check(t2 - t1 >= 0.5, f"処理中もスレッドが書き続ける（{t2 - t1:.2f}s 進んだ）")
            s = run.read_cv_status(sess, base)
            check(s["cv_alive"] is True and s["cv_busy"] is True and s["cv_busy_n"] == 25,
                  "run.py から見て「生きていて処理中」")
        finally:
            hb.stop()
        t3 = _read(sess / HEARTBEAT_NAME)["t"]
        time.sleep(0.5)
        check(_read(sess / HEARTBEAT_NAME)["t"] == t3, "stop() で書き込みが止まる")


def test_reset_while_previous_session_busy():
    """Quest の RESET で新しいセッションができたが、CV は前のセッションの処理中（実機の失敗の再現）。"""
    print("== RESET 直後: CV は前のセッションを処理中 ==")
    with tempfile.TemporaryDirectory() as base:
        old = Path(base) / "session_old"; old.mkdir()
        new = Path(base) / "session_new"; new.mkdir()
        hb = Heartbeat(base, "x", interval_s=0.2)
        hb.reset_session(old)
        hb.update(busy=True, busy_n=25, total_ok=13, done=16)
        hb.start()
        try:
            time.sleep(0.5)
            s = run.read_cv_status(new, base)   # run.py は新しいセッションを見ている
            check(s["cv_alive"] is True and s.get("cv_on") == "session_old",
                  "新しいセッションには無くても「生きていて session_old を処理中」と伝わる")
            check(s.get("cv_ok", 0) == 0 and s.get("cv_done", 0) == 0,
                  "前のセッションの枚数（13 / 16）は新しいセッションのものとして伝わらない")
            pong = json.loads(run.build_pong_payload(1, "session_new", 5, s).decode())
            check(pong["cv_on"] == "session_old" and pong["session"] == "session_new",
                  "PONG: session（run.py の今のセッション）と cv_on（CV が処理中のセッション）が別に載る")

            hb.reset_session(new)                # CV が前の処理を終えて新しいセッションへ移った
            hb.update(busy=False, total_ok=0, done=0)
            hb.write_now()
            s = run.read_cv_status(new, base)
            check(s["cv_alive"] is True and "cv_on" not in s and s["cv_done"] == 0,
                  "移った後は新しいセッションのハートビートを使う")
        finally:
            hb.stop()


if __name__ == "__main__":
    test_contents()
    test_thread_keeps_alive_while_main_blocked()
    test_reset_while_previous_session_busy()
    if _fails:
        print(f"\n{len(_fails)} FAILED")
        sys.exit(1)
    print("\nALL HEARTBEAT TESTS PASSED")
