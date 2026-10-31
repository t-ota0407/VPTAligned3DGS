"""localize の寿命を起動元（Windows の run.py）に連動させる。標準ライブラリだけで動く（テスト可能）。

run.py は WSL2 の localize を子プロセスとして起動し、**標準入力をパイプでつないだまま何も送らない**。
run.py が終了すると（GUI を閉じた・Ctrl+C・異常終了のどれでも）パイプが閉じ、localize の標準入力が
EOF になる。それを見て localize も終了する。

wsl.exe を止めるだけでは、Linux 側の Python が残ることがある。そのため「親が終わったら子も終わる」を
子の側で保証する。重い処理（hloc）の途中ならすぐには抜けられないので、猶予を過ぎたら強制終了する。
"""
import os
import sys
import threading


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

    hard_exit はテストで差し替えるためのもの。
    """
    stream = stream if stream is not None else sys.stdin.buffer

    def on_eof():
        log.info("起動元（run.py）が終了したので、localize も終了します")
        stop_event.set()
        timer = threading.Timer(grace_s, lambda: hard_exit(0))
        timer.daemon = True     # 正常に抜けられたら待たずに終わる
        timer.start()

    return watch_stream_eof(stream, on_eof)
