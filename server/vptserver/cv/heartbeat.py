"""CV ワーカ（localize.watch）の生存信号（ハートビート）。標準ライブラリだけで動く（テスト可能）。

**なぜ別スレッドで書くのか。**
hloc の 1 回の処理（特徴抽出 → 検索 → マッチ → PnP）はメインループを数秒〜数分止める。
メインループからしか書かないと、PC が遅いほど生存信号が途切れ、Quest は「処理中なのか止まったのか」を
時間で推測するしかなくなる（実機で 1 枚 13 秒かかり、正常に処理中の CV を「停止」と誤判定して
再位置合わせが失敗した）。別スレッドで 2 秒ごとに書けば、**生存信号の鮮度は PC の速さに依存しない**。
古くなるのはプロセスが死んだとき（または固まったとき）だけになる。

**どこに書くのか（2 か所）。**
  1. いま処理しているセッションのフォルダ  <session>/cv_heartbeat.json
     … 進み具合（採用枚数・処理済み枚数・処理中か・直近の解の採否）。run.py がそのまま Quest へ渡す。
  2. 監視しているベースのフォルダ          <base>/cv_heartbeat.json
     … 生存と「どのセッションを処理中か」だけ（枚数は書かない）。
     Quest の RESET で新しいセッションができても、CV は前のセッションの処理を終えるまで移れない。
     その間 1 は新しいセッションに現れないので、run.py は 2 を見て「CV は生きていて、
     前のセッションを片付けている」と Quest に伝える（PONG の cv_on）。
     枚数を書かないのは、前のセッションの枚数を新しいセッションのものと取り違えさせないため。
"""
import json
import os
import threading
import time
from pathlib import Path

HEARTBEAT_NAME = "cv_heartbeat.json"
INTERVAL_S = 2.0


def write_json_atomic(path, d):
    """一時ファイルに書いてから置き換える。失敗しても例外を出さない（生存信号は補助情報）。

    /mnt/c（Windows 側）では、run.py が読んでいる瞬間に置き換えが失敗することがある。
    次の周期（2 秒後）に書き直されるので、ここでは握りつぶす。
    """
    path = Path(path)
    tmp = str(path) + ".tmp"
    try:
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(d, f, ensure_ascii=False)
        os.replace(tmp, path)
        return True
    except Exception:
        return False


class Heartbeat:
    """生存信号を 2 秒ごとに書くスレッド。メインループは update() で状態を渡すだけ。

    使い方:
        hb = Heartbeat(base_dir, scene)
        hb.start()
        hb.reset_session(session_dir)                 # セッション切替
        hb.update(busy=True, busy_n=3); hb.write_now()  # 重い処理に入る直前（すぐ反映したいとき）
        hb.update(busy=False, done=..., total_ok=...)   # 処理と解き直しが終わってから
        hb.stop()
    """

    def __init__(self, base_dir, scene="", interval_s=INTERVAL_S):
        self._lock = threading.Lock()
        self._write_lock = threading.Lock()   # スレッドと write_now() の書き込みが重ならないように
        self._base = Path(base_dir)
        self._session = None
        self._scene = scene or ""
        self._state = {}
        self._interval = float(interval_s)
        self._stop = threading.Event()
        self._thread = None
        self.pid = os.getpid()

    # ---- メインループから ----
    def reset_session(self, session_dir):
        """新しいセッションへ移った。前のセッションの進み具合は持ち越さない。"""
        with self._lock:
            self._session = Path(session_dir)
            self._state = {}

    def set_scene(self, scene):
        with self._lock:
            self._scene = scene or ""

    def update(self, **fields):
        with self._lock:
            self._state.update(fields)

    def snapshot(self):
        """書き込む中身（セッション用・ベース用）を作る。テストでも使う。"""
        with self._lock:
            now = time.time()
            busy = bool(self._state.get("busy", False))
            sess = dict(self._state)
            sess.update({"t": now, "pid": self.pid, "scene": self._scene})
            base = {"t": now, "pid": self.pid, "scene": self._scene,
                    "session": self._session.name if self._session is not None else "",
                    "busy": busy, "busy_n": int(self._state.get("busy_n", 0))}
            return self._session, sess, base

    def write_now(self):
        """いまの状態をすぐ書く（重い処理に入る直前・解き直しの直後など）。"""
        session, sess, base = self.snapshot()
        with self._write_lock:
            same = session is not None and session.resolve() == self._base.resolve()
            if session is not None:
                write_json_atomic(session / HEARTBEAT_NAME, sess)
            if not same:   # セッション＝ベース（--flat 等）のときは進み具合つきの 1 枚だけにする
                write_json_atomic(self._base / HEARTBEAT_NAME, base)

    # ---- スレッド ----
    def start(self):
        if self._thread is not None:
            return
        self._stop.clear()
        self._thread = threading.Thread(target=self._run, name="cv-heartbeat", daemon=True)
        self._thread.start()

    def stop(self):
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=self._interval + 1.0)
            self._thread = None

    def _run(self):
        while not self._stop.is_set():
            self.write_now()
            self._stop.wait(self._interval)
