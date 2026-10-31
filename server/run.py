#!/usr/bin/env python3
"""VPTAligned3DGS オフロードサーバ（Windows 側の UDP 受け口 ＋ ネイティブ GUI）。

- Quest から UDP で届くキーフレームを受信・再構成し ./received/ に保存。
- LAN にビーコンを流し、Quest が送信先 IP を自動検出できるようにする。
- WSL2 の CV（vptserver.cv.localize --watch）が書き出す received/result_sim3.json を検知し、
  Quest へ Sim(3) を返送（type=3 RESULT）。
- 通信状態を customtkinter のネイティブウインドウで可視化（pip install customtkinter）。
  未導入ならヘッドレスで動作。

中身は vptserver/ の各モジュールにある（このファイルは引数の解析と組み立てだけ）:
    dispatch.py  データポートに届くパケットの振り分け（キーフレーム / PING / RESET / 実験イベント）
    session.py   セッションフォルダの切替・cv.json の書き出し・Sim(3) の返送
    liveness.py  PONG の中身（CV の生存信号の読み取り）
    events.py    実験イベントの保存（CP 実測値など）
    scenes.py    DB ができているシーンの判定
    cv_launcher.py  WSL2 の localize の起動・終了

使い方:
    python run.py                 # 受信＋ビーコン＋返送＋GUIウインドウ
    python run.py --verbose
    python run.py --no-gui --no-beacon
"""
import argparse
import os
import sys
import threading
from pathlib import Path

from vptserver import gui as gui_mod
from vptserver.beacon import Beacon, DEFAULT_BEACON_PORT
from vptserver.dispatch import udp_loop
from vptserver.events import EventSink
from vptserver.reassembler import Reassembler
from vptserver.scenes import autodetect_scene, list_built_scenes
from vptserver.session import ResultSender, SessionManager, write_cv_config
from vptserver.state import ServerState
from vptserver.udp_receiver import UdpReceiver

# GUI・コンソール・受信スレッドが共有する状態
STATE = ServerState()


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
        sender = ResultSender(rx, args.result_port, sessions=sessions, fixed_file=args.result_file,
                              state=STATE)
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
        threading.Thread(target=udp_loop, args=(rx, asm, sessions, args.result_port, events),
                         kwargs={"state": STATE}, daemon=True).start()
        print("[server] GUI ウインドウを表示中（閉じると終了）")
        try:
            gui_mod.run_dashboard(STATE, on_close=cleanup,
                                  config=cv_cfg, scenes=cv_scenes, on_config=update_cv)
        except KeyboardInterrupt:
            cleanup()
    else:
        print("[server] Ctrl-C to stop")
        try:
            udp_loop(rx, asm, sessions, args.result_port, events, state=STATE)
        except KeyboardInterrupt:
            print("\n[server] stopping...")
        finally:
            cleanup()


if __name__ == "__main__":
    main()
