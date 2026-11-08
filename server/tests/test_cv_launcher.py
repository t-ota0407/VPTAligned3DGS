"""WSL2 の CV（localize）の自動起動（vptserver/cv_launcher.py・cv/lifecycle.py）の純粋な部分の検証。

ここで守りたいこと（**初めての Windows でも動く**ための前提）:
  1. wsl.exe の出力（UTF-16LE / UTF-8）を読める。既定のディストリを言語に依らず見分け、docker 系を除く。
  2. 空白・日本語を含むユーザー名のパスでも、WSL のパスへの変換と bash へのクォートが壊れない。
     ネットワークドライブ（WSL から見えない）は分かる形で弾く。
  3. 「別の localize が動いている」の判定を、WSL の時計ではなくファイルの更新時刻で行う。
  4. 起動元が終わったら localize も終わる（標準入力の EOF）。重い処理の途中なら猶予の後に強制終了。
  5. 起動直後に何度も落ちるなら再起動を諦める（設定の問題を無限ループにしない）。

WSL を実際に起動する部分はここでは試さない（実機で確認する）。
実行: python -m tests.test_cv_launcher
"""
import io
import json
import os
import sys
import tempfile
import threading
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from tests._check import check, finish  # noqa: E402

from vptserver import cv_launcher as L
from vptserver.cv import lifecycle


# 作者の PC の `wsl -l -v` そのまま（既定は 20.04、CV 環境は 24.04 にある）
WSL_LIST = ("  NAME              STATE           VERSION\r\n"
            "* Ubuntu-20.04      Stopped         2\r\n"
            "  Ubuntu-24.04      Stopped         2\r\n"
            "  docker-desktop    Stopped         2\r\n")


def test_decode_and_parse():
    print("== wsl.exe の出力を読む ==")
    utf16 = WSL_LIST.encode("utf-16-le")
    check(L.decode_wsl_output(utf16) == WSL_LIST, "UTF-16LE（BOM なし）を読める")
    check(L.decode_wsl_output(b"\xff\xfe" + utf16) == WSL_LIST, "UTF-16LE（BOM あり）を読める")
    check(L.decode_wsl_output("ログ 山田\n".encode("utf-8")) == "ログ 山田\n", "UTF-8（Linux 側の出力）を読める")
    check(L.decode_wsl_output(b"") == "", "空")

    names, default = L.parse_distro_list(L.decode_wsl_output(utf16))
    check(names == ["Ubuntu-20.04", "Ubuntu-24.04"], f"docker 系を除いた一覧 {names}")
    check(default == "Ubuntu-20.04", "既定（* 付き）を見分ける")
    names, default = L.parse_distro_list("")
    check(names == [] and default is None, "ディストリが無い")

    # 見出し・状態の列が Windows の言語で変わっても、見出しをディストリ名と取り違えない
    localized = ("  名前              状態           バージョン\r\n"
                 "* Ubuntu-24.04      実行中         2\r\n"
                 "  Debian            停止           1\r\n")
    names, default = L.parse_distro_list(localized)
    check(names == ["Ubuntu-24.04", "Debian"] and default == "Ubuntu-24.04",
          f"見出しが日本語でも読める {names}")
    check(L.parse_distro_versions(localized) == {"Ubuntu-24.04": "2", "Debian": "1"},
          "WSL のバージョン（WSL1 は GPU を使えないので見分ける）")


def test_paths_and_quoting():
    print("== パスの変換とクォート（空白・日本語のユーザー名） ==")
    win = r"C:\Users\山田 太郎\OneDrive\デスクトップ\VPT Aligned\server"
    check(L.windows_to_wsl_path(win) == "/mnt/c/Users/山田 太郎/OneDrive/デスクトップ/VPT Aligned/server",
          "C: → /mnt/c（空白・日本語をそのまま保つ）")
    check(L.windows_to_wsl_path(r"D:\data") == "/mnt/d/data", "D: → /mnt/d（小文字）")
    try:
        L.windows_to_wsl_path(r"\\fileserver\share\vpt")
        check(False, "UNC パスは弾く")
    except ValueError:
        check(True, "UNC パス（ネットワークドライブ）は WSL から見えないので分かる形で弾く")

    check(L.bash_path("~/vptcv") == '"$HOME"/vptcv', "~/ は $HOME を展開させる")
    check(L.bash_path("/opt/my env") == "'/opt/my env'", "空白を含むパスはクォート")

    s = L.build_cv_script("/mnt/c/Users/山田 太郎/VPT Aligned/server",
                          "/mnt/c/Users/山田 太郎/VPT Aligned/server/received_demo")
    check("cd '/mnt/c/Users/山田 太郎/VPT Aligned/server'" in s, "cd 先はクォートされる")
    check("--watch '/mnt/c/Users/山田 太郎/VPT Aligned/server/received_demo'" in s,
          "--watch は run.py の --out から（クォート付き）")
    check("exec python -m vptserver.cv.localize" in s and "--exit-on-stdin-eof" in s,
          "exec で起動し、起動元が終わったら終了する")
    check('"$HOME"/vptcv/bin/python' in s and f"exit {L.EXIT_NO_VENV}" in s,
          "venv が無ければ案内を出して専用の終了コードで抜ける")
    fixed = s.replace("/mnt/c/Users/山田 太郎/VPT Aligned/server", "")
    check(all(ord(c) < 128 for c in fixed), "パス以外は ASCII だけ（WSL へ渡すコマンド行が化けない）")


def test_config_and_running_detection():
    print("== 設定の保存と「別の localize が動いている」の判定 ==")
    with tempfile.TemporaryDirectory() as d:
        check(L.load_config(d) == {}, "設定が無ければ空")
        L.save_config(d, {"wsl_distro": "Ubuntu-24.04", "cv_venv": "~/vptcv"})
        check(L.load_config(d) == {"wsl_distro": "Ubuntu-24.04", "cv_venv": "~/vptcv"}, "保存して読み戻せる")
        (Path(d) / L.CONFIG_NAME).write_text("{壊れた", encoding="utf-8")
        check(L.load_config(d) == {}, "壊れた設定は無視（起動を止めない）")

        hb = Path(d) / L.HEARTBEAT_NAME
        check(L.cv_already_running(d) is False, "生存信号が無い → 動いていない")
        # 中の t は WSL の時計（2 分遅れ）でも、ファイルは今書かれた → 動いている
        hb.write_text(json.dumps({"t": time.time() - 120, "session": "s"}))
        check(L.cv_already_running(d) is True, "WSL の時計がずれていても、更新時刻で「動いている」と分かる")
        old = time.time() - 20
        os.utime(hb, (old, old))
        check(L.cv_already_running(d) is False, "20 秒更新されていない → 動いていない（引き継いでよい）")


def test_lifecycle():
    print("== 起動元が終わったら localize も終わる ==")
    called = threading.Event()
    lifecycle.watch_stream_eof(io.BytesIO(b""), called.set)
    check(called.wait(2.0), "標準入力が EOF になったら知らせる")

    class Log:
        def info(self, *a): pass
    stop = threading.Event()
    exited = []
    lifecycle.exit_when_parent_gone(stop, Log(), grace_s=0.2, stream=io.BytesIO(b""),
                                    hard_exit=lambda code: exited.append(code))
    check(stop.wait(2.0), "停止の合図（stop_event）が立つ")
    time.sleep(0.6)
    check(exited == [0], "猶予を過ぎても終わらなければ強制終了する（重い処理の途中でも残らない）")

    # 見張りが標準入力で待っている間も sys.stdin を閉じられること。hloc の DataLoader が fork した子は
    # 起動時に sys.stdin.close() を呼ぶので、見張りが sys.stdin のロックを握っていると子が永遠に止まる
    # （キーフレームが 1 枚も処理されない。lifecycle.py の説明）
    r, w = os.pipe()
    saved = sys.stdin
    sys.stdin = io.TextIOWrapper(io.BufferedReader(io.FileIO(r, closefd=False)))
    try:
        stop2 = threading.Event()
        watcher = lifecycle.exit_when_parent_gone(stop2, Log(), grace_s=60, hard_exit=lambda code: None)
        time.sleep(0.3)                                    # 見張りが読み込みで待つまで
        closer = threading.Thread(target=sys.stdin.close, daemon=True)
        closer.start()
        closer.join(2.0)
        check(not closer.is_alive(), "見張りが待っている間も sys.stdin を閉じられる（fork した子が詰まらない）")
        os.close(w)                                        # 起動元が終わった
        watcher.join(2.0)
        check(stop2.is_set(), "標準入力の記述子を直接読んでも、EOF で停止の合図が立つ")
    finally:
        sys.stdin = saved
        os.close(r)


def test_restart_policy():
    print("== 再起動の方針 ==")

    class FakeProc:
        def poll(self): return 0

    with tempfile.TemporaryDirectory() as d:
        msgs = []
        cv = L.CvLauncher(d, Path(d) / "received", emit=msgs.append)
        p = FakeProc()
        cv._proc = p
        cv._started_t = time.time()           # 起動直後に落ちた
        cv._on_exit(p, 1)
        check(not cv.gave_up, "1 回落ちただけなら再起動する")
        cv._on_exit(p, 1)
        cv._on_exit(p, 1)
        check(cv.gave_up and "自動再起動はしません" in msgs[-1],
              f"起動直後に {L.MAX_QUICK_FAILS} 回続けて落ちたら諦めて理由を出す")

        cv2 = L.CvLauncher(d, Path(d) / "received", emit=msgs.append)
        cv2._proc = p
        cv2._started_t = time.time()
        cv2._on_exit(p, L.EXIT_NO_VENV)
        check(cv2.gave_up and "setup_windows.bat" in cv2.problem,
              "venv が無い（終了コード 3）ならすぐ諦めてセットアップを案内する")

        cv3 = L.CvLauncher(d, Path(d) / "received", emit=msgs.append)
        cv3._proc = p
        cv3._started_t = time.time() - 3600   # 長く動いてから落ちた
        for _ in range(5):
            cv3._on_exit(p, 1)
            cv3._started_t = time.time() - 3600
        check(not cv3.gave_up, "長く動いてから落ちたものは何度でも再起動する")

        cv4 = L.CvLauncher(d, Path(d) / "received", emit=msgs.append)
        cv4._proc = p
        cv4._stopping = True
        cv4._on_exit(p, 0)
        check(not cv4.gave_up, "止めるときの終了は異常終了として扱わない")


if __name__ == "__main__":
    test_decode_and_parse()
    test_paths_and_quoting()
    test_config_and_running_detection()
    test_lifecycle()
    test_restart_policy()
    finish("ALL CV LAUNCHER TESTS PASSED")
