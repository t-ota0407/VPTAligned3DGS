"""サーバ存在通知ビーコン。

PC の存在を LAN にブロードキャストし、Quest 側が送信先 IP を自動学習できるようにする。
ペイロード: b"VPTSRV1" + uint16(データ受信ポート, LE)
Quest は受信パケットの送信元 IP を「PC の IP」として採用し、そこへユニキャストでキーフレームを送る。
"""
import socket
import struct
import threading

BEACON_MAGIC = b"VPTSRV1"
DEFAULT_BEACON_PORT = 50001


def _local_ipv4s():
    """このマシンの IPv4 アドレス一覧（ループバック除く）。"""
    ips = set()
    try:
        # 既定経路に出ていく NIC の IP（実際の送信はしない）
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(("8.8.8.8", 80))
        ips.add(s.getsockname()[0])
        s.close()
    except Exception:
        pass
    try:
        for info in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET):
            ips.add(info[4][0])
    except Exception:
        pass
    ips.discard("127.0.0.1")
    return ips


def _directed_broadcasts(ips):
    """各 IPv4 から /24 想定の指向性ブロードキャストアドレスを作る（best-effort）。"""
    outs = set()
    for ip in ips:
        parts = ip.split(".")
        if len(parts) == 4:
            outs.add(".".join(parts[:3] + ["255"]))
    return outs


class Beacon:
    def __init__(self, data_port: int = 50000,
                 beacon_port: int = DEFAULT_BEACON_PORT, interval: float = 1.0):
        self.payload = BEACON_MAGIC + struct.pack("<H", data_port)
        self.beacon_port = beacon_port
        self.interval = interval
        self._stop = threading.Event()
        self._thread = None

    def start(self):
        self._thread = threading.Thread(target=self._loop, daemon=True)
        self._thread.start()

    def _loop(self):
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        while not self._stop.is_set():
            # 255.255.255.255 と、各 NIC の /24 指向性ブロードキャストの両方へ
            targets = {"255.255.255.255"} | _directed_broadcasts(_local_ipv4s())
            for t in targets:
                try:
                    s.sendto(self.payload, (t, self.beacon_port))
                except Exception:
                    pass
            self._stop.wait(self.interval)
        s.close()

    def stop(self):
        self._stop.set()
        if self._thread:
            self._thread.join(timeout=2)
