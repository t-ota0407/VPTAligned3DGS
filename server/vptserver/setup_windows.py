"""初めての Windows で、このサーバを動かせる状態にする（setup_windows.bat から呼ばれる。再実行しても安全）。

setup_windows.bat が Windows 側の Python 環境（server/.venv と requirements.txt）を作ったあと、ここで:
  1. GUI（tkinter / customtkinter）と NVIDIA ドライバ（Windows 側）を確かめる。
  2. WSL2 と CV 用のディストリを用意する。無ければ Ubuntu-24.04 を入れる（動作確認済みの組み合わせ）。
     CV 環境が既にあるディストリはそのまま使う。WSL1 なら 2 へ変換する（WSL1 は GPU を使えない）。
  3. そのディストリで setup_wsl.sh を実行する（venv・torch・hloc・重みの取得まで）。
     使うディストリを launch_config.json に書く（run.py の CV 自動起動が読む）。
  4. ファイアウォール: Quest → PC の UDP 50000 を通す。**Windows の「パブリック」ネットワークで詰まりやすい**:
     初回のダイアログで既定のまま「許可」を押すと、プライベートだけ許可され、パブリックにはブロック規則が
     作られる（ブロックは許可より優先される）。会場や学内の Wi-Fi はパブリック扱いのことが多い。
  5. scene_data/ のシーンを調べ、再ローカライズ DB が無いものはその場で作れるようにする。

`--build-db [シーン名 ...]` で 5 だけを行う（build_scene_db.bat。シーンを後から足したとき）。

判断の部分（ディストリの選び方・シーンの状態・ファイアウォールの読み取りと直す内容）は関数に分けて
テストする（tests/test_setup_windows.py）。WSL・ファイアウォールを実際に操作する部分は実機で確認する。
"""
import argparse
import base64
import os
import shutil
import subprocess
import sys
from pathlib import Path

from . import cv_launcher as L
from .scenes import scene_state

SERVER_DIR = Path(__file__).resolve().parents[1]
SCENE_ROOT = SERVER_DIR / "scene_data"
TESTED_DISTRO = "Ubuntu-24.04"           # setup_wsl.sh の版の固定（Python 3.12）はこれで確認した
DATA_PORT = 50000                        # run.py の --port の既定（Quest → PC）
FIREWALL_RULE = f"VPTAligned3DGS (UDP {DATA_PORT} in)"

# 実行の結果（最後にまとめて出す）
_summary = []


def head(title):
    print(f"\n=== {title} ===")


def ok(msg):
    print("  OK   " + msg)
    _summary.append(("OK", msg))


def warn(en, ja=""):
    print("  [!]  " + en)
    if ja:
        print("       " + ja)
    _summary.append(("WARN", en))


def fail(en, ja=""):
    print("  [x]  " + en)
    if ja:
        print("       " + ja)
    _summary.append(("FAIL", en))


def info(msg):
    print("       " + msg)


def ask_yes(question, default=True):
    """Enter だけなら default。入力が無い（EOF: 自動実行・パイプ）ときは **いいえ**
    （管理者権限の変更や長い DB 構築を、誰も答えていないのに始めない）。"""
    try:
        a = input(f"  ?    {question} {'[Y/n]' if default else '[y/N]'} ").strip().lower()
    except EOFError:
        print("(no input: skipped)")
        return False
    return default if not a else a in ("y", "yes")


# ================= 判断の部分（テスト対象） =================

def plan_distro(names, found_with_venv):
    """CV に使うディストリ。CV 環境のあるもの（find_cv_distro の結果）→ 動作確認済みの Ubuntu-24.04。
    どちらも無ければ None（Ubuntu-24.04 を入れるか、利用者に選んでもらう）。

    他のディストリ（既定の Ubuntu-20.04 等）に勝手に入れない: Python の版が違うと固定した版が入らない。
    """
    if found_with_venv:
        return found_with_venv
    return TESTED_DISTRO if TESTED_DISTRO in names else None


def parse_firewall_state(text):
    """firewall_query_script() の出力を読む。

    返り値: {"rule": None | {"enabled": bool, "profile": str},
             "nets": [(区分, 名前)],                 # 接続中のネットワーク（Public / Private / DomainAuthenticated）
             "app":  [(Action, Profile, 規則ID, 表示名)]}  # python に付いた受信の規則（有効なもの）
    """
    st = {"rule": None, "nets": [], "app": []}
    for ln in text.replace("\r", "").split("\n"):
        f = ln.split("|")
        if f[0] == "RULE" and len(f) >= 3:
            st["rule"] = {"enabled": f[1].strip() == "True", "profile": f[2].strip()}
        elif f[0] == "NET" and len(f) >= 3:
            st["nets"].append((f[1].strip(), f[2].strip()))
        elif f[0] == "APP" and len(f) >= 5:
            st["app"].append((f[1].strip(), f[2].strip(), f[3], f[4].strip()))
    return st


def on_public_network(st):
    return any(cat == "Public" for cat, _ in st["nets"])


def plan_firewall(st, allow_public):
    """直す内容。返り値: (規則に設定するプロファイルの一覧 or None（今のままでよい）, 無効にするブロック規則の ID)。

    python に付いたブロック規則は、どの許可規則よりも優先されるので外す（初回のダイアログで
    「キャンセル」した、または既定のまま「許可」してパブリックにブロックが作られた）。
    """
    want = ["Domain", "Private"] + (["Public"] if allow_public else [])
    rule = st["rule"]
    have = {p.strip() for p in rule["profile"].split(",")} if rule else set()
    covered = "Any" in have or set(want) <= have
    profiles = None if (rule and rule["enabled"] and covered) else want
    blocks = [rid for action, _, rid, _ in st["app"] if action == "Block"]
    return profiles, blocks


def _ps_quote(s):
    return "'" + s.replace("'", "''") + "'"


def firewall_query_script():
    return ("[Console]::OutputEncoding = [Text.Encoding]::UTF8\n"
            f"$r = Get-NetFirewallRule -DisplayName {_ps_quote(FIREWALL_RULE)} -ErrorAction SilentlyContinue"
            " | Select-Object -First 1\n"
            "if ($r) { 'RULE|' + $r.Enabled + '|' + $r.Profile }\n"
            "Get-NetConnectionProfile | ForEach-Object { 'NET|' + $_.NetworkCategory + '|' + $_.Name }\n"
            "Get-NetFirewallApplicationFilter | Where-Object { $_.Program -like '*python*' } | Get-NetFirewallRule |"
            " Where-Object { $_.Direction -eq 'Inbound' -and $_.Enabled -eq 'True' } |"
            " ForEach-Object { 'APP|' + $_.Action + '|' + $_.Profile + '|' + $_.Name + '|' + $_.DisplayName }\n")


def firewall_fix_script(profiles, block_ids):
    """管理者で実行する PowerShell。UDP 50000 の許可規則を作る（または直す）・python のブロック規則を無効にする。"""
    lines = ["$ErrorActionPreference = 'Stop'"]
    if profiles:
        prof = ",".join(profiles)
        lines += [
            f"$r = Get-NetFirewallRule -DisplayName {_ps_quote(FIREWALL_RULE)} -ErrorAction SilentlyContinue",
            f"if ($r) {{ $r | Set-NetFirewallRule -Enabled True -Action Allow -Profile {prof} }}",
            f"else {{ New-NetFirewallRule -DisplayName {_ps_quote(FIREWALL_RULE)} -Direction Inbound -Action Allow"
            f" -Protocol UDP -LocalPort {DATA_PORT} -Profile {prof}"
            " -Description 'Quest 3 -> PC keyframes (VPTAligned3DGS server/run.py)' | Out-Null }",
        ]
    for rid in block_ids:
        lines.append(f"Get-NetFirewallRule -Name {_ps_quote(rid)} | Disable-NetFirewallRule")
    return "\n".join(lines) + "\n"


def _encoded(script):
    return base64.b64encode(script.encode("utf-16-le")).decode("ascii")


# ================= Windows 側 =================

def step_windows():
    head("Windows")
    try:
        import tkinter  # noqa: F401
        import customtkinter  # noqa: F401
        ok("GUI (tkinter / customtkinter)")
    except Exception as e:
        warn(f"GUI is not available ({e}). run.py will run without the window (console only).",
             "GUI が使えません（run.py はウインドウ無しで動きます）。python.org の Python を"
             "「tcl/tk and IDLE」付きで入れ直すと使えます")
    smi = shutil.which("nvidia-smi")
    if smi:
        r = subprocess.run([smi, "--query-gpu=name,driver_version", "--format=csv,noheader"],
                           capture_output=True, text=True)
        ok(f"NVIDIA GPU: {r.stdout.strip() or '?'}")
    else:
        warn("No NVIDIA driver found on Windows. The CV worker will run on CPU (very slow: minutes per alignment).",
             "NVIDIA のドライバが見つかりません。CV は CPU で動き、とても遅くなります（GPU 搭載 PC を推奨）")


# ================= WSL =================

def _run_console(cmd):
    """このコンソールのまま実行する（sudo のパスワードや Ubuntu の初回設定を入力できるように）。"""
    try:
        return subprocess.run(cmd).returncode
    except KeyboardInterrupt:
        return 130


def install_tested_distro():
    print()
    info(f"Installing WSL2 + {TESTED_DISTRO}. Windows may ask for administrator permission.")
    info(f"WSL2 と {TESTED_DISTRO} を入れます（管理者の許可を求められることがあります）。")
    info("When Ubuntu asks, create a user name and password (remember the password: it is needed for 'sudo').")
    info("Ubuntu からユーザー名とパスワードを聞かれたら設定してください（パスワードは後で sudo に使います）。")
    info("Then type 'exit' to come back here. / 設定後、exit と入力するとここへ戻ります。")
    return _run_console([L.wsl_exe() or "wsl.exe", "--install", "-d", TESTED_DISTRO])


def choose_existing(names):
    print()
    for i, n in enumerate(names, 1):
        info(f"{i}) {n}")
    try:
        a = input("  ?    Use which distro? (number, empty = cancel) ").strip()
    except EOFError:
        return None
    return names[int(a) - 1] if a.isdigit() and 1 <= int(a) <= len(names) else None


def step_wsl(args):
    """CV に使うディストリを決め、setup_wsl.sh を実行する。返り値: ディストリ名（できなければ None）。"""
    head("WSL2 (CV worker)")
    cfg = L.load_config(SERVER_DIR)
    venv = args.cv_venv or cfg.get("cv_venv") or L.DEFAULT_VENV

    text = L.wsl_list_text() if L.wsl_available() else None
    names = L.parse_distro_list(text)[0] if text else []
    if args.distro:
        if args.distro not in names:
            fail(f"WSL distro '{args.distro}' not found (installed: {', '.join(names) or 'none'}).")
            return None, venv
        distro = args.distro
    else:
        found = L.find_cv_distro(venv, preferred=cfg.get("wsl_distro"), log=info) if names else None
        distro = plan_distro(names, found)
    if distro is None:
        if names:
            info(f"Installed distros: {', '.join(names)}. None has the CV environment yet.")
            info(f"{TESTED_DISTRO} is recommended (the tested versions need its Python 3.12).")
        else:
            info("WSL2 or a Linux distro is not installed yet. / WSL2（Linux）がまだ入っていません。")
        if ask_yes(f"Install {TESTED_DISTRO} now?"):
            install_tested_distro()
            text = L.wsl_list_text()
            names = L.parse_distro_list(text)[0] if text else []
            if TESTED_DISTRO in names:
                distro = TESTED_DISTRO
            else:
                fail("WSL is not ready yet. Restart Windows, open 'Ubuntu 24.04' from the Start menu once"
                     " to create your user, then run setup_windows.bat again.",
                     "WSL の準備がまだです。Windows を再起動し、スタートメニューから Ubuntu 24.04 を一度開いて"
                     "ユーザーを作ってから、setup_windows.bat をもう一度実行してください")
                return None, venv
        elif names:
            distro = choose_existing(names)
        if distro is None:
            fail("No WSL distro for the CV worker. / CV 用の WSL ディストリがありません")
            return None, venv

    versions = L.parse_distro_versions(text or "")
    if versions.get(distro) == "1":
        info(f"'{distro}' is WSL1, which cannot use the GPU. / '{distro}' は WSL1 で GPU を使えません")
        if not ask_yes(f"Convert '{distro}' to WSL2 now? (takes a few minutes)"):
            fail(f"'{distro}' is WSL1. Convert it with: wsl --set-version {distro} 2")
            return None, venv
        if _run_console([L.wsl_exe(), "--set-version", distro, "2"]) != 0:
            fail(f"Could not convert '{distro}' to WSL2.")
            return None, venv

    info(f"Setting up the CV environment in '{distro}' ({venv}). The first time takes 10-30 minutes"
         " and downloads several GB.")
    info(f"'{distro}' に CV 環境を作ります。初回は 10〜30 分ほどかかり、数 GB をダウンロードします。")
    try:
        server_wsl = L.to_wsl_path(distro, SERVER_DIR)
    except ValueError as e:
        fail(str(e), "このフォルダは WSL から見えません。ローカルのドライブ（C: 等）へ置いてください")
        return None, venv
    # 改行が CRLF に化けていても動くように（ZIP 展開・autocrlf）、LF にした一時ファイルを実行する
    inner = ('tmp="$(mktemp)" && sed "s/\\r$//" "$0" > "$tmp" && '
             f'VPT_CV_VENV={L.bash_path(venv)} bash "$tmp" "$1"; rc=$?; rm -f "$tmp"; exit $rc')
    rc = _run_console([L.wsl_exe(), "-d", distro, "-e", "bash", "-c", inner,
                       f"{server_wsl}/setup_wsl.sh", server_wsl])
    if rc != 0:
        fail(f"setup_wsl.sh failed in '{distro}' (exit code {rc}). See the messages above.",
             "WSL 側のセットアップに失敗しました。上のメッセージを確認し、もう一度実行してください")
        return None, venv
    L.save_config(SERVER_DIR, {"wsl_distro": distro, "cv_venv": venv})
    ok(f"CV environment in WSL '{distro}' ({venv}); saved to {L.CONFIG_NAME}")
    return distro, venv


# ================= ファイアウォール =================

def _powershell(script, timeout=120):
    r = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-EncodedCommand", _encoded(script)],
                       capture_output=True, timeout=timeout)
    return r.returncode, r.stdout.decode("utf-8", "replace")


def step_firewall():
    head(f"Firewall (Quest -> PC, UDP {DATA_PORT})")
    try:
        _, out = _powershell(firewall_query_script())
    except Exception as e:
        warn(f"Could not read the firewall settings ({e}). If Quest cannot reach the PC, see OperationGuide 6.")
        return
    st = parse_firewall_state(out)
    public = on_public_network(st)
    for cat, name in st["nets"]:
        info(f"network: {name} ({cat})")
    allow_public = False
    if public:
        info("This PC is on a 'Public' network. Windows blocks incoming connections there by default,")
        info("so Quest cannot reach run.py unless the port is allowed on Public networks too.")
        info("このネットワークは「パブリック」です。既定では外からの接続が止められ、Quest が PC に届きません。")
        allow_public = ask_yes(f"Allow UDP {DATA_PORT} on Public networks too? (only that one port;"
                               " only used while run.py is running)")
    profiles, blocks = plan_firewall(st, allow_public)
    if blocks:
        info(f"Found {len(blocks)} firewall rule(s) that BLOCK Python (made when the Windows dialog was cancelled"
             " or answered for another network type). Block rules win over allow rules.")
        info("Python を遮断する規則があります（ブロックは許可より優先されます）。")
    if profiles is None and not blocks:
        ok(f"firewall rule '{FIREWALL_RULE}'")
        if public and not allow_public:
            warn("On a Public network Quest may not reach this PC. Switch the network to Private, or run this again.",
                 "パブリックのままだと届かないことがあります。ネットワークを「プライベート」にしてください")
        return
    what = []
    if profiles:
        what.append(f"allow UDP {DATA_PORT} ({', '.join(profiles)})")
    if blocks:
        what.append(f"disable {len(blocks)} Python block rule(s)")
    if not ask_yes(f"Change the firewall now: {' / '.join(what)}? (needs administrator permission)"):
        warn("Firewall not changed. If Quest cannot reach the PC, run setup_windows.bat again.")
        return
    outer = ("$p = Start-Process powershell -Verb RunAs -Wait -PassThru -WindowStyle Hidden -ArgumentList "
             f"'-NoProfile','-ExecutionPolicy','Bypass','-EncodedCommand','{_encoded(firewall_fix_script(profiles, blocks))}'\n"
             "exit $p.ExitCode\n")
    try:
        rc, _ = _powershell(outer, timeout=300)
        _, out = _powershell(firewall_query_script())
    except Exception as e:
        rc, out = str(e), ""
    p2, b2 = plan_firewall(parse_firewall_state(out), allow_public)
    if p2 is None and not b2:
        ok(f"firewall: {' / '.join(what)}")
    else:
        warn(f"Firewall change did not apply (result {rc}). Was the administrator prompt declined?",
             "ファイアウォールを変更できませんでした（管理者の許可を拒否しましたか？）")


# ================= シーン（再ローカライズ DB） =================

def list_scenes():
    if not SCENE_ROOT.is_dir():
        return {}
    return {d.name: scene_state(d) for d in sorted(SCENE_ROOT.iterdir()) if d.is_dir()}


def build_db(distro, venv, names):
    server_wsl = L.to_wsl_path(distro, SERVER_DIR)
    done = []
    for name in names:
        print(f"\n--- building the localization DB: scene_data/{name} ---")
        rc = _run_console([L.wsl_exe(), "-d", distro, "-e", "bash", "-c",
                           f'cd "$0" && exec {L.bash_path(venv)}/bin/python -m vptserver.cv.hloc_db --scene "$1"',
                           server_wsl, f"scene_data/{name}"])
        if rc == 0 and scene_state(SCENE_ROOT / name) == "built":
            ok(f"DB built: scene_data/{name}")
            done.append(name)
        else:
            fail(f"DB build failed: scene_data/{name} (exit code {rc}). See the messages above.")
    return done


def step_scenes(distro, venv, only=None, ask=True):
    head("Scenes (scene_data)")
    scenes = list_scenes()
    if not scenes:
        warn(f"No scenes in {SCENE_ROOT}. Put the COLMAP data of your 3DGS scene there (see server/README.md).",
             "scene_data にシーンがありません。3DGS を作ったときの COLMAP 一式を置いてください（server/README.md）")
        return
    labels = {"built": "ready", "partial": "DB incomplete (build again)", "raw": "DB not built yet",
              "invalid": "not a COLMAP scene (needs images/ and sparse/0/)"}
    for name, s in scenes.items():
        info(f"{name:24s} {labels[s]}")
    todo = [n for n, s in scenes.items() if s in ("raw", "partial") and (not only or n in only)]
    for n in only or []:
        if n not in scenes:
            fail(f"scene_data/{n} not found")
    if any(s == "built" for s in scenes.values()):
        ok(f"{sum(s == 'built' for s in scenes.values())} scene(s) ready")
    if not todo:
        return
    if distro is None:
        warn("Some scenes have no DB yet; finish the WSL setup first, then run build_scene_db.bat.")
        return
    info("The DB is built once per scene on the GPU (several to tens of minutes per scene).")
    info("DB はシーンごとに一度だけ作ります（GPU で 1 シーン数分〜数十分）。")
    if ask and not ask_yes(f"Build the DB now for: {', '.join(todo)}?"):
        warn(f"DB not built for: {', '.join(todo)}. Run build_scene_db.bat later.")
        return
    build_db(distro, venv, todo)


# ================= まとめ =================

def print_summary():
    head("Summary")
    for kind, msg in _summary:
        print(f"  {kind:4s} {msg}")
    if any(k == "FAIL" for k, _ in _summary):
        print("\nSetup is not complete. Fix the [x] items above and run setup_windows.bat again.")
        print("セットアップは完了していません。[x] の項目を直して、setup_windows.bat をもう一度実行してください。")
        return 1
    print("\nReady. Double-click start_experiment.bat (experiment) or start_demo.bat (demo exhibition).")
    print("準備ができました。start_experiment.bat（評価実験）か start_demo.bat（デモ展示）をダブルクリックしてください。")
    return 0


def main(argv=None):
    ap = argparse.ArgumentParser(description="VPTAligned3DGS: set up this Windows PC (called by setup_windows.bat)")
    ap.add_argument("--distro", default=None, help="CV に使う WSL ディストリ（省略時は自動で決める）")
    ap.add_argument("--cv-venv", default=None, help=f"WSL 側の CV の venv（既定 {L.DEFAULT_VENV}）")
    ap.add_argument("--skip-wsl", action="store_true", help="WSL の確認とセットアップを飛ばす")
    ap.add_argument("--skip-firewall", action="store_true", help="ファイアウォールを確認しない")
    ap.add_argument("--build-db", nargs="*", metavar="SCENE", default=None,
                    help="DB の構築だけを行う（シーン名を省略すると DB の無いシーンすべて）")
    args = ap.parse_args(argv)
    try:
        # 子プロセス（wsl.exe）の出力と順序が入れ替わらないように 1 行ごとに書き出す
        sys.stdout.reconfigure(line_buffering=True, errors="replace")
    except Exception:
        pass
    if os.name != "nt":
        print("This setup is for Windows. On Linux, run server/setup_wsl.sh instead.")
        return 1

    print("VPTAligned3DGS PC setup  (server: %s)" % SERVER_DIR)
    if args.build_db is not None:
        cfg = L.load_config(SERVER_DIR)
        venv = args.cv_venv or cfg.get("cv_venv") or L.DEFAULT_VENV
        distro = args.distro or L.find_cv_distro(venv, preferred=cfg.get("wsl_distro"), log=info)
        if distro is None:
            fail("No WSL distro with the CV environment. Run setup_windows.bat first.",
                 "CV 環境のある WSL ディストリがありません。先に setup_windows.bat を実行してください")
        step_scenes(distro, venv, only=args.build_db or None, ask=not args.build_db)
        return print_summary()

    step_windows()
    distro, venv = (None, None) if args.skip_wsl else step_wsl(args)
    if not args.skip_firewall:
        step_firewall()
    step_scenes(distro, venv or L.DEFAULT_VENV)
    return print_summary()


if __name__ == "__main__":
    sys.exit(main())
