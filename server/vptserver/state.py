"""GUI とコンソールが読む、サーバの共有状態。"""
import json
import threading
import time
from collections import deque
from datetime import datetime


class ServerState:
    """GUI が読む共有状態（スレッドセーフ）。"""

    def __init__(self):
        self._lock = threading.Lock()
        self.started = time.time()
        self.ports = {}
        self.beacon_on = False
        self.session = None          # 現在のセッションフォルダ名
        self.quest_ip = None
        self.last_kf_time = None
        self.keyframe_count = 0
        self.last_result = None
        self.last_result_time = None
        self.events = deque(maxlen=60)

    def event(self, msg):
        with self._lock:
            self.events.appendleft({"t": datetime.now().strftime("%H:%M:%S"), "msg": msg})

    def on_keyframe(self, req, ip):
        with self._lock:
            self.keyframe_count += 1
            self.quest_ip = ip
            self.last_kf_time = time.time()
        self.event(f"keyframe req={req} from {ip}")

    def on_result_sent(self, payload, ip, port):
        try:
            d = json.loads(payload.decode())
        except Exception:
            d = None
        with self._lock:
            self.last_result = d
            self.last_result_time = time.time()
        if d:
            self.event(f"Sim(3) 返送→{ip}:{port}  scale={d.get('sigma'):.4f} "
                       f"inliers={d.get('inliers')}/{d.get('n')} rmse={d.get('rmse'):.4f}")
        else:
            self.event(f"結果返送→{ip}:{port}")

    def snapshot(self):
        with self._lock:
            now = time.time()
            return dict(
                uptime=int(now - self.started),
                ports=dict(self.ports),
                beacon_on=self.beacon_on,
                session=self.session,
                quest_ip=self.quest_ip,
                kf_age=(None if self.last_kf_time is None else round(now - self.last_kf_time, 1)),
                keyframe_count=self.keyframe_count,
                last_result=self.last_result,
                result_age=(None if self.last_result_time is None else round(now - self.last_result_time, 1)),
                events=list(self.events),
            )
