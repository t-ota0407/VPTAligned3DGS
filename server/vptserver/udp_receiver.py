"""UDP 受信ループ。受信パケットを Packet に解析して yield する。"""
import socket
from typing import Iterator

from .protocol import parse, Packet


class UdpReceiver:
    def __init__(self, port: int = 50000, bufsize: int = 2048,
                 verbose: bool = False, timeout: float = 0.5):
        self.port = port
        self.bufsize = bufsize
        self.verbose = verbose
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.sock.bind(("", port))  # 全インターフェースで受信（ブロードキャスト含む）
        # ブロッキング recvfrom だと（特に Windows で）Ctrl-C が届かないため、
        # 定期的に制御を返してメインスレッドが KeyboardInterrupt を拾えるようにする。
        self.sock.settimeout(timeout)
        self.last_addr = None  # 直近にキーフレームを送ってきた Quest の (ip, port)

    def packets(self) -> Iterator[Packet]:
        while True:
            try:
                data, addr = self.sock.recvfrom(self.bufsize)
            except socket.timeout:
                continue  # タイムアウト＝この間に Ctrl-C を拾えるようにするための空振り
            self.last_addr = addr
            if self.verbose:
                print(f"[rx] {len(data)}B from {addr[0]}:{addr[1]}")
            p = parse(data)
            if p is not None:
                yield p

    def close(self):
        self.sock.close()
