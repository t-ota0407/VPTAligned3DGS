"""受信セッション（received/session_<ts>/）の切替と、CV の結果（Sim(3)）の Quest への返送。"""
import json
import os
import socket
import threading
from datetime import datetime
from pathlib import Path

from .cv.gates import DEFAULT_GATES
from .protocol import TYPE_RESULT, build
from .state import ServerState
from .udp_receiver import UdpReceiver


def write_cv_config(cv_path, cfg):
    """cv.json を一時ファイル経由でアトミックに書き出す（WSL2 の localize がライブ反映）。"""
    tmp = str(cv_path) + ".tmp"
    out = {"scene": cfg.get("scene"), "every": int(cfg.get("every", 2)),
           "min": int(cfg.get("min", 5)), "window": int(cfg.get("window", 24))}
    # 品質ゲート（P1）のしきい値。GUI 未指定なら既定を書いておく（WSL2 の watch がライブ反映）。
    for k, dv in DEFAULT_GATES.items():
        out[k] = cfg.get(k, dv)
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(out, f, indent=2)
    os.replace(tmp, cv_path)


class SessionManager:
    """受信セッション（= received/session_<ts>/）の生成と切替。

    1 セッション = 1 つの Quest ワールド座標系。セッションを跨いで座標を混ぜてはいけない
    （原点が変わるため Sim(3) が壊れる）。従来は run.py の再起動でしか切り替えられなかったが、
    実験（E1 の独立試行・E2 のコールドスタート×10）を現実的な手間で回すために、
    Quest からの RESET パケット（type=4）でも新セッションへ切り替えられるようにした。

    WSL2 側の `localize.watch` は常に「最新の session_*」を追従し、切替を検出すると
    Cc/Cw の蓄積バッファを自ら捨てる。したがって PC 側は新しいフォルダを作るだけでよい。
    """

    def __init__(self, out_base, flat, write_cv, state):
        self._out_base = out_base
        self._flat = flat
        self._write_cv = write_cv     # 新セッションの cv.json を書くコールバック
        self._state = state
        self._lock = threading.Lock()
        self._dir = None
        self._trial = 0

    @property
    def dir(self):
        with self._lock:
            return self._dir

    @property
    def base(self):
        """セッションフォルダの親（--out）。CV のベースのハートビートがここに置かれる。"""
        return self._out_base

    @property
    def result_file(self):
        return Path(self.dir) / "result_sim3.json"

    def start(self):
        """最初のセッションを作る（従来の起動時挙動と同じ）。"""
        if self._flat:
            with self._lock:
                self._dir = self._out_base
            self._state.session = "(flat)"
        else:
            self._new_dir()
        self._write_cv(self.dir)
        return self.dir

    def roll(self, info=None):
        """新しいセッションへ切り替える（Quest の試行リセット）。切替後のパスを返す。

        info は Quest が送ってきた JSON（trial 番号など）。trial.json として残し、
        後段の解析で「PC のこのセッション = Quest のこの試行」を突き合わせられるようにする。
        """
        if self._flat:
            print("[server] --flat のためリセットを無視（セッション分離が無効）")
            self._state.event("RESET 受信（--flat のため無視）", en="RESET received (ignored: --flat)",
                              level="warn")
            return self.dir

        self._trial += 1
        self._new_dir()
        self._write_cv(self.dir)
        try:
            meta = dict(info or {})
            meta["server_trial"] = self._trial
            meta["rolled_at"] = datetime.now().isoformat()
            (Path(self.dir) / "trial.json").write_text(
                json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8")
        except Exception as e:
            print("[server] trial.json 書き出し失敗:", e)
        label = (info or {}).get("label") or (info or {}).get("trial") or self._trial
        print(f"[server] === RESET: 新セッション {self._state.session}（trial={label}）===")
        self._state.event(f"RESET → 新セッション {self._state.session}（trial={label}）",
                          en=f"RESET -> new session {self._state.session} (trial={label})", level="mark")
        return self.dir

    def _new_dir(self):
        # 同一秒内の連続リセットでも衝突しないよう、既存なら連番を付ける
        base = "session_" + datetime.now().strftime("%Y%m%d_%H%M%S")
        name, k = base, 1
        while os.path.isdir(os.path.join(self._out_base, name)):
            name = f"{base}_{k}"
            k += 1
        d = os.path.join(self._out_base, name)
        os.makedirs(d, exist_ok=True)
        with self._lock:
            self._dir = d
        self._state.session = name
        # 受信カウンタは試行ごとに 0 から数え直す（GUI の表示が試行の進捗になる）
        with self._state._lock:
            self._state.keyframe_count = 0
            self._state.last_result = None
            self._state.last_result_time = None


class ResultSender(threading.Thread):
    """WSL2 が書く result_sim3.json を監視し、更新を検出したら Quest へ返送する。

    監視先はセッションごとに変わるため、毎ポーリングで現在のセッションから解決する
    （--result-file で固定した場合のみ従来通り固定パス）。
    """

    def __init__(self, rx: UdpReceiver, result_port: int,
                 sessions: "SessionManager" = None, fixed_file=None, state: ServerState = None):
        super().__init__(daemon=True)
        self.rx = rx
        self.state = state if state is not None else ServerState()
        self.sessions = sessions
        self.fixed_file = Path(fixed_file) if fixed_file else None
        self.result_port = result_port
        self._stop = threading.Event()
        self._pending = False
        self._watched = None       # 現在監視中のパス（セッション切替の検出用）
        self._last_mtime = None
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

    @property
    def result_file(self):
        return self.fixed_file if self.fixed_file else self.sessions.result_file

    def run(self):
        while not self._stop.is_set():
            try:
                rf = self.result_file
                if rf != self._watched:
                    # 起動時 / セッション切替時: 既存ファイルは「前の試行の結果」なので
                    # mtime をベースライン化して返送しない（別ワールド座標系の解の誤適用を防ぐ）。
                    self._watched = rf
                    self._pending = False
                    try:
                        self._last_mtime = rf.stat().st_mtime if rf.exists() else None
                    except Exception:
                        self._last_mtime = None

                if rf.exists():
                    mt = rf.stat().st_mtime
                    if mt != self._last_mtime:     # このセッションで新しく書き出された
                        self._last_mtime = mt
                        self._pending = True
                        self.rx.last_addr or print("[server] Sim(3) 検出。Quest からのキーフレーム受信後に返送します。")
                # 送信先（Quest IP）が分かっていれば送る。未確定なら次回以降に再試行（スパムしない）。
                if self._pending and self.rx.last_addr is not None:
                    self._send(rf)
                    self._pending = False
            except Exception as e:
                print("[server] 結果返送エラー:", e)
            self._stop.wait(1.0)

    def _send(self, rf):
        addr = self.rx.last_addr
        payload = rf.read_bytes()
        self.sock.sendto(build(TYPE_RESULT, payload), (addr[0], self.result_port))
        print(f"[server] Sim(3) を Quest {addr[0]}:{self.result_port} へ返送 ({len(payload)}B)")
        self.state.on_result_sent(payload, addr[0], self.result_port)

    def stop(self):
        self._stop.set()
