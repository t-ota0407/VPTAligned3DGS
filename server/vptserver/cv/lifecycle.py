"""localize の寿命を起動元（Windows の run.py）に連動させる。標準ライブラリだけで動く（テスト可能）。

run.py は WSL2 の localize を子プロセスとして起動し、**標準入力をパイプでつないだまま何も送らない**。
run.py が終了すると（GUI を閉じた・Ctrl+C・異常終了のどれでも）パイプが閉じ、localize の標準入力が
EOF になる。それを見て localize も終了する。

wsl.exe を止めるだけでは、Linux 側の Python が残ることがある。そのため「親が終わったら子も終わる」を
子の側で保証する。重い処理（hloc）の途中ならすぐには抜けられないので、猶予を過ぎたら強制終了する。

**標準入力は `sys.stdin` ではなくファイル記述子を直接読む**（`_FdReader`）。`sys.stdin.buffer.read()` で
待つと、その間ずっと BufferedReader のロックを握る。hloc の DataLoader はワーカを fork し、multiprocessing は
子の起動時に `sys.stdin.close()` を呼ぶ（`multiprocessing.util._close_stdin`）。子は握られたままのロックを
受け継ぐが、握っていたスレッドは子にはいないので close() が永遠に返らず、親は最初の画像を待ち続ける
（キーフレームが 1 枚も処理されない。2026-11-08 にデモで発生し、WSL で再現・修正を確認）。
"""
import os
import sys
import threading


class _FdReader:
    """ファイル記述子を os.read で直接読む（Python のバッファ付きストリームのロックを握らない）。"""

    def __init__(self, fd):
        self.fd = fd

    def read(self, n):
        return os.read(self.fd, n)


def watch_stream_eof(stream, on_eof):
    """stream が EOF になったら on_eof() を呼ぶ見張りスレッドを起動して返す。

    stream には何も送られてこない前提（読んだ分は捨てる）。読み込みで例外が出ても EOF とみなす。
    """
    def run():
        try:
            while True:
                chunk = stream.read(1)
                if not chunk:
                    break
        except Exception:
            pass
        on_eof()

    t = threading.Thread(target=run, name="parent-watch", daemon=True)
    t.start()
    return t


def exit_when_parent_gone(stop_event, log, grace_s=10.0, stream=None, hard_exit=os._exit):
    """標準入力が EOF になったら stop_event を立て、grace_s 秒たっても終わらなければ強制終了する。

    hard_exit はテストで差し替えるためのもの。stream を省くと標準入力の記述子を直接読む（モジュールの説明）。
    """
    stream = stream if stream is not None else _FdReader(sys.stdin.fileno())

    def on_eof():
        log.info("起動元（run.py）が終了したので、localize も終了します")
        stop_event.set()
        timer = threading.Timer(grace_s, lambda: hard_exit(0))
        timer.daemon = True     # 正常に抜けられたら待たずに終わる
        timer.start()

    return watch_stream_eof(stream, on_eof)
