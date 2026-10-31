"""疎通確認（PING/PONG）の中身: CV ワーカの生存信号を読み、PONG の JSON を組み立てる。

Quest は PONG で「run.py が応答するか」と「WSL2 の CV（localize.watch）が生きているか」を知る。
CV の生存信号（cv_heartbeat.json）は localize が別スレッドで書く（→ cv/heartbeat.py）。
"""
import json
import os
import time
from pathlib import Path

from .cv.heartbeat import HEARTBEAT_NAME

# CV ハートビートがこの秒数より古ければ「CV 未稼働」とみなす（localize は 2 秒ごとに書く）
CV_STALE_S = 8.0
# ただし処理中（busy）と分かっている場合は、hloc の 1 バッチが数十秒かかりうるので猶予を延ばす
CV_BUSY_STALE_S = 90.0


def heartbeat_file_age(path, now=None):
    """生存信号ファイルの古さ[s]（ファイルが無ければ None）。**ファイルの更新時刻（Windows の時計）で測る。**

    中の "t" は WSL の時計で書かれる。WSL2 はスリープ復帰後などに Windows と時計がずれることがあり
    （数十秒〜分。ノート PC で起きやすい既知の不具合）、"t" で測ると生きている CV を「停止」と誤判定する。
    /mnt/c のファイルは Windows 側が書くので、更新時刻は Windows の時計になる。
    PONG の中身（下）と、run.py が localize の二重起動を避ける判定（cv_launcher）の両方が使う。
    """
    try:
        mtime = os.stat(path).st_mtime
    except OSError:
        return None
    return max(0.0, (time.time() if now is None else now) - mtime)


def heartbeat_age(path, d=None):
    """生存信号の古さ[s]。更新時刻が取れないときだけ、中の "t"（WSL の時計）で代わりに測る。"""
    age = heartbeat_file_age(path)
    return age if age is not None else max(0.0, time.time() - float((d or {}).get("t", 0)))


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
        p = Path(session_dir) / HEARTBEAT_NAME
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
        bp = Path(base_dir) / HEARTBEAT_NAME
        if not bp.exists() or bp.resolve() == (Path(session_dir) / HEARTBEAT_NAME).resolve():
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
