"""UDP パケットの定義と解析（Quest 側 VpPacket.cs と一致させること。type 定数は tests/test_liveness.py が突き合わせる）。

パケット: 13B ヘッダ + ペイロード
  [ 'V','P', type(1), reqId(4 LE), idx(2 LE), total(2 LE), len(2 LE) ] + payload[len]
  type=1: META（UTF-8 JSON）/ type=2: IMG チャンク（JPEG の一部）
  type=3: RESULT（PC→Quest）/ type=4: RESET（Quest→PC・試行リセット）
  type=5: PING（Quest→PC）/ type=6: PONG（PC→Quest）
  type=7: EVENT（Quest→PC・実験ログの 1 行。ACK 付き再送あり）
"""
import struct
from typing import NamedTuple, Optional

MAGIC = b"VP"
HEADER_SIZE = 13
TYPE_META = 1
TYPE_IMG = 2
TYPE_RESULT = 3   # PC→Quest: Sim(3) 結果（UTF-8 JSON: sigma,qx,qy,qz,qw,px,py,pz,...）
# Quest→PC: 試行リセット（UTF-8 JSON: trial, label, note）。run.py は新しい session_* を切り、
# WSL2 の localize.watch は最新セッション追従で Cc/Cw バッファを自動リセットする。
# 実験（E1 の独立試行 / E2 のコールドスタート）を run.py 再起動なしで回すために使う。
TYPE_RESET = 4

# 疎通確認（ハートビート）。Quest→PC の PING に PC が PONG で即応答する。
#
# なぜ必要か: Quest 側が「送信先 IP を知っている」ことと「サーバが動いている」ことは別物。
# IP は前回起動時の学習値が PlayerPrefs に残るし、手動設定もできるため、run.py を起動して
# いなくても送信先は埋まる。実際に往復を確認しないと疎通判定にならない。
# PONG には CV ワーカ（WSL2 の localize.watch）の生存も載せる。run.py だけ動いていて CV が
# 未起動だと、キーフレームは届くのに Sim(3) が永遠に返らない、という失敗の仕方をするため。
TYPE_PING = 5   # Quest→PC（データポート）。reqId に seq。payload は任意の JSON。
TYPE_PONG = 6   # PC→Quest（結果ポート）。reqId に PING の seq をそのまま返す。

# 実験イベント（Quest→PC・データポート）。reqId に通し番号 seq、payload は
# ExperimentLogger の 1 行（UTF-8 JSON）。CP 実測値・試行の開始終了・トラッキング原点の
# 移動など、**解析に必要な行だけ**を送る。
#
# なぜ要るか: これらは従来 Quest 内部の JSONL にしか残らず、取り出すのに adb pull が要った。
# 実験のたびにケーブルを繋いで引き抜くのは手間な上、引き忘れると 10 試行ぶんの
# CP 実測値（測り直しの効かないデータ）が端末の中に取り残される。
#
# 信頼性: UDP なので取りこぼす。PONG に「受信済みの連続 seq」(ev_ack) を載せ、
# Quest はそれを超える未達分を再送する。CP 実測値は測り直しが効かないため、
# ベストエフォートでは足りない。
TYPE_EVENT = 7


def build(type_, payload, req=0, idx=0, total=1):
    """13B ヘッダ + payload のパケットを組み立てる（送信用。Quest 側 VpPacket.Build と同形式）。"""
    import struct
    return (MAGIC + bytes([type_]) + struct.pack("<I", req) + struct.pack("<H", idx) +
            struct.pack("<H", total) + struct.pack("<H", len(payload)) + payload)


class Packet(NamedTuple):
    type: int
    req: int
    idx: int
    total: int
    payload: bytes


def parse(pkt: bytes) -> Optional[Packet]:
    """生バイト列を Packet に解析。不正なら None。"""
    if len(pkt) < HEADER_SIZE or pkt[0:2] != MAGIC:
        return None
    typ = pkt[2]
    req = struct.unpack_from("<I", pkt, 3)[0]
    idx = struct.unpack_from("<H", pkt, 7)[0]
    total = struct.unpack_from("<H", pkt, 9)[0]
    length = struct.unpack_from("<H", pkt, 11)[0]
    payload = pkt[HEADER_SIZE:HEADER_SIZE + length]
    return Packet(typ, req, idx, total, payload)
