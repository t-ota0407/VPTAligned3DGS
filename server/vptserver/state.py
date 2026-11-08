"""GUI とコンソールが読む、サーバの共有状態。"""
import json
import threading
import time
from collections import deque
from datetime import datetime


# この秒数以上 PING が途切れてから届いたら、イベント欄に「Quest 接続」を出し直す（毎回は出さない）
QUEST_RECONNECT_NOTE_S = 10.0

# イベント欄の行の重要度（GUI の色）。debug は「すべて」でだけ見える
#   error 赤 / warn 琥珀 / ok 緑（準備ができた・結果を返した）/ mark 青（セッション・試行の区切り）/
#   info 通常 / debug 薄い（キーフレームの受信・CV の細かい出力）
LEVELS = ("debug", "info", "mark", "ok", "warn", "error")

# 残す行数。「重要」と「すべて」を別に持つ: CV の細かい出力（1 試行で数百行）が、
# RESET や CV のエラーのような重要な行を押し出して消さないように。
EVENTS_IMPORTANT_MAX = 200
EVENTS_ALL_MAX = 800


class ServerState:
    """GUI が読む共有状態（スレッドセーフ）。

    イベント欄の行は日本語（msg）と英語（en）の両方を持つ。GUI が選んでいる言語で出す
    （en が無い行は msg のまま。CV の出力そのものなど）。行は古い順に並び、通し番号（id）と
    書き換えの回数（rev）を持つ。GUI はこれで「新しく増えた行」だけを足す（毎秒すべてを書き直すと、
    読んでいる途中でスクロールが戻ってしまう）。
    """

    def __init__(self):
        self._lock = threading.Lock()
        self.started = time.time()
        self.ports = {}
        self.beacon_on = False
        self.session = None          # 現在のセッションフォルダ名
        self.quest_ip = None
        self.last_kf_time = None
        self.last_ping_time = None   # Quest の PING（2 秒ごと）。キーフレームを送っていない間の生存はこれで見る
        self.keyframe_count = 0
        self.last_result = None
        self.last_result_time = None
        self.events = deque(maxlen=EVENTS_IMPORTANT_MAX)     # debug 以外
        self.events_all = deque(maxlen=EVENTS_ALL_MAX)       # すべて
        self._seq = 0
        self._kf_line = None         # イベント欄の「キーフレーム受信 N 枚」の行（続く間は書き換える）

    def event(self, msg, en=None, level="info"):
        """イベント欄に 1 行足す。msg は日本語、en は英語（無ければ msg を出す）、level は重要度（LEVELS）。"""
        with self._lock:
            self._add(msg, en, level)

    def _add(self, msg, en, level):
        """行を足す（ロックの中で呼ぶ）。足した行を返す。"""
        self._seq += 1
        line = {"id": self._seq, "rev": 0, "t": datetime.now().strftime("%H:%M:%S"),
                "msg": msg, "en": en, "level": level if level in LEVELS else "info"}
        self.events_all.append(line)
        if line["level"] != "debug":
            self.events.append(line)
        return line

    def on_ping(self, ip):
        """Quest から PING が届いた。キーフレームを送っていない間（試行前・デモの表示中）も
        Quest がつながっていると分かるようにする（以前はキーフレームでしか接続を表示しなかった）。"""
        now = time.time()
        with self._lock:
            last = self._last_contact()
            changed = ip != self.quest_ip
            self.quest_ip = ip
            self.last_ping_time = now
        if changed or last is None or now - last >= QUEST_RECONNECT_NOTE_S:
            self.event(f"Quest 接続: {ip}", en=f"Quest connected: {ip}", level="ok")

    def _last_contact(self):
        """PING とキーフレームの新しいほうの時刻（ロックの中で呼ぶ）。"""
        ts = [x for x in (self.last_ping_time, self.last_kf_time) if x is not None]
        return max(ts) if ts else None

    def on_keyframe(self, req, ip):
        """キーフレームを 1 枚受け取った。

        イベント欄には 1 枚ごとに足さず、**続けて届いている間は 1 行にまとめて書き換える**（rev を上げる）。
        1 試行で 20 枚以上届くので、1 枚 1 行だと欄が埋まる。枚数は GUI の「このセッション」にも出るので、
        この行は「すべて」でだけ見える（debug）。
        """
        with self._lock:
            self.keyframe_count += 1
            self.quest_ip = ip
            self.last_kf_time = time.time()
            n = self.keyframe_count
            msg = f"キーフレーム受信 {n} 枚（最新 req={req}・{ip}）"
            en = f"Keyframes received: {n} (latest req={req} from {ip})"
            top = self.events_all[-1] if self.events_all else None
            if top is not None and top is self._kf_line:
                # 直前の行もキーフレームなら、その行を書き換える（同じ id・rev を上げた別の dict に置き換える。
                # GUI が読んでいる最中の dict を書き換えないように）
                line = dict(top, rev=top["rev"] + 1, t=datetime.now().strftime("%H:%M:%S"), msg=msg, en=en)
                self.events_all[-1] = line
            else:
                line = self._add(msg, en, "debug")
            self._kf_line = line

    def on_result_sent(self, payload, ip, port):
        try:
            d = json.loads(payload.decode())
        except Exception:
            d = None
        with self._lock:
            self.last_result = d
            self.last_result_time = time.time()
        if d:
            tail = (f"scale={d.get('sigma'):.4f} "
                    f"inliers={d.get('inliers')}/{d.get('n')} rmse={d.get('rmse'):.4f}")
            self.event(f"Sim(3) 返送→{ip}:{port}  {tail}", en=f"Sim(3) sent -> {ip}:{port}  {tail}", level="ok")
        else:
            self.event(f"結果返送→{ip}:{port}", en=f"Result sent -> {ip}:{port}", level="ok")

    def snapshot(self):
        with self._lock:
            now = time.time()
            contact = self._last_contact()
            return dict(
                uptime=int(now - self.started),
                ports=dict(self.ports),
                beacon_on=self.beacon_on,
                session=self.session,
                quest_ip=self.quest_ip,
                kf_age=(None if self.last_kf_time is None else round(now - self.last_kf_time, 1)),
                # Quest から最後に何か（PING かキーフレーム）が届いてからの秒数
                quest_age=(None if contact is None else round(now - contact, 1)),
                keyframe_count=self.keyframe_count,
                last_result=self.last_result,
                result_age=(None if self.last_result_time is None else round(now - self.last_result_time, 1)),
                events=list(self.events),            # 重要（古い順）
                events_all=list(self.events_all),    # すべて（古い順）
            )
