#!/usr/bin/env python3
"""VPTAligned3DGS オフロードサーバ（Windows 側の UDP 受け口 ＋ ネイティブ GUI）。

- Quest から UDP で届くキーフレームを受信・再構成し ./received/ に保存。
- LAN にビーコンを流し、Quest が送信先 IP を自動検出できるようにする。
- WSL2 の CV（vptserver.cv.localize --watch）が書き出す received/result_sim3.json を検知し、
  Quest へ Sim(3) を返送（type=3 RESULT）。
- 通信状態を customtkinter のネイティブウインドウで可視化（pip install customtkinter）。
  未導入ならヘッドレスで動作。

使い方:
    python run.py                 # 受信＋ビーコン＋返送＋GUIウインドウ
    python run.py --verbose
    python run.py --no-gui --no-beacon
"""
import argparse
import json
import os
import socket
import sys
import threading
import time
from collections import deque
from datetime import datetime, timezone
from pathlib import Path

from vptserver.udp_receiver import UdpReceiver
from vptserver.reassembler import Reassembler
from vptserver.storage import save_keyframe
from vptserver.beacon import Beacon, DEFAULT_BEACON_PORT
from vptserver.protocol import (TYPE_RESULT, TYPE_RESET, TYPE_PING, TYPE_PONG,
                                TYPE_EVENT, build)
from vptserver import gui as gui_mod

# CV ハートビートがこの秒数より古ければ「CV 未稼働」とみなす（localize は 2 秒ごとに書く）
CV_STALE_S = 8.0
# ただし処理中（busy）と分かっている場合は、hloc の 1 バッチが数十秒かかりうるので猶予を延ばす
CV_BUSY_STALE_S = 90.0
# 同じ resetId がこの秒数以内に来たら「取りこぼし対策の再送」とみなす。
# これを超えて同じ番号が来たら別の試行（Quest 再起動で番号が戻った）として扱う。
RESET_DEDUP_S = 5.0


def heartbeat_age(path, d=None):
    """生存信号の古さ[s]。**ファイルの更新時刻（Windows の時計）で測る。**

    中の "t" は WSL の時計で書かれる。WSL2 はスリープ復帰後などに Windows と時計がずれることがあり
    （数十秒〜分。ノート PC で起きやすい既知の不具合）、"t" で測ると生きている CV を「停止」と誤判定する。
    /mnt/c のファイルは Windows 側が書くので、更新時刻は Windows の時計になる。
    更新時刻が取れないときだけ "t" を使う。
    """
    try:
        return max(0.0, time.time() - os.stat(path).st_mtime)
    except OSError:
        return max(0.0, time.time() - float((d or {}).get("t", 0)))


def read_cv_status(session_dir, base_dir=None):
    """WSL2 の CV ワーカ（localize.watch）が生きているかを、セッションフォルダの
    cv_heartbeat.json の鮮度で判定する。

    run.py だけ起動していて CV が未起動だと、キーフレームは保存されるのに Sim(3) が
    永遠に返らない。外からは「位置合わせが失敗している」ようにしか見えず、実験者は
    原因に気づかないまま試行を消費する。これを Quest 側へ伝えて事前に止められるようにする。

    **セッションにまだハートビートが無いとき**（Quest の RESET 直後）は、CV が前のセッションの
    処理を終えていないだけのことがある（CV は処理を終えるまで次のセッションへ移れない）。
    そのときは base_dir の cv_heartbeat.json（CV が今どのセッションを処理しているか・生きているか）を見て、
    `cv_on` にそのセッション名を入れて返す。**枚数（cv_ok / cv_done 等）は返さない** —
    前のセッションの枚数を、新しいセッションのものと取り違えさせないため（→ heartbeat.py）。
    """
    try:
        p = Path(session_dir) / "cv_heartbeat.json"
        if not p.exists():
            return _read_base_cv_status(base_dir, session_dir)
        d = json.loads(p.read_text(encoding="utf-8"))
        age = heartbeat_age(p, d)
        # 処理中は hloc が数秒〜十数秒ブロックしてハートビートを更新できない。
        # busy が立っていれば「動いてはいる」と分かるので、失効までの猶予を延ばす。
        busy = bool(d.get("busy", False))
        stale = CV_BUSY_STALE_S if busy else CV_STALE_S
        out = {
            "cv_alive": age <= stale,
            "cv_age": round(age, 1),
            "cv_scene": d.get("scene"),
            "cv_window": d.get("window"),
            "cv_ok": int(d.get("total_ok", 0)),
            # 処理し終えた枚数（採否を問わず、解き直しまで済んだ分）。Quest のデモが
            # 「送信を止めてから PC が全部処理し終えたか」を kf と比べて判定する。
            "cv_done": int(d.get("done", 0)),
            "cv_busy": busy,
            "cv_busy_n": int(d.get("busy_n", 0)),
        }
        # 直近の解の採否理由。これを Quest まで運ぶと、「なぜロックしないか」が
        # WSL2 のターミナルを見ずに実機の HUD で分かる。
        if d.get("solve_why") is not None:
            out["cv_pub"] = bool(d.get("solve_published", False))
            out["cv_why"] = str(d.get("solve_why"))[:110]
        return out
    except Exception:
        return {"cv_alive": False, "cv_age": -1.0}


def _read_base_cv_status(base_dir, session_dir):
    """セッションにハートビートが無いときの代わり。CV が生きていて別のセッションを処理中なら
    `cv_on` にそのセッション名を入れて返す。ベースにも無ければ「ハートビート無し」（cv_age=-1）。"""
    none = {"cv_alive": False, "cv_age": -1.0}
    if base_dir is None:
        return none
    try:
        bp = Path(base_dir) / "cv_heartbeat.json"
        if not bp.exists() or bp.resolve() == (Path(session_dir) / "cv_heartbeat.json").resolve():
            return none
        d = json.loads(bp.read_text(encoding="utf-8"))
        age = heartbeat_age(bp, d)
        busy = bool(d.get("busy", False))
        stale = CV_BUSY_STALE_S if busy else CV_STALE_S
        return {
            "cv_alive": age <= stale,
            "cv_age": round(age, 1),
            "cv_scene": d.get("scene"),
            "cv_busy": busy,
            "cv_busy_n": int(d.get("busy_n", 0)),
            "cv_on": str(d.get("session") or ""),   # CV が今処理しているセッション（＝このセッションではない）
        }
    except Exception:
        return none


def build_pong_payload(seq, session, kf_count, cv_status, ev_ack=0, ev_n=0):
    """PONG の JSON を組み立てる（純粋関数・テスト対象）。

    Quest 側 `UDPManager.PongMsg` と鍵名を一致させること。
    ev_ack は「連続して受信済みの実験イベント seq」。Quest はこれを超える分を再送する。
    """
    d = {"seq": int(seq), "session": session or "", "kf": int(kf_count),
         "ev_ack": int(ev_ack), "ev_n": int(ev_n)}
    d.update(cv_status or {})
    return json.dumps(d, ensure_ascii=False).encode("utf-8")


def cp_store_filename(model_id):
    """model 側 CP の JSON ファイル名。

    Unity 側 `ControlPointStore.FileName` と**同じ規則**にすること。名前が食い違うと、
    Editor の「JSON を読み込んで反映」で人がファイルを選び直す羽目になる。
    """
    # C# の char.IsLetterOrDigit は Unicode 対応（日本語のアセット名もそのまま残る）ので、
    # ASCII に限定しないこと。Python の str.isalnum() がほぼ同じ判定になる。
    kept = [c if (c.isalnum() or c in "-_.") else "_"
            for c in (model_id or "default")]
    name = "".join(kept)
    return "controlpoints_" + (name if name else "default") + ".json"


class EventSink:
    """Quest から届く実験イベント（type=7）を保存する。

    **2 か所に書く。**

    1. `<out>/quest_events.jsonl`（トップレベル・正本）
       到着順にすべて追記する。イベントは試行の境目をまたいで流れる（試行終了直後の
       再送など）ので、「今のセッション」に入れる方式では、遅れて届いた行が次の試行の
       フォルダに紛れ込む。まずここに全部落とすことで、取りこぼしも取り違えも起きない。

    2. `<out>/session_*/quest_events.jsonl`（セッション別・複製）
       **到着時刻ではなく、行に載っている試行ラベルで振り分ける。** ラベルは
       `trial_start` で分かり、PC 側の `session_*/trial.json` と 1 対 1 に対応する。
       遅れて届いても正しいフォルダに入るので、1 の懸念は生じない。
       セッションフォルダを開けばその試行のキーフレームと経過が揃うので、
       試行単位で見たいときに扱いやすい。

    正本は 1 のほう。2 は「そのセッションに属すると確定した行」だけなので、
    試行に属さない行（session_start、**CP の測量**など）や、ラベルが未確定のうちに
    届いた行は入らない。測量は実験前に 1 回だけで、どの試行にも属さない（trial=0）ため、
    正本にだけ載るのが正しい。試行との対応は `trial_start.survey` が持つ。

    重複除去は (run, seq)。UDP の再送で同じ行が何度も来るため。
    `ack` は「連続して受け取れた seq」で、これを PONG に載せると Quest は
    穴の開いた分だけを再送できる。CP の測量は実験を通して 6 点しかなく、
    後から測り直すと座標系が変わってやり直しが効かないので、
    ベストエフォート（投げっぱなし）では足りない。
    """

    def __init__(self, path):
        self.path = Path(path)
        self._lock = threading.Lock()
        self._run = None
        self._seen = set()
        self._ack = 0
        self._count = 0
        self._last = None
        self._label_of = {}      # (run, trial) -> label（trial_start で覚える）
        self._dir_of = {}        # label -> session ディレクトリ
        self._cp = {}            # model -> model 側 CP の組み立て中バッファ（下記）

    def _session_dir(self, label):
        """ラベルから session ディレクトリを引く（trial.json を走査。結果はキャッシュ）。"""
        if label in self._dir_of:
            return self._dir_of[label]
        try:
            for d in sorted(self.path.parent.glob("session_*")):
                tj = d / "trial.json"
                if not tj.exists():
                    continue
                try:
                    lbl = json.loads(tj.read_text(encoding="utf-8")).get("label")
                except Exception:
                    continue
                if lbl:
                    self._dir_of[lbl] = d
        except Exception:
            pass
        return self._dir_of.get(label)

    def add(self, seq, payload):
        """1 イベントを取り込む。新規なら True。"""
        try:
            d = json.loads(payload.decode("utf-8"))
        except Exception:
            return False
        run = str(d.get("run", ""))
        with self._lock:
            if run != self._run:
                # アプリを再起動すると seq は 1 から振り直される。run が変われば数え直す。
                self._run, self._seen, self._ack, self._count = run, set(), 0, 0
                self._label_of = {}
                # CP の組み立てバッファも捨てる。前の起動の点が新しい編集結果に混ざると、
                # 「送ったつもりのない点」が JSON に残ってシーンへ戻ってしまう。
                self._cp = {}
            if seq in self._seen:
                return False
            self._seen.add(seq)
            while (self._ack + 1) in self._seen:
                self._ack += 1
            self._count += 1
            d.setdefault("seq", seq)
            if d.get("type") == "trial_start" and d.get("label"):
                self._label_of[(run, d.get("trial"))] = d["label"]
            label = self._label_of.get((run, d.get("trial")))
            cp_model = self._note_cp(d)
            line = json.dumps(d, ensure_ascii=False)
            self._last = d
        if cp_model:
            self._write_cp_json(cp_model)
        try:
            with open(self.path, "a", encoding="utf-8") as f:
                f.write(line + "\n")
        except Exception as e:
            print("[server] イベント書き出し失敗:", e)
            return False

        # セッション別の複製。**到着時刻ではなくラベルで振り分ける**ので、
        # 遅れて届いた行でも正しい試行のフォルダに入る。
        if label:
            sd = self._session_dir(label)
            if sd is not None:
                try:
                    with open(sd / "quest_events.jsonl", "a", encoding="utf-8") as f:
                        f.write(line + "\n")
                except Exception as e:
                    print("[server] セッション別イベント書き出し失敗:", e)
        return True

    @property
    def ack(self):
        with self._lock:
            return self._ack

    @property
    def count(self):
        with self._lock:
            return self._count

    # ---- model 側 CP（VR の中で置き直した結果）を JSON に組み直す ----------------
    #
    # Quest は `cp_model_edit_done`(phase=begin) → **1 点 1 行**の `cp_model_point` →
    # `cp_model_edit_done`(phase=end) の順で送る。1 行にまとめないのは UDP イベントの
    # 1100 B 制限のため（測量の cp_survey と同じ流儀）。
    #
    # ここで Unity の `ControlPointStore` と**同じ形**の JSON に戻すと、Editor の
    # Control Points ウィンドウの「JSON を読み込んで反映」でそのままシーンへ書き戻せる。
    # **adb pull が要らない**のがこの経路の値打ち（実機の控えは冗長化として別にある）。

    def _note_cp(self, d):
        """model 側 CP のイベントを溜める。書き出すべきなら model 名を返す。呼び出しはロック内。"""
        t = d.get("type")
        if t not in ("cp_model_point", "cp_model_edit_done"):
            return None
        model = str(d.get("model") or "default")
        st = self._cp.setdefault(model, {"pts": {}, "n": 0, "edited": 0, "done": False})

        if t == "cp_model_point":
            p = d.get("p")
            if not (isinstance(p, list) and len(p) >= 3):
                return None
            st["pts"][int(d.get("i", len(st["pts"])))] = {
                "name": str(d.get("name") or ""),
                "p": [float(p[0]), float(p[1]), float(p[2])],
            }
            # 締めが先に届いていたら（欠けた点が再送で後から埋まる）そのつど書き直す。
            # 書き直さないと、取りこぼした点だけ古い座標のまま JSON に残る。
            return model if st["done"] else None

        st["n"] = max(st["n"], int(d.get("points") or 0))
        st["edited"] = max(st["edited"], int(d.get("edited") or 0))
        if d.get("phase") == "end":
            st["done"] = True
            return model
        return None

    def _write_cp_json(self, model):
        """received/controlpoints_<model>.json を ControlPointStore と同じ形で書く。"""
        with self._lock:
            st = self._cp.get(model)
            if not st or not st["pts"]:
                return None
            have = sorted(st["pts"])
            pts = [st["pts"][i] for i in have]
            expected, edited = st["n"], st["edited"]

        out = {"modelId": model,
               "savedUtc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ"),
               "points": pts}
        path = self.path.parent / cp_store_filename(model)
        try:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(json.dumps(out, ensure_ascii=False, indent=2), encoding="utf-8")
        except Exception as e:
            print("[server] CP JSON 書き出し失敗:", e)
            return None

        # 欠けたまま黙って書くと、Editor に取り込んだとき「その点だけ古い」状態になる。
        # UDP の再送で後から埋まればもう一度書き直されるので、警告だけ出して待つ。
        if expected and len(pts) < expected:
            missing = [i for i in range(expected) if i not in set(have)]
            print(f"[server] CP JSON: {len(pts)}/{expected} 点しか届いていません "
                  f"(欠け: {missing}) — 再送を待っています: {path}")
        else:
            print(f"[server] CP JSON を書きました（{len(pts)} 点 / うち編集 {edited}）: {path}")
        return path

    def describe(self, d):
        """GUI/コンソール用の 1 行説明。"""
        t = d.get("type", "?")
        # CP の測量は実験前に 1 回だけ（試行ごとではない）。survey 番号で試行と対応づく。
        if t == "cp_survey":
            return f"CP 測量 {d.get('name')} (survey #{d.get('survey')})"
        if t == "cp_survey_start":
            return f"CP 測量 開始 ({d.get('points')} 点)"
        if t == "cp_survey_done":
            return (f"CP 測量 #{d.get('survey')} 完了 "
                    f"({d.get('points')} 点 / 広がり {d.get('span_m')} m)")
        if t == "cp_survey_cancel":
            return f"CP 測量 中断 ({d.get('done')} 点まで)"
        if t == "cp_survey_invalidated":
            return f"CP 測量 #{d.get('survey')} が無効化: {d.get('why')}"
        # model 側 CP の VR 内編集（位置合わせ前の準備。測量とは別物）
        if t == "cp_model_edit":
            a = d.get("action")
            if a == "start":
                return f"model CP 編集 開始 ({d.get('points')} 点)"
            if a == "end":
                return "model CP 編集 終了（変更なし）"
            return (f"model CP {d.get('name')} を置き直し "
                    f"(ビルド値から {float(d.get('moved_m') or 0) * 100:.1f} cm)")
        if t == "cp_model_edit_revert":
            return f"model CP {d.get('name')} をビルド値へ戻した"
        if t == "cp_model_point":
            return f"model CP 座標 {d.get('name')}"
        if t == "cp_model_edit_done":
            if d.get("phase") == "begin":
                return f"model CP 編集 完了 → 受信中 ({d.get('points')} 点)"
            return (f"model CP 編集 完了: {d.get('edited')}/{d.get('points')} 点を変更 → "
                    f"{cp_store_filename(str(d.get('model') or 'default'))}")
        if t == "trial_start":
            return f"試行開始 {d.get('label')}"
        if t == "trial_end":
            return f"試行終了 {d.get('label') or d.get('trial')}: {d.get('verdict')}"
        if t == "tracking_origin_changed":
            return f"トラッキング原点が移動 ({d.get('cause')})"
        return str(t)


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


STATE = ServerState()


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
            self._state.event("RESET 受信（--flat のため無視）")
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
        self._state.event(f"RESET → 新セッション {self._state.session}（trial={label}）")
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
                 sessions: "SessionManager" = None, fixed_file=None):
        super().__init__(daemon=True)
        self.rx = rx
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
        STATE.on_result_sent(payload, addr[0], self.result_port)

    def stop(self):
        self._stop.set()


def udp_loop(rx: UdpReceiver, asm: Reassembler, sessions: SessionManager, result_port: int = 50002,
             events: "EventSink" = None):
    """キーフレーム受信ループ。rx.close() で抜ける。"""
    last_reset_id = None
    last_reset_t = 0.0
    pong_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    last_ping_log = 0.0
    try:
        for p in rx.packets():
            if p.type == TYPE_EVENT:
                # 実験イベント（CP 実測など）。ACK は次の PONG で返す。
                if events is not None and events.add(p.req, p.payload):
                    d = events._last
                    print(f"[server] event #{p.req}: {events.describe(d)}")
                    STATE.event("記録: " + events.describe(d))
                continue

            if p.type == TYPE_PING:
                # 疎通確認。Quest は「送信先 IP を知っている」だけでは疎通と見なさないので、
                # ここで必ず即応答する。ついでに CV ワーカの生存も返す。
                try:
                    payload = build_pong_payload(
                        p.req, STATE.session, STATE.keyframe_count,
                        read_cv_status(sessions.dir, sessions.base),
                        ev_ack=(events.ack if events else 0),
                        ev_n=(events.count if events else 0))
                    addr = rx.last_addr
                    if addr is not None:
                        pong_sock.sendto(build(TYPE_PONG, payload, req=p.req), (addr[0], result_port))
                        now = time.time()
                        if now - last_ping_log > 30.0:      # ログを埋めないよう間引く
                            last_ping_log = now
                            print(f"[server] PING/PONG 応答中（Quest {addr[0]}）")
                except Exception as e:
                    print("[server] PONG 送信エラー:", e)
                continue

            if p.type == TYPE_RESET:
                # 試行リセット（Quest→PC）。再組立には渡さず、セッションを切り替える。
                #
                # Quest は取りこぼし対策で同じ resetId を 3 回続けて送るので重複を無視するが、
                # **番号の一致だけで判定してはいけない**。Quest 側の番号はアプリ再起動で
                # 振り出しに戻りうるため、次の試行の RESET を「前に見た番号」と誤認して
                # 無視してしまい、セッションが切り替わらずキーフレームが前の試行のファイルを
                # 上書きする（実機で発生）。**同じ番号かつ数秒以内**のときだけ重複とみなす。
                now = time.time()
                if p.req == last_reset_id and (now - last_reset_t) < RESET_DEDUP_S:
                    continue
                last_reset_id, last_reset_t = p.req, now
                try:
                    info = json.loads(p.payload.decode("utf-8")) if p.payload else {}
                except Exception:
                    info = {}
                sessions.roll(info)
                asm.clear()       # 前試行の未完チャンクを持ち越さない
                continue

            kf = asm.add(p)
            if kf is None:
                continue
            base = save_keyframe(kf, sessions.dir)
            m = kf.meta
            ip = rx.last_addr[0] if rx.last_addr else "?"
            STATE.on_keyframe(kf.req, ip)
            print(f"[server] keyframe req={kf.req} {m.get('w')}x{m.get('h')} "
                  f"pos=({m.get('px')},{m.get('py')},{m.get('pz')})  -> {base}.jpg")
    except OSError:
        pass  # ソケットが閉じられた（シャットダウン）


def list_built_scenes(scene_root="scene_data"):
    """scene_data/ 配下でビルド済み（hloc/sfm_superpoint+lightglue がある）シーンの一覧。"""
    root = Path(scene_root)
    if not root.is_dir():
        return []
    # WSL2(Linux) の localize が読むので、パスは forward slash（as_posix）で統一する。
    return [d.as_posix() for d in sorted(root.iterdir())
            if d.is_dir() and (d / "hloc" / "sfm_superpoint+lightglue").is_dir()]


def autodetect_scene(scene_root="scene_data"):
    """ビルド済みシーンが1つだけなら返す（曖昧なら None）。"""
    b = list_built_scenes(scene_root)
    return b[0] if len(b) == 1 else None


def write_cv_config(cv_path, cfg):
    """cv.json を一時ファイル経由でアトミックに書き出す（WSL2 の localize がライブ反映）。"""
    from vptserver.cv.gates import DEFAULT_GATES
    tmp = str(cv_path) + ".tmp"
    out = {"scene": cfg.get("scene"), "every": int(cfg.get("every", 2)),
           "min": int(cfg.get("min", 5)), "window": int(cfg.get("window", 24))}
    # 品質ゲート（P1）のしきい値。GUI 未指定なら既定を書いておく（WSL2 の watch がライブ反映）。
    for k, dv in DEFAULT_GATES.items():
        out[k] = cfg.get(k, dv)
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(out, f, indent=2)
    os.replace(tmp, cv_path)


def _prepare_process():
    """どこから起動しても同じに動くようにする（初めての環境・ダブルクリック起動のため）。

    - 作業ディレクトリを server/ にする。scene_data/ や --out（received 等）は server/ 基準で解決される
      （リポジトリ直下から `python server\\run.py` と起動しても、シーンが見つからない事故にならない）。
    - 標準出力・標準エラーで表示できない文字を「?」に置き換える。日本語のログを、コード ページの違う
      コンソールやリダイレクト先へ出しても UnicodeEncodeError で落ちないように。
    - 1 行ごとに書き出す。ファイルへリダイレクトしたとき、強制終了で直前のログが消えないように。
    """
    os.chdir(Path(__file__).resolve().parent)
    for s in (sys.stdout, sys.stderr):
        try:
            s.reconfigure(errors="replace", line_buffering=True)
        except Exception:
            pass


def main():
    _prepare_process()
    ap = argparse.ArgumentParser(description="VPTAligned3DGS offload server (Windows UDP + GUI)")
    ap.add_argument("--port", type=int, default=50000, help="キーフレーム受信ポート")
    ap.add_argument("--out", default="received", help="保存先のベースディレクトリ")
    ap.add_argument("--flat", action="store_true",
                    help="セッションフォルダを作らず --out 直下に保存（旧挙動）")
    ap.add_argument("--beacon-port", type=int, default=DEFAULT_BEACON_PORT)
    ap.add_argument("--no-beacon", action="store_true")
    ap.add_argument("--result-port", type=int, default=50002, help="Quest へ Sim(3) を返送するポート")
    ap.add_argument("--result-file", default=None, help="監視する Sim(3) JSON（既定: <out>/result_sim3.json）")
    ap.add_argument("--no-result", action="store_true")
    ap.add_argument("--no-gui", action="store_true", help="GUIウインドウを出さずヘッドレス実行")
    ap.add_argument("--verbose", action="store_true")
    # ↓ WSL2 の CV へ渡す設定（セッションフォルダの cv.json に書き出す）。WSL2 は引数なしで起動可。
    ap.add_argument("--scene", default=None, help="DBシーン（省略時 scene_data から自動検出）。cv.json に記録")
    ap.add_argument("--every", type=int, default=2,
                help="CV: 何枚ごとに Sim(3) 再計算（cv.json に記録）。連続一致の機会を増やすため小さめ")
    ap.add_argument("--min", type=int, default=5,
                help="CV: 初回 Sim(3) の最小枚数（cv.json に記録）。5未満だと RANSAC が回らない")
    ap.add_argument("--expected-scale", type=float, default=None,
                help="このシーンの既知スケール（例 0.96）。指定するとそこから外れた解を棄却する。"
                     "COLMAP の任意単位と実メートルの比は再構成に固有の固定値なので、"
                     "一度分かれば最も安価で強力なゲートになる。未指定なら無効")
    ap.add_argument("--scale-range", type=float, nargs=2, metavar=("LO", "HI"), default=None,
                    help="|scale| の健全性レンジ（既定 1e-3〜1e3）。ソルバの発散を落とすだけの"
                         "シーン非依存チェックなので、通常は触らない。**シーンの実スケール付近に"
                         "締めないこと** — COLMAP の単位はシーン固有で、締めると正しい解が全滅する")
    # ↓ WSL2 の CV（localize）の自動起動（→ vptserver/cv_launcher.py）
    ap.add_argument("--no-cv", action="store_true",
                    help="WSL2 の localize を自動起動しない（従来どおり WSL2 のターミナルで手動起動する）")
    ap.add_argument("--wsl-distro", default=None,
                    help="CV 環境のある WSL ディストリ（省略時: 前回の設定 → CV 環境のあるものを自動で探す）")
    ap.add_argument("--cv-venv", default=None,
                    help="WSL 側の CV 環境（venv）の場所（省略時: 前回の設定 → ~/vptcv）")
    args = ap.parse_args()

    # セッションフォルダ: 起動ごとに received/session_<timestamp>/ を作り、そこへ保存する。
    # → 毎回自動でセッション分離（手動クリア不要・別セッションの座標系混入や reqId 衝突を回避）。
    #   Quest からの RESET（type=4）でも切り替わる（SessionManager）。
    os.makedirs(args.out, exist_ok=True)

    # WSL2 の CV 設定をセッションフォルダの cv.json に書き出す（GUI から変更すると即時更新→localize がライブ反映）。
    cv_scenes = list_built_scenes()
    cv_cfg = {"scene": args.scene or autodetect_scene(), "every": args.every, "min": args.min}
    if args.expected_scale is not None:
        cv_cfg["expected_scale"] = abs(args.expected_scale)
        print(f"[server] 期待スケール {cv_cfg['expected_scale']:.3f} でゲートします")
    if args.scale_range is not None:
        lo, hi = sorted(abs(v) for v in args.scale_range)
        cv_cfg["scale_range"] = [lo, hi]
        print(f"[server] |scale| の健全性レンジを [{lo:g}, {hi:g}] にします")

    def write_cv_for(session_dir):
        """指定セッションの cv.json を現在の設定で書く（新セッション作成時にも呼ばれる）。"""
        try:
            write_cv_config(os.path.join(session_dir, "cv.json"), cv_cfg)
        except Exception as e:
            print("[server] cv.json 書き出し失敗:", e)

    sessions = SessionManager(args.out, args.flat, write_cv_for, STATE)
    # 実験イベント（CP 実測など）は 1 本の JSONL に貯める。試行の境目をまたぐので
    # セッション別には置かない（再送分が次のセッションへ紛れ込む）。
    events = EventSink(os.path.join(args.out, "quest_events.jsonl"))

    # WSL2 の CV（localize）を自動で起動・終了する（→ vptserver/cv_launcher.py）。
    # localize は起動時にシーンが決まっていないと終了するので、シーンが決まってから起動する
    # （複数のシーンがあって未指定なら、GUI でシーンを選んだ時点で起動する）。
    cv = None
    if not args.no_cv:
        from vptserver.cv_launcher import CvLauncher
        cv = CvLauncher(Path.cwd(), args.out, distro=args.wsl_distro, venv=args.cv_venv,
                        emit=print, on_event=STATE.event)

    def start_cv_if_ready():
        if cv is None or cv_cfg.get("scene") is None:
            return
        cv.start()

    def update_cv(cfg):
        cv_cfg.update({"scene": cfg.get("scene"),
                       "every": int(cfg.get("every", cv_cfg["every"])),
                       "min": int(cfg.get("min", cv_cfg["min"]))})
        if cfg.get("expected_scale") is not None:
            cv_cfg["expected_scale"] = abs(float(cfg["expected_scale"]))
        write_cv_for(sessions.dir)
        STATE.event(f"cv.json 更新: scene={cv_cfg['scene']} every={cv_cfg['every']} min={cv_cfg['min']}")
        start_cv_if_ready()                 # シーンを選んだ時点で CV を起動（起動済みなら何もしない）

    session_dir = sessions.start()          # 最初のセッション作成＋cv.json 書き出し
    print(f"[server] cv.json: {cv_cfg}")
    if cv is None:
        print("[server] CV の自動起動は OFF（--no-cv）。WSL2 で localize を手動で起動してください")
    elif cv_cfg.get("scene") is None:
        msg = (f"シーンが決まっていないため、CV はまだ起動しません（候補: {', '.join(cv_scenes) or 'なし'}）。"
               "GUI でシーンを選ぶか、--scene scene_data/<name> を付けて起動してください")
        print("[server] " + msg)
        STATE.event(msg)
    else:
        start_cv_if_ready()

    rx = UdpReceiver(args.port, verbose=args.verbose)
    asm = Reassembler()
    STATE.ports = {"data": args.port, "beacon": (None if args.no_beacon else args.beacon_port),
                   "result": (None if args.no_result else args.result_port)}
    STATE.beacon_on = not args.no_beacon

    beacon = None
    if not args.no_beacon:
        beacon = Beacon(data_port=args.port, beacon_port=args.beacon_port)
        beacon.start()

    sender = None
    if not args.no_result:
        sender = ResultSender(rx, args.result_port, sessions=sessions, fixed_file=args.result_file)
        sender.start()
    result_desc = args.result_file or "<session>/result_sim3.json"

    print(f"[server] listening UDP :{args.port} -> ./{session_dir}/")
    print(f"[server] beacon {'OFF' if args.no_beacon else f':{args.beacon_port}'}, "
          f"result {'OFF' if args.no_result else f'{result_desc} -> Quest:{args.result_port}'}")
    print("[server] Quest から RESET(type=4) を受けると新しいセッションへ自動で切り替えます（実験の試行リセット）")

    def cleanup():
        if cv is not None:
            cv.stop()                       # localize も止める（標準入力を閉じる → 終わらなければ kill）
        if sender is not None:
            sender.stop()
        if beacon is not None:
            beacon.stop()
        rx.close()
        print("[server] bye")

    use_gui = (not args.no_gui) and gui_mod.available()
    if (not args.no_gui) and not use_gui:
        print("[server] customtkinter 未導入のためヘッドレス実行（GUI を使うには: pip install customtkinter）")

    if use_gui:
        # GUI はメインスレッド、UDP 受信は別スレッド
        threading.Thread(target=udp_loop, args=(rx, asm, sessions, args.result_port, events), daemon=True).start()
        print("[server] GUI ウインドウを表示中（閉じると終了）")
        try:
            gui_mod.run_dashboard(STATE, on_close=cleanup,
                                  config=cv_cfg, scenes=cv_scenes, on_config=update_cv)
        except KeyboardInterrupt:
            cleanup()
    else:
        print("[server] Ctrl-C to stop")
        try:
            udp_loop(rx, asm, sessions, args.result_port, events)
        except KeyboardInterrupt:
            print("\n[server] stopping...")
        finally:
            cleanup()


if __name__ == "__main__":
    main()
