#!/usr/bin/env python3
"""疎通テスト用。Quest を使わず、正しい形式のダミーキーフレームを 1 件送る。

使い方:
    # 同じ PC 内（サーバとファイアウォールの確認）
    python tools/test_send.py 127.0.0.1

    # 別端末から PC へ（ネットワーク経路の確認）。PC の IP を指定
    python tools/test_send.py 192.168.1.23

    # ブロードキャスト（Quest と同じ挙動の確認）
    python tools/test_send.py 255.255.255.255

run.py を別ターミナルで起動した状態で実行し、
サーバ側に "keyframe req=999 ..." が出れば経路は OK。
"""
import socket
import struct
import sys
import json

MAGIC = b"VP"
TYPE_META = 1
TYPE_IMG = 2
PORT = 50000


def header(t, req, idx, total, payload):
    return (MAGIC + bytes([t]) + struct.pack("<I", req) +
            struct.pack("<H", idx) + struct.pack("<H", total) +
            struct.pack("<H", len(payload)) + payload)


def main():
    host = sys.argv[1] if len(sys.argv) > 1 else "127.0.0.1"
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)

    jpg = b"\xff\xd8" + b"TESTKEYFRAME" * 50 + b"\xff\xd9"
    chunk = 256
    chunks = [jpg[i:i + chunk] for i in range(0, len(jpg), chunk)]
    meta = {"w": 512, "h": 384, "sw": 1280, "sh": 960,
            "fx": 600.0, "fy": 600.0, "cx": 640.0, "cy": 480.0,
            "px": 0.0, "py": 1.0, "pz": 0.0,
            "qx": 0.0, "qy": 0.0, "qz": 0.0, "qw": 1.0,
            "ts": "test", "n": len(chunks), "len": len(jpg)}

    s.sendto(header(TYPE_META, 999, 0, 1, json.dumps(meta).encode()), (host, PORT))
    for i, c in enumerate(chunks):
        s.sendto(header(TYPE_IMG, 999, i, len(chunks), c), (host, PORT))
    s.close()
    print(f"sent dummy keyframe req=999 ({len(jpg)}B / {len(chunks)} chunks) -> {host}:{PORT}")


if __name__ == "__main__":
    main()
