"""データポート（UDP 50000）に届くパケットの振り分け。"""
import json
import socket
import time

from .events import EventSink
from .liveness import build_pong_payload, read_cv_status
from .protocol import TYPE_EVENT, TYPE_PING, TYPE_PONG, TYPE_RESET, build
from .reassembler import Reassembler
from .session import SessionManager
from .state import ServerState
from .storage import save_keyframe
from .udp_receiver import UdpReceiver

# 実験イベントのうち、GUI のイベント欄で目立たせるもの（青: 試行の区切り / 琥珀: 測定が無効になりうる）
EVENT_LEVELS = {"trial_start": "mark", "trial_end": "mark",
                "tracking_origin_changed": "warn", "cp_survey_invalidated": "warn"}

# 同じ resetId がこの秒数以内に来たら「取りこぼし対策の再送」とみなす。
# これを超えて同じ番号が来たら別の試行（Quest 再起動で番号が戻った）として扱う。
RESET_DEDUP_S = 5.0


def udp_loop(rx: UdpReceiver, asm: Reassembler, sessions: SessionManager, result_port: int = 50002,
             events: EventSink = None, state: ServerState = None):
    """キーフレーム受信ループ。rx.close() で抜ける。

    届いたパケットを種類で振り分ける: 実験イベント（type=7）→ events / PING → PONG を即返す /
    RESET → 新しいセッションへ / それ以外はキーフレームのチャンクとして再組立てして保存。
    """
    state = state if state is not None else ServerState()
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
                    state.event("記録: " + events.describe(d),
                                en="Recorded: " + events.describe(d, lang="en"),
                                level=EVENT_LEVELS.get((d or {}).get("type"), "info"))
                continue

            if p.type == TYPE_PING:
                # 疎通確認。Quest は「送信先 IP を知っている」だけでは疎通と見なさないので、
                # ここで必ず即応答する。ついでに CV ワーカの生存も返す。
                try:
                    payload = build_pong_payload(
                        p.req, state.session, state.keyframe_count,
                        read_cv_status(sessions.dir, sessions.base),
                        ev_ack=(events.ack if events else 0),
                        ev_n=(events.count if events else 0))
                    addr = rx.last_addr
                    if addr is not None:
                        state.on_ping(addr[0])      # GUI の Quest の行（キーフレームが無い間の生存）
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
            state.on_keyframe(kf.req, ip)
            print(f"[server] keyframe req={kf.req} {m.get('w')}x{m.get('h')} "
                  f"pos=({m.get('px')},{m.get('py')},{m.get('pz')})  -> {base}.jpg")
    except OSError:
        pass  # ソケットが閉じられた（シャットダウン）
