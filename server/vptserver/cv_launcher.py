"""WSL2 の CV ワーカ（localize.watch）を、Windows の run.py から起動・終了する。

**なぜ。** 以前は Windows で run.py、WSL2 で localize を、別々のターミナルから手で起動していた。
手間なうえ、run.py の `--out` と localize の `--watch` を手で合わせる必要があり、食い違うと
キーフレームが届くのに位置合わせが永遠に返らない（実機で発生）。run.py が起動・終了を受け持てば、
1 回のダブルクリック（start_demo.bat）で済み、`--watch` は `--out` から自動で決まる。

**公開を前提に、作者の環境に依存しない**（初めての Windows でも動く）:
  - WSL のディストリを決め打ちしない。既定のディストリに CV 環境があるとは限らない
    （作者の PC でも既定は Ubuntu-20.04、CV 環境は Ubuntu-24.04 にあった）。CV の venv があるものを探し、
    見つけたら launch_config.json に覚える。
  - Windows のパスは WSL の wslpath で変換し、bash に渡す文字列はすべてクォートする
    （空白や日本語を含むユーザー名・OneDrive 配下のデスクトップでも壊れない）。
  - wsl.exe の出力は UTF-16LE のことがある（WSL_UTF8=1 を付けても古い WSL では効かない）ので両方読める。
  - 見つからない・起動できないときは、次に何をすればよいかを英語と日本語のどちらでも分かる短い文で出す。

**終了の連動。** localize の標準入力をパイプでつないだまま何も送らない。run.py が終わるとパイプが閉じ、
localize は EOF を見て終了する（`--exit-on-stdin-eof`。→ cv/lifecycle.py）。run.py が正常に終わる
ときは、パイプを閉じたうえで、終わらなければ Linux 側の PID を kill する。

純粋な部分（出力の解釈・パスの変換・コマンドの組み立て・設定の読み書き）は関数に分けてテストする
（tests/test_cv_launcher.py）。WSL を実際に起動する部分は実機で確認する。
"""
import json
import os
import re
import shlex
import shutil
import subprocess
import threading
import time
from pathlib import Path, PureWindowsPath

from .cv.heartbeat import HEARTBEAT_NAME
from .liveness import heartbeat_file_age

DEFAULT_VENV = "~/vptcv"
CONFIG_NAME = "launch_config.json"
CREATE_NO_WINDOW = 0x08000000          # 子プロセスのためにコンソール窓を開かない（Windows のみ有効）
ALREADY_RUNNING_S = 8.0                # ベースの生存信号がこれより新しければ、別の localize が動いている

# 起動直後に落ちたら「設定の問題」とみなし、何度も再起動しない
QUICK_FAIL_S = 30.0
MAX_QUICK_FAILS = 3
WATCHDOG_S = 5.0                       # この間隔で「動いていなければ起動する」を見る（再起動の間隔にもなる）
EXIT_NO_VENV = 3                       # build_cv_script が venv 無しで返す終了コード

# localize の INFO の行のうち、GUI のイベント欄の「重要」にも出すもの（ほかの INFO は「すべて」でだけ見える）
CV_NOTABLE_INFO = ("DB ロード", "scene 変更", "セッション追従", "設定更新")
# 準備ができたことを示す行（緑）
CV_READY_INFO = ("watch 開始", "CUDA: available")
# 1 行に収める長さ（GUI のイベント欄。コンソールと cv.log には全文が出る）
GUI_LINE_MAX = 300


# ================= 純粋な部分（テスト対象） =================

def decode_wsl_output(raw):
    """wsl.exe の出力をテキストにする。wsl.exe 自身のメッセージは UTF-16LE のことがあり、
    Linux 側のコマンドの出力は UTF-8。どちらでも読めるようにする。"""
    if not raw:
        return ""
    if raw.startswith(b"\xff\xfe") or (len(raw) >= 2 and raw[1:2] == b"\x00"):
        try:
            return raw.decode("utf-16-le").lstrip("\ufeff")
        except UnicodeDecodeError:
            pass
    return raw.decode("utf-8", errors="replace")


def _distro_rows(text):
    """`wsl -l -v` の表の行を (名前, 既定か, WSL のバージョン) で返す。

    見出しや状態の列は Windows の言語で変わるので、**末尾が数字（バージョン）の行**だけをデータとみなす。
    既定の行の先頭には言語に依らず `*` が付く（`wsl -l` の「(既定)」は言語で変わるので使わない）。
    """
    for ln in text.replace("\x00", "").replace("\r", "").split("\n"):
        s = ln.strip()
        is_default = s.startswith("*")
        tokens = s.lstrip("*").split()
        if len(tokens) >= 3 and tokens[-1].isdigit():
            yield tokens[0], is_default, tokens[-1]


def parse_distro_list(text):
    """`wsl -l -v` の出力からディストリ名と既定を取り出す。

    返り値: (names, default)。docker-desktop 系は CV 環境にならないので除く。
    """
    names, default = [], None
    for name, is_default, _ in _distro_rows(text):
        if name.lower().startswith("docker-desktop"):
            continue
        names.append(name)
        if is_default:
            default = name
    return names, default


def parse_distro_versions(text):
    """`wsl -l -v` の出力から {ディストリ名: WSL のバージョン（"1" / "2"）}。WSL1 は GPU（CUDA）を使えない。"""
    return {name: ver for name, _, ver in _distro_rows(text)}


def windows_to_wsl_path(path, mount_root="/mnt/"):
    """wslpath が使えないときの代わり（`C:\\a b\\c` → `/mnt/c/a b/c`）。

    ネットワークドライブや UNC パスは WSL の /mnt から見えないので ValueError。
    """
    p = PureWindowsPath(os.path.abspath(str(path)))
    drive = p.drive
    if len(drive) != 2 or drive[1] != ":":
        raise ValueError(f"WSL から見えない場所です（ネットワークドライブ・UNC 等）: {path}")
    rest = "/".join(p.parts[1:])
    root = mount_root.rstrip("/")
    return f"{root}/{drive[0].lower()}" + (f"/{rest}" if rest else "")


def bash_path(p):
    """bash に渡すパス。`~/` で始まるものは $HOME を展開させ、残りはクォートする。"""
    if p == "~":
        return '"$HOME"'
    if p.startswith("~/"):
        return '"$HOME"/' + shlex.quote(p[2:])
    return shlex.quote(p)


def build_cv_script(server_wsl, watch_wsl, venv=DEFAULT_VENV):
    """WSL 側で localize を起動する bash スクリプト（1 行）。

    - venv が無ければ、何をすればよいかを出して終了コード 3 で抜ける（再起動ループにしない）。
    - exec で bash を python に置き換える（PID が localize 本体になり、kill が効く）。
    - 出力は UTF-8・バッファ無し（run.py へ 1 行ずつ流す）。
    """
    v = bash_path(venv)
    # WSL へ渡すコマンド行は ASCII だけにする（環境によって非 ASCII が化ける）。日本語の説明は run.py 側で出す
    missing = shlex.quote(f"CV environment not found in WSL: {venv}. Run server/setup_windows.bat")
    return ("export PYTHONUNBUFFERED=1 PYTHONIOENCODING=utf-8; "
            f"if [ ! -x {v}/bin/python ]; then echo {missing}; exit 3; fi; "
            f". {v}/bin/activate && cd {shlex.quote(server_wsl)} && "
            f"exec python -m vptserver.cv.localize --watch {shlex.quote(watch_wsl)} --exit-on-stdin-eof")


def cv_event_level(line):
    """localize の出力 1 行の重要度（GUI のイベント欄の色と、「重要」に出すかどうか）。

    "error" / "warn"  … localize が `ERROR:` / `WARNING:` を付けた行（→ cv/localize.py の _LevelFormatter）、
                        hloc の `… WARNING]`、Python の例外（Traceback と `XxxError:` の行）
    "ok"              … 準備ができた（watch 開始・CUDA が使える）
    "info"            … 覚えておくべき出来事（DB のロード・シーン変更・セッションの追従・設定の更新）
    "debug"           … それ以外（キーフレームごとの採否・hloc の進み具合など）。「すべて」でだけ見える
    """
    if ("ERROR" in line or "CRITICAL" in line or "Traceback" in line
            or re.search(r"\b[A-Za-z]*(Error|Exception)\b:", line)):
        return "error"
    if "WARNING" in line:
        return "warn"
    if any(k in line for k in CV_READY_INFO):
        return "ok"
    if any(k in line for k in CV_NOTABLE_INFO):
        return "info"
    return "debug"


def gui_line(line):
    """GUI に出す形に整える。hloc の進み具合（tqdm）は復帰文字（CR）で上書きしながら 1 行に溜まるので、
    最後の表示だけを残す。長すぎる行は末尾を残して切る。"""
    parts = [x for x in line.split("\r") if x.strip()]
    text = parts[-1] if parts else line
    return text if len(text) <= GUI_LINE_MAX else "…" + text[-(GUI_LINE_MAX - 1):]


def cuda_from_line(line):
    """localize が起動時に出す行（`CUDA: available` / `CUDA: NOT available`）から GPU の可否。該当しなければ None。"""
    if "CUDA: NOT available" in line:
        return False
    if "CUDA: available" in line:
        return True
    return None


def load_config(server_dir):
    p = Path(server_dir) / CONFIG_NAME
    try:
        return json.loads(p.read_text(encoding="utf-8")) if p.exists() else {}
    except Exception:
        return {}


def save_config(server_dir, cfg):
    """設定を書く。**既にある鍵は残し、渡した鍵だけを上書きする。**

    WSL の設定（wsl_distro / cv_venv）と GUI の言語（gui_lang）を同じファイルに持つので、
    片方を保存したときにもう片方を消さないようにする。
    """
    p = Path(server_dir) / CONFIG_NAME
    try:
        merged = dict(load_config(server_dir))
        merged.update(cfg)
        p.write_text(json.dumps(merged, ensure_ascii=False, indent=2), encoding="utf-8")
    except Exception:
        pass


def cv_already_running(out_dir, now=None):
    """`<out>/cv_heartbeat.json`（ベースの生存信号）が新しければ、別の localize がこのフォルダを処理中。"""
    age = heartbeat_file_age(Path(out_dir) / HEARTBEAT_NAME, now)
    return age is not None and age <= ALREADY_RUNNING_S


# ================= WSL を呼ぶ部分 =================

def wsl_exe():
    return shutil.which("wsl.exe") or shutil.which("wsl")


def wsl_available():
    return wsl_exe() is not None


def _wsl(args, distro=None, timeout=60):
    """wsl.exe を実行して (終了コード, 出力テキスト) を返す。起動できなければ (None, 理由)。"""
    exe = wsl_exe()
    if exe is None:
        return None, "wsl.exe not found"
    cmd = [exe] + (["-d", distro] if distro else []) + list(args)
    env = dict(os.environ, WSL_UTF8="1")
    try:
        r = subprocess.run(cmd, capture_output=True, timeout=timeout, env=env,
                           creationflags=CREATE_NO_WINDOW if os.name == "nt" else 0)
    except subprocess.TimeoutExpired:
        return None, f"wsl.exe timed out ({timeout}s)"
    except Exception as e:
        return None, str(e)
    return r.returncode, decode_wsl_output(r.stdout) + decode_wsl_output(r.stderr)


def wsl_list_text():
    """`wsl -l -v` の出力（WSL が無い・ディストリが無いときは None）。"""
    rc, out = _wsl(["-l", "-v"], timeout=60)
    return out if rc == 0 else None


def list_distros():
    out = wsl_list_text()
    return parse_distro_list(out) if out is not None else ([], None)


def distro_has_venv(distro, venv=DEFAULT_VENV):
    rc, _ = _wsl(["-e", "bash", "-c", f"test -x {bash_path(venv)}/bin/python"], distro=distro, timeout=90)
    return rc == 0


def to_wsl_path(distro, win_path):
    """Windows のパスを WSL のパスへ。wslpath（自動マウントの設定も反映される）→ だめなら手で変換。"""
    rc, out = _wsl(["-e", "wslpath", "-a", str(Path(win_path).resolve())], distro=distro, timeout=60)
    if rc == 0 and out.strip().startswith("/"):
        return out.strip().splitlines()[-1].strip()
    return windows_to_wsl_path(win_path)


def find_cv_distro(venv=DEFAULT_VENV, preferred=None, log=print):
    """CV の venv があるディストリを探す。preferred（引数・前回の設定）→ 既定 → その他の順。"""
    names, default = list_distros()
    if not names:
        return None
    order = []
    for n in [preferred, default] + names:
        if n and n in names and n not in order:
            order.append(n)
    for n in order:
        log(f"[cv] WSL '{n}' に CV 環境（{venv}）があるか確認中…")
        if distro_has_venv(n, venv):
            return n
    return None


# ================= 起動・終了の管理 =================

class CvLauncher:
    """localize を WSL2 で起動し、出力を run.py に流し、終了・異常終了を管理する。

    見張りのスレッドが WATCHDOG_S ごとに「動いていなければ起動する」を見る。これで次の 3 つを 1 か所で扱う:
      - 異常終了したら再起動する（起動直後に何度も落ちるなら設定の問題として諦める）
      - 別の localize（手動で起動したもの）が動いている間は起動しない。それが終わったら引き継ぐ
      - run.py を閉じてすぐ開き直したとき、終了途中の古い localize が消えてから起動する
    WSL の確認（初回はディストリの起動に数十秒かかる）は見張りのスレッドで行い、GUI を止めない。
    """

    def __init__(self, server_dir, out_dir, distro=None, venv=None, emit=print, on_event=None):
        self.server_dir = Path(server_dir).resolve()
        self.out_dir = Path(out_dir).resolve()
        cfg = load_config(self.server_dir)
        self.distro = distro or cfg.get("wsl_distro")
        self.venv = venv or cfg.get("cv_venv") or DEFAULT_VENV
        self.emit = emit
        self.on_event = on_event or (lambda msg, en=None, level="info": None)
        self.problem = ""            # 起動できない理由（コンソール・GUI の日本語表示用）
        self.problem_en = ""         # 同じ理由の英語（GUI の既定の表示用）
        self.gave_up = False         # 設定の問題で起動を諦めた
        self.cuda = None             # localize が起動時に出す CUDA の可否（True / False / 未確認 None）
        self._proc = None
        self._pid = None             # Linux 側の localize の PID（localize が最初に "pid=N" を出す）
        self._wanted = False
        self._stopping = False
        self._lock = threading.Lock()
        self._wake = threading.Event()
        self._watchdog = None
        self._quick_fails = 0
        self._started_t = 0.0
        self._external_noted = False
        self._log_path = self.out_dir / "cv.log"
        self._server_wsl = None
        self._watch_wsl = None

    @property
    def running(self):
        return self._proc is not None and self._proc.poll() is None

    def _say(self, msg, event=False, en=None, level="info"):
        """コンソールへ出す。event=True なら GUI のイベント欄にも（en はその英語・level は重要度）。"""
        self.emit(msg)
        if event:
            self.on_event(msg, en=en, level=level)

    def _fail(self, ja, en):
        """起動を諦める。理由は GUI の一番上にも出る（英語・日本語）。"""
        self.problem, self.problem_en = ja, en
        self.gave_up = True

    def status(self):
        """GUI が 1 秒ごとに読む今の状態（読むだけ。どのスレッドから呼んでもよい）。

        phase: "failed"   … 起動を諦めた（problem に理由）
               "running"  … localize のプロセスが動いている（生存信号はシーン DB の読み込み後から）
               "external" … 別の localize がこのフォルダを処理中なので、起動せずに待っている
               "starting" … 起動を依頼済み（WSL の確認中・異常終了からの再起動待ち）
               "idle"     … まだ依頼されていない（シーンが決まっていない）
        """
        if self.gave_up:
            phase = "failed"
        elif self.running:
            phase = "running"
        elif self._external_noted:
            phase = "external"
        elif self._wanted:
            phase = "starting"
        else:
            phase = "idle"
        return {"phase": phase, "problem": self.problem, "problem_en": self.problem_en or self.problem,
                "distro": self.distro, "cuda": self.cuda,
                "run_s": (time.time() - self._started_t) if phase == "running" else None,
                "log": str(self._log_path)}

    # ---- 公開 API ----

    def start(self):
        """起動を依頼する（すぐ戻る）。実際の起動と WSL の確認は見張りのスレッドが行う。"""
        self._wanted = True
        self._stopping = False
        if self._watchdog is None:
            self._watchdog = threading.Thread(target=self._watch, name="cv-watchdog", daemon=True)
            self._watchdog.start()
        self._wake.set()

    def retry(self):
        """諦めた後にもう一度試す（GUI の「再試行」）。setup_windows.bat をやり直した後などに使う。

        ディストリとパスも決め直す（セットアップで CV 環境の場所が変わったかもしれないため）。
        """
        if not self.gave_up:
            return
        self.problem = self.problem_en = ""
        self._quick_fails = 0
        self._server_wsl = self._watch_wsl = None
        self.gave_up = False
        self._say("[cv] CV の起動をやり直します", event=True, en="[cv] Retrying to start the CV worker",
                  level="mark")
        self.start()

    def stop(self, timeout=8.0):
        """localize を止める。標準入力を閉じる（→ 自分で終了）→ 終わらなければ Linux 側を kill → wsl.exe を止める。"""
        self._wanted = False
        self._stopping = True
        self._wake.set()
        with self._lock:
            proc = self._proc
        if proc is None or proc.poll() is not None:
            return
        self.emit("[cv] localize を終了しています…")
        try:
            proc.stdin.close()
        except Exception:
            pass
        try:
            proc.wait(timeout=timeout)
            self.emit("[cv] localize は終了しました")
            return
        except subprocess.TimeoutExpired:
            pass
        if self._pid is not None:
            _wsl(["-e", "kill", "-TERM", str(self._pid)], distro=self.distro, timeout=20)
            try:
                proc.wait(timeout=5)
                self.emit("[cv] localize を停止しました")
                return
            except subprocess.TimeoutExpired:
                _wsl(["-e", "kill", "-KILL", str(self._pid)], distro=self.distro, timeout=20)
        try:
            proc.kill()
        except Exception:
            pass
        self.emit("[cv] localize を強制終了しました")

    # ---- 見張り ----

    def _watch(self):
        while True:
            self._wake.wait(WATCHDOG_S)
            self._wake.clear()
            if self._stopping:
                return
            if not self._wanted or self.gave_up or self.running:
                continue
            if cv_already_running(self.out_dir):
                if not self._external_noted:
                    self._external_noted = True
                    self._say("[cv] 別の localize がこのフォルダを処理中のため、起動を待ちます"
                              "（手動で起動したものをそのまま使い、終わったら引き継ぎます）", event=True,
                              en="[cv] Another localize is already processing this folder. Using it, "
                                 "and taking over when it ends")
                continue
            self._external_noted = False
            if self._server_wsl is None and not self.prepare():
                self.gave_up = True
                self._say("[cv] CV を自動起動できません: " + self.problem, event=True,
                          en="[cv] Cannot start the CV worker: " + (self.problem_en or self.problem),
                          level="error")
                continue
            self._spawn()

    def prepare(self):
        """ディストリとパスを決める。失敗したら False（self.problem に理由）。"""
        if os.name != "nt":
            self.problem = "CV の自動起動は Windows でのみ使えます（--no-cv で手動起動）"
            self.problem_en = "Starting the CV worker automatically works only on Windows (use --no-cv)"
            return False
        if not wsl_available():
            self.problem = ("WSL がインストールされていません。server\\setup_windows.bat の案内に従ってください"
                            " (WSL is not installed — run server\\setup_windows.bat)")
            self.problem_en = "WSL is not installed. Run server\\setup_windows.bat and follow its guidance"
            return False
        names, _ = list_distros()
        if self.distro and self.distro not in names:
            self._say(f"[cv] 設定のディストリ '{self.distro}' が見つかりません。探し直します")
            self.distro = None
        if not self.distro or not distro_has_venv(self.distro, self.venv):
            found = find_cv_distro(self.venv, preferred=self.distro, log=self.emit)
            if found is None:
                self.problem = (f"どの WSL ディストリにも CV 環境（{self.venv}）がありません。"
                                "server\\setup_windows.bat を実行してください"
                                f" (CV environment {self.venv} not found in WSL — run server\\setup_windows.bat)")
                self.problem_en = (f"No WSL distribution has the CV environment ({self.venv}). "
                                   "Run server\\setup_windows.bat")
                return False
            self.distro = found
        save_config(self.server_dir, {"wsl_distro": self.distro, "cv_venv": self.venv})
        try:
            self._server_wsl = to_wsl_path(self.distro, self.server_dir)
            self._watch_wsl = to_wsl_path(self.distro, self.out_dir)
        except ValueError as e:
            self.problem = str(e)
            self.problem_en = ("This folder is not reachable from WSL (network drive or UNC path). "
                               "Put the repository on a local drive")
            return False
        self._say(f"[cv] WSL '{self.distro}' で CV を起動します（{self.venv}・監視 {self._watch_wsl}）")
        return True

    def _spawn(self):
        script = build_cv_script(self._server_wsl, self._watch_wsl, self.venv)
        try:
            proc = subprocess.Popen(
                [wsl_exe(), "-d", self.distro, "-e", "bash", "-c", script],
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                creationflags=CREATE_NO_WINDOW, env=dict(os.environ, WSL_UTF8="1"))
        except Exception as e:
            self._fail(f"wsl.exe を起動できません: {e}", f"Cannot start wsl.exe: {e}")
            self._say("[cv] " + self.problem, event=True, en="[cv] " + self.problem_en, level="error")
            return
        with self._lock:
            self._proc = proc
            self._pid = None
        self._started_t = time.time()
        self.cuda = None                 # 起動し直したら確かめ直す（localize が起動時に出す）
        threading.Thread(target=self._pump, args=(proc,), name="cv-output", daemon=True).start()
        self._say(f"[cv] localize を起動しました（ログ: {self._log_path}）", event=True,
                  en=f"[cv] localize started (log: {self._log_path})")

    def _pump(self, proc):
        """localize の出力を 1 行ずつ run.py へ流し、cv.log にも残す。終わったら異常終了を扱う。"""
        try:
            self._log_path.parent.mkdir(parents=True, exist_ok=True)
            logf = open(self._log_path, "a", encoding="utf-8")
        except Exception:
            logf = None
        try:
            for raw in iter(proc.stdout.readline, b""):
                line = decode_wsl_output(raw).rstrip("\r\n")
                if not line:
                    continue
                m = re.search(r"\bpid=(\d+)\b", line)
                if m and self._pid is None:
                    self._pid = int(m.group(1))
                cuda = cuda_from_line(line)
                if cuda is not None:
                    self.cuda = cuda
                self.emit("[cv] " + line)
                if logf is not None:
                    logf.write(time.strftime("%Y-%m-%d %H:%M:%S ") + line + "\n")
                    logf.flush()
                # GUI のイベント欄へ。重要度は行から決める（debug の行は「すべて」でだけ見える）
                self.on_event("[cv] " + gui_line(line), level=cv_event_level(line))
        except Exception:
            pass
        finally:
            code = proc.wait()
            if logf is not None:
                logf.close()
            self._on_exit(proc, code)

    def _on_exit(self, proc, code):
        if self._stopping or proc is not self._proc:
            return
        lived = time.time() - self._started_t
        self._quick_fails = self._quick_fails + 1 if lived < QUICK_FAIL_S else 0
        if code == EXIT_NO_VENV:
            self._fail(f"WSL '{self.distro}' に CV 環境（{self.venv}）がありません。"
                       "server\\setup_windows.bat を実行してください",
                       f"The CV environment ({self.venv}) is missing in WSL '{self.distro}'. "
                       "Run server\\setup_windows.bat")
            self._say("[cv] " + self.problem, event=True, en="[cv] " + self.problem_en, level="error")
        elif self._quick_fails >= MAX_QUICK_FAILS:
            self._fail(f"localize が起動直後に {MAX_QUICK_FAILS} 回続けて終了しました（終了コード {code}）。"
                       f"{self._log_path} を確認してください",
                       f"localize exited right after starting {MAX_QUICK_FAILS} times in a row "
                       f"(exit code {code}). Check {self._log_path}")
            self._say("[cv] " + self.problem + "。自動再起動はしません", event=True,
                      en="[cv] " + self.problem_en + ". Not restarting automatically", level="error")
        else:
            self._say(f"[cv] localize が終了しました（終了コード {code}）。{WATCHDOG_S:.0f} 秒後に再起動します",
                      event=True,
                      en=f"[cv] localize exited (exit code {code}). Restarting in {WATCHDOG_S:.0f} s",
                      level="warn")
