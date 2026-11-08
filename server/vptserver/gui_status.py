"""GUI に出す判定（純粋関数・テスト対象）。gui.py は描画だけを受け持つ。

一番上の総合ステータスは「準備できたか・次に何をするか」を 1 行で出す。**判定の材料は Quest の
HUD と同じ**にする（CV の生存は liveness.read_cv_status、Quest の生存は PING）。PC と Quest で
食い違うと、どちらを信じればよいか分からなくなる。

返す値は文言そのものではなく (level, 鍵, 差し込み) で、文言は i18n.py が言語ごとに持つ。
level は "ok"（緑）/ "busy"（青・待てばよい）/ "warn"（琥珀・人の操作が要る）/ "error"（赤）。

入力:
  snap     … ServerState.snapshot()（quest_age・keyframe_count など）
  cv       … liveness.read_cv_status() の結果（PONG と同じもの）
  launcher … CvLauncher.status() の結果。--no-cv なら None
  scene    … cv.json の scene（未決定なら None）
"""
import math
import os
import socket
import time

# Quest は 2 秒ごとに PING を送る（UDPManager.pingIntervalS）。3 回続けて届かなければ途切れたとみなす
QUEST_STALE_S = 6.0
# localize のプロセスが動いているのに、これだけ経っても生存信号が無ければ異常とみなす。
# 生存信号はシーン DB を読み込んだ後に書き始める（localize の watch 開始）ので、初回の WSL の起動と
# DB の読み込みの分は待つ。
CV_SILENT_AFTER_S = 300.0


def quest_alive(snap):
    age = (snap or {}).get("quest_age")
    return age is not None and age <= QUEST_STALE_S


def overall_status(snap, cv, launcher, scene):
    """総合ステータス (level, key, params)。上から順に、最初に当てはまったものを出す。"""
    cv = cv or {}
    phase = launcher.get("phase") if launcher else None
    if phase == "failed":
        return "error", "st_cv_failed", {}
    if not scene:
        return "warn", "st_need_scene", {}
    if not cv.get("cv_alive"):
        if launcher is None:
            return "warn", "st_cv_manual", {}
        if phase == "running" and (launcher.get("run_s") or 0.0) > CV_SILENT_AFTER_S:
            return "error", "st_cv_silent", {}
        return "busy", "st_cv_starting", {}
    if cv.get("cv_on"):
        # CV は生きているが、RESET 前のセッションをまだ片付けている（→ liveness._read_base_cv_status）
        return "busy", "st_cv_other", {"session": cv["cv_on"]}
    if not quest_alive(snap):
        return "warn", "st_wait_quest", {}
    if cv.get("cv_busy"):
        return "busy", "st_processing", {"n": int(cv.get("cv_busy_n") or 0)}
    if launcher is not None and launcher.get("cuda") is False:
        return "warn", "st_ready_cpu", {}
    return "ok", "st_ready", {}


def cv_line(cv, launcher, scene):
    """「CV」の行: (level, [(key, params), ...])。GUI は各部を訳して ` · ` でつなぐ。"""
    cv = cv or {}
    phase = launcher.get("phase") if launcher else None
    if phase == "failed":
        return "error", [("cv_failed", {})]
    if cv.get("cv_alive"):
        parts = [("cv_external", {}) if phase == "external" else ("cv_running", {})]
        if launcher is None:
            parts.append(("cv_noauto", {}))
        elif launcher.get("distro"):
            parts.append(("_raw", {"text": launcher["distro"]}))
        cuda = launcher.get("cuda") if launcher else None
        if cuda is True:
            parts.append(("cv_gpu", {}))
        elif cuda is False:
            parts.append(("cv_cpu", {}))
        if cv.get("cv_on"):
            parts.append(("cv_on_prev", {"session": cv["cv_on"]}))
        elif cv.get("cv_busy"):
            parts.append(("cv_busy", {"n": int(cv.get("cv_busy_n") or 0)}))
        level = "warn" if cuda is False else ("busy" if cv.get("cv_busy") or cv.get("cv_on") else "ok")
        return level, parts
    if launcher is None:
        return "warn", [("cv_not_detected", {}), ("cv_noauto", {})]
    if phase == "idle" and not scene:
        return "warn", [("cv_wait_scene", {})]
    if phase == "running" and (launcher.get("run_s") or 0.0) > CV_SILENT_AFTER_S:
        return "error", [("cv_silent", {})]
    return "busy", [("cv_starting", {})]


def session_progress(kf_count, cv):
    """このセッションの 受信・処理済み・採用・待ち の枚数。CV がまだこのセッションを見ていなければ None。

    処理済み（cv_done）は採否を問わず解き直しまで済んだ枚数、採用（cv_ok）は Sim(3) の蓄積に入った枚数。
    待ち = 受信 − 処理済み。これが減らずに増え続けるなら CV が追いついていない（GPU が使えていない等）。
    """
    if not cv or "cv_done" not in cv:
        return None
    done = int(cv.get("cv_done") or 0)
    return {"rx": int(kf_count), "done": done, "ok": int(cv.get("cv_ok") or 0),
            "queue": max(0, int(kf_count) - done)}


def last_solve(hb):
    """セッションの生存信号に載っている直近の解の採否。まだ解いていなければ None。

    返送した解だけでなく**公開ゲートで落とした解とその理由**も出すため（「なぜ確定しないか」が
    コンソールを見なくても分かる）。理由の文（gates.should_publish）は英語で、HUD と同じもの。
    """
    if not hb or hb.get("solve_why") is None:
        return None
    return {"published": bool(hb.get("solve_published")), "why": str(hb.get("solve_why")),
            "inl": int(hb.get("solve_inliers") or 0), "n": int(hb.get("solve_n") or 0)}


# ---- 実験 / デモ・閉じるとき・シーンを変えるとき ----

# キーフレームがこの秒数以内に届いていれば、試行（デモなら再位置合わせ）の途中とみなす。
# Quest の自動送信は 1 秒ごと（KeyframeUploader.autoMinIntervalS）なので、届き続けている間はこれを超えない
RECENT_KF_S = 30.0


def infer_mode(out_dir):
    """保存先の名前から「評価実験 / デモ展示」を決める（start_experiment.bat は received、
    start_demo.bat は received_demo に保存する）。どちらでもなければ None（表示しない）。"""
    name = os.path.basename(os.path.normpath(str(out_dir or ""))).lower()
    if name == "received_demo":
        return "demo"
    if name == "received":
        return "experiment"
    return None


def close_warning(snap, cv):
    """窓を閉じる前に確かめるべきなら (鍵, 差し込み)。そうでなければ None（すぐ閉じてよい）。

    閉じると CV（localize）も止まる。処理中・待ちがある、またはキーフレームが届き続けているときに
    うっかり閉じると、試行（デモなら再位置合わせ）がそこで終わってしまう。
    """
    cv = cv or {}
    prog = session_progress((snap or {}).get("keyframe_count", 0), cv)
    queue = prog["queue"] if prog else 0
    if cv.get("cv_busy") or queue > 0:
        return "close_busy", {"n": max(queue, int(cv.get("cv_busy_n") or 0))}
    age = (snap or {}).get("kf_age")
    if age is not None and age < RECENT_KF_S:
        return "close_receiving", {"age": f"{age:.0f}"}
    return None


def scene_change_warning(snap, current, new):
    """シーンを変える前に確かめるべきなら (鍵, 差し込み)。そうでなければ None。

    localize はシーンが変わると DB を読み直し、そのセッションの途中経過（採用したキーフレームの蓄積）を
    捨てる（→ localize.watch の cv.json の反映）。キーフレームが届いた後に変えると、その試行は
    やり直しになる。まだシーンが決まっていない（CV が動いていない）ときと、同じシーンのときは確かめない。
    """
    if not current or not new or new == current:
        return None
    n = int((snap or {}).get("keyframe_count") or 0)
    return ("scene_busy", {"n": n}) if n > 0 else None


def fmt_num(x, nd):
    """数値を小数 nd 桁で。数でないもの・NaN は「—」（GUI を落とさない）。"""
    try:
        v = float(x)
    except (TypeError, ValueError):
        return "—"
    return "—" if math.isnan(v) else f"{v:.{nd}f}"


def fmt_uptime(s):
    s = int(s)
    h, m, x = s // 3600, (s % 3600) // 60, s % 60
    return f"{h}h {m}m {x}s"


# ---- この PC の IPv4（Quest の Manual IP に入れる値。大学の Wi-Fi 等でビーコンが届かないとき用）----
_ip_cache = {"t": 0.0, "v": []}


def local_ipv4s(ttl_s=15.0):
    """LAN 側の IPv4。既定の経路のアドレスを先頭に、無ければ全アダプタから（ループバック・リンクローカルを除く）。

    UDP の connect は経路を決めるだけでパケットは出ない。インターネットに出られない私設 LAN では
    経路が無いことがあるので、そのときはホスト名から引いた一覧に頼る。
    """
    now = time.time()
    if now - _ip_cache["t"] < ttl_s:
        return list(_ip_cache["v"])
    ips = []
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            s.connect(("10.255.255.255", 1))
            ips.append(s.getsockname()[0])
        finally:
            s.close()
    except OSError:
        pass
    if not ips or ips[0].startswith(("127.", "0.")):
        ips = []
        try:
            for ip in socket.gethostbyname_ex(socket.gethostname())[2]:
                if not ip.startswith(("127.", "169.254.")) and ip not in ips:
                    ips.append(ip)
        except OSError:
            pass
    _ip_cache.update(t=now, v=ips)
    return list(ips)
