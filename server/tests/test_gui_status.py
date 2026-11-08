"""GUI の判定（gui_status）と文言（i18n）、GUI が読む状態の検証。

ここで守りたいこと:
  1. 総合ステータスが、壊れている順に正しい 1 つを出す（CV の起動失敗 → シーン未決定 → CV 未稼働 →
     前のセッションの片付け → Quest 未接続 → 処理中 → CPU 動作 → 準備完了）。
  2. **英語と日本語の文言が同じ鍵・同じ差し込み名を持つ。** 片方だけに足すと、切り替えた瞬間に
     鍵の名前がそのまま出る・差し込みが空になる。
  3. Quest の生存を PING で見る（キーフレームを送っていない間も「接続中」と出る）。
  4. 言語の保存が WSL の設定を消さない（同じ launch_config.json に持つため）。

実行: python -m tests.test_gui_status
"""
import json
import sys
import tempfile
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from tests._check import check, finish  # noqa: E402

from vptserver import cv_launcher as L  # noqa: E402
from vptserver import gui_status as gs  # noqa: E402
from vptserver import i18n  # noqa: E402
from vptserver.cv.heartbeat import HEARTBEAT_NAME  # noqa: E402
from vptserver.events import EventSink  # noqa: E402
from vptserver.liveness import read_session_heartbeat  # noqa: E402
from vptserver.state import ServerState  # noqa: E402

QUEST_OK = {"quest_age": 1.0}
CV_OK = {"cv_alive": True, "cv_busy": False, "cv_done": 0, "cv_ok": 0}
RUNNING = {"phase": "running", "run_s": 30.0, "cuda": True, "distro": "Ubuntu-24.04"}


def test_i18n_tables():
    print("== 文言の表（英語・日本語）==")
    en, ja = i18n.STRINGS["en"], i18n.STRINGS["ja"]
    check(set(en) == set(ja), f"両言語の鍵が一致（英語のみ: {sorted(set(en) - set(ja))} / "
                              f"日本語のみ: {sorted(set(ja) - set(en))}）")
    bad = [k for k in en if k in ja and i18n.placeholders(en[k]) != i18n.placeholders(ja[k])]
    check(not bad, f"両言語の差し込み名が一致（食い違い: {bad}）")
    check(i18n.DEFAULT_LANG == "en", "既定は英語")
    check(i18n.normalize(None) == "en" and i18n.normalize("xx") == "en" and i18n.normalize("ja") == "ja",
          "知らない言語・未指定は英語")
    check(i18n.t("ja", "st_ready") == "準備完了" and i18n.t("en", "st_ready") == "Ready", "言語で引き分ける")
    check(i18n.t("en", "st_processing", n=3) == "Processing 3 keyframe(s)…", "差し込める")
    check(i18n.t("en", "st_processing") == en["st_processing"], "差し込みが足りなくても落ちない")
    check(i18n.t("en", "no_such_key") == "no_such_key", "無い鍵は鍵そのもの（落ちない）")

    # gui_status が返す鍵がすべて表にある（_detail も）
    used = set()
    for snap, cv, la, sc in _status_cases():
        _, key, _ = gs.overall_status(snap, cv, la, sc)
        used |= {key, key + "_detail"}
        _, parts = gs.cv_line(cv, la, sc)
        used |= {k for k, _ in parts if k != "_raw"}
    missing = sorted(k for k in used if k not in en)
    check(not missing, f"判定が返す鍵がすべて文言の表にある（無い: {missing}）")


def _status_cases():
    return [
        (QUEST_OK, CV_OK, {"phase": "failed"}, "scene_data/a"),
        ({}, {}, {"phase": "idle"}, None),
        (QUEST_OK, {"cv_alive": False}, {"phase": "starting"}, "scene_data/a"),
        (QUEST_OK, {"cv_alive": False}, dict(RUNNING, run_s=9999.0), "scene_data/a"),
        (QUEST_OK, {"cv_alive": False}, None, "scene_data/a"),
        (QUEST_OK, dict(CV_OK, cv_on="session_x"), RUNNING, "scene_data/a"),
        ({}, CV_OK, RUNNING, "scene_data/a"),
        (QUEST_OK, dict(CV_OK, cv_busy=True, cv_busy_n=2), RUNNING, "scene_data/a"),
        (QUEST_OK, CV_OK, dict(RUNNING, cuda=False), "scene_data/a"),
        (QUEST_OK, CV_OK, RUNNING, "scene_data/a"),
        (QUEST_OK, CV_OK, None, "scene_data/a"),
        (QUEST_OK, CV_OK, dict(RUNNING, phase="external"), "scene_data/a"),
        (QUEST_OK, {"cv_alive": False}, {"phase": "idle"}, None),
    ]


def test_overall_status():
    print("== 総合ステータス（上から順に最初に当てはまったもの）==")
    st = gs.overall_status
    sc = "scene_data/a"
    check(st(QUEST_OK, CV_OK, {"phase": "failed"}, sc)[:2] == ("error", "st_cv_failed"),
          "CV の起動を諦めたら、他がそろっていても赤で出す")
    check(st(QUEST_OK, CV_OK, RUNNING, None)[:2] == ("warn", "st_need_scene"),
          "シーン未決定なら選ぶよう促す（CV はまだ起動しない）")
    check(st(QUEST_OK, {"cv_alive": False}, {"phase": "starting"}, sc)[:2] == ("busy", "st_cv_starting"),
          "起動を依頼済みで生存信号がまだ無い → 起動中（待てばよい）")
    check(st(QUEST_OK, {"cv_alive": False}, dict(RUNNING, run_s=60.0), sc)[1] == "st_cv_starting",
          "プロセスが動き始めて間もないなら、DB の読み込み中として待つ")
    check(st(QUEST_OK, {"cv_alive": False}, dict(RUNNING, run_s=gs.CV_SILENT_AFTER_S + 1), sc)[:2]
          == ("error", "st_cv_silent"), "長く動いているのに生存信号が無い → 異常")
    check(st(QUEST_OK, {"cv_alive": False}, None, sc)[:2] == ("warn", "st_cv_manual"),
          "--no-cv で CV が見つからない → 手で起動するよう促す")
    lv, key, params = st(QUEST_OK, dict(CV_OK, cv_on="session_x"), RUNNING, sc)
    check((lv, key, params) == ("busy", "st_cv_other", {"session": "session_x"}),
          "CV が前のセッションを片付けている → 待てばよい（RESET 直後）")
    check(st({}, CV_OK, RUNNING, sc)[:2] == ("warn", "st_wait_quest"), "Quest から何も届いていない → 接続待ち")
    check(st({"quest_age": gs.QUEST_STALE_S + 0.5}, CV_OK, RUNNING, sc)[1] == "st_wait_quest",
          "PING が 3 回続けて途切れたら接続待ちに戻る")
    check(st(QUEST_OK, dict(CV_OK, cv_busy=True, cv_busy_n=2), RUNNING, sc) == ("busy", "st_processing", {"n": 2}),
          "処理中は枚数つきで青")
    check(st(QUEST_OK, CV_OK, dict(RUNNING, cuda=False), sc)[:2] == ("warn", "st_ready_cpu"),
          "GPU が使えないなら、準備完了でも琥珀で知らせる")
    check(st(QUEST_OK, CV_OK, RUNNING, sc)[:2] == ("ok", "st_ready"), "すべてそろえば準備完了")
    check(st(QUEST_OK, CV_OK, None, sc)[:2] == ("ok", "st_ready"), "--no-cv でも手で起動した CV が生きていれば準備完了")
    check(st(QUEST_OK, CV_OK, dict(RUNNING, cuda=None), sc)[:2] == ("ok", "st_ready"),
          "CUDA が未確認（行をまだ見ていない）なら警告しない")


def test_cv_line():
    print("== CV の行 ==")
    sc = "scene_data/a"
    lv, parts = gs.cv_line(CV_OK, RUNNING, sc)
    keys = [k for k, _ in parts]
    check(lv == "ok" and keys == ["cv_running", "_raw", "cv_gpu"] and parts[1][1]["text"] == "Ubuntu-24.04",
          "動作中・ディストリ・GPU")
    lv, parts = gs.cv_line(dict(CV_OK, cv_busy=True, cv_busy_n=3), dict(RUNNING, cuda=False), sc)
    check(lv == "warn" and ("cv_cpu", {}) in parts and ("cv_busy", {"n": 3}) in parts,
          "CPU 動作は琥珀・処理中の枚数も出す")
    lv, parts = gs.cv_line(CV_OK, None, sc)
    check(("cv_noauto", {}) in parts and parts[0][0] == "cv_running", "--no-cv で手で起動したもの")
    lv, parts = gs.cv_line(CV_OK, dict(RUNNING, phase="external"), sc)
    check(parts[0][0] == "cv_external", "別の localize を使っている")
    check(gs.cv_line({"cv_alive": False}, {"phase": "failed"}, sc) == ("error", [("cv_failed", {})]), "起動失敗")
    check(gs.cv_line({"cv_alive": False}, {"phase": "idle"}, None)[1] == [("cv_wait_scene", {})], "シーン待ち")
    check(gs.cv_line({"cv_alive": False}, {"phase": "starting"}, sc)[1] == [("cv_starting", {})], "起動中")
    check(gs.cv_line({"cv_alive": False}, None, sc)[1][0][0] == "cv_not_detected", "--no-cv で未検出")


def test_progress_and_solve():
    print("== このセッションの枚数・直近の解 ==")
    check(gs.session_progress(5, {"cv_alive": True}) is None,
          "CV がこのセッションを見ていない（枚数の無い生存信号）なら枚数を出さない")
    check(gs.session_progress(12, {"cv_done": 9, "cv_ok": 7}) == {"rx": 12, "done": 9, "ok": 7, "queue": 3},
          "待ち = 受信 − 処理済み")
    check(gs.session_progress(3, {"cv_done": 5, "cv_ok": 5})["queue"] == 0, "待ちは負にならない")
    check(gs.last_solve(None) is None and gs.last_solve({"busy": False}) is None, "まだ解いていなければ None")
    ls = gs.last_solve({"solve_why": "rmse=0.0912m(>0.08m)", "solve_published": False,
                        "solve_inliers": 18, "solve_n": 24})
    check(ls == {"published": False, "why": "rmse=0.0912m(>0.08m)", "inl": 18, "n": 24},
          "ゲートで落とした解も理由つきで出す")
    check(gs.fmt_num(None, 2) == "—" and gs.fmt_num(float("nan"), 2) == "—" and gs.fmt_num(0.12345, 3) == "0.123",
          "数でないものはダッシュで出す")
    check(gs.fmt_uptime(3725) == "1h 2m 5s", "稼働時間")

    with tempfile.TemporaryDirectory() as d:
        check(read_session_heartbeat(d) is None, "生存信号が無ければ None")
        (Path(d) / HEARTBEAT_NAME).write_text(json.dumps({"solve_why": "ok", "done": 3}), encoding="utf-8")
        check(read_session_heartbeat(d) == {"solve_why": "ok", "done": 3}, "生存信号の中身をそのまま読む")
        (Path(d) / HEARTBEAT_NAME).write_text("{壊れ", encoding="utf-8")
        check(read_session_heartbeat(d) is None, "壊れていても落ちない")


def test_state_ping():
    print("== Quest の生存（PING）==")
    s = ServerState()
    snap = s.snapshot()
    check(snap["quest_ip"] is None and snap["quest_age"] is None and not gs.quest_alive(snap), "最初は未接続")
    s.on_ping("192.168.0.23")
    snap = s.snapshot()
    check(snap["quest_ip"] == "192.168.0.23" and gs.quest_alive(snap),
          "キーフレームが無くても PING だけで接続中になる（試行前・デモの表示中）")
    check(snap["keyframe_count"] == 0 and snap["kf_age"] is None, "PING は受信枚数に数えない")
    n_ev = len(snap["events"])
    check(n_ev == 1 and snap["events"][-1]["en"] == "Quest connected: 192.168.0.23"
          and snap["events"][-1]["level"] == "ok", "初めての PING でイベント欄に接続を出す（英語も持つ・緑）")
    s.on_ping("192.168.0.23")
    check(len(s.snapshot()["events"]) == n_ev, "続く PING ではイベント欄を埋めない")
    s.last_ping_time = time.time() - 30
    check(not gs.quest_alive(s.snapshot()), "PING が途切れたら接続中でなくなる")
    s.on_ping("192.168.0.23")
    check(len(s.snapshot()["events"]) == n_ev + 1, "途切れてから戻ったら接続をもう一度出す")
    s.event("日本語だけ")
    e = s.snapshot()["events"][-1]
    check(e["msg"] == "日本語だけ" and e["en"] is None and e["level"] == "info",
          "英語の無い行も持てる（GUI は日本語のまま出す）。重要度の既定は info")


def test_state_events():
    print("== イベント欄の行（順序・通し番号・重要度・キーフレームのまとめ）==")
    s = ServerState()
    s.event("a")
    s.event("b", level="warn")
    s.event("c", level="no_such_level")
    ev = s.snapshot()["events"]
    check([e["msg"] for e in ev] == ["a", "b", "c"], "古い順に並ぶ（GUI は増えた分を下へ書き足す）")
    check([e["id"] for e in ev] == [1, 2, 3] and all(e["rev"] == 0 for e in ev), "通し番号が付く")
    check(ev[2]["level"] == "info", "知らない重要度は info")

    for i in range(20):
        s.on_keyframe(i + 1, "192.168.0.23")
    snap = s.snapshot()
    kf = snap["events_all"][-1]
    check(len(snap["events"]) == 3, "キーフレームの行は「重要」に入れない（枚数は「このセッション」に出る）")
    check(len(snap["events_all"]) == 4 and kf["level"] == "debug"
          and kf["en"] == "Keyframes received: 20 (latest req=20 from 192.168.0.23)",
          "続けて届いたキーフレームは「すべて」の 1 行にまとめる")
    check(kf["id"] == 4 and kf["rev"] == 19, "まとめた行は同じ id のまま rev が上がる（GUI はその行だけ書き直す）")
    s.event("RESET → 新セッション", en="RESET -> new session", level="mark")
    s.on_keyframe(1, "192.168.0.23")
    al = s.snapshot()["events_all"]
    check(len(al) == 6 and al[-2]["en"] == "RESET -> new session" and al[-1]["rev"] == 0,
          "間に別の行が入ったら、キーフレームの行を新しく始める（RESET の行を書き換えない）")

    s2 = ServerState()
    for i in range(1000):
        s2.event(f"x{i}", level="debug")
    s2.event("重要", level="error")
    snap = s2.snapshot()
    check(snap["events"][-1]["msg"] == "重要" and len(snap["events"]) == 1,
          "細かい行がいくら流れても、重要な行は「重要」に残る")
    check(len(snap["events_all"]) <= 800, "「すべて」は上限で古い行から消える")


def test_describe_both_languages():
    print("== 実験イベントの 1 行説明（英語・日本語）==")
    sink = EventSink(Path(tempfile.gettempdir()) / "vpt_test_events_unused.jsonl")
    samples = [
        {"type": "cp_survey", "name": "CP_01", "survey": 1},
        {"type": "cp_survey_start", "points": 6},
        {"type": "cp_survey_done", "survey": 1, "points": 6, "span_m": 3.2},
        {"type": "cp_survey_cancel", "done": 2},
        {"type": "cp_survey_invalidated", "survey": 1, "why": "x"},
        {"type": "cp_model_edit", "action": "start", "points": 6},
        {"type": "cp_model_edit", "action": "end"},
        {"type": "cp_model_edit", "action": "move", "name": "VCP_01", "moved_m": 0.05},
        {"type": "cp_model_edit_revert", "name": "VCP_01"},
        {"type": "cp_model_point", "name": "VCP_01"},
        {"type": "cp_model_edit_done", "phase": "begin", "points": 6},
        {"type": "cp_model_edit_done", "phase": "end", "edited": 2, "points": 6, "model": "m"},
        {"type": "trial_start", "label": "T03"},
        {"type": "trial_end", "label": "T03", "verdict": "ok"},
        {"type": "tracking_origin_changed", "cause": "recenter"},
        {"type": "unknown_type"},
    ]
    ok = True
    for d in samples:
        ja, en = sink.describe(d), sink.describe(d, lang="en")
        if not ja or not en:
            ok = False
        if d["type"] != "unknown_type" and (en == ja or any(ord(c) > 0x3000 for c in en)):
            ok = False
            print("     英語になっていない:", d["type"], en)
    check(ok, "すべての型で英語と日本語の説明が出る（英語に日本語が混ざらない）")
    check(sink.describe({"type": "trial_start", "label": "T03"}) == "試行開始 T03", "既定は日本語（コンソール）")


def test_mode_and_confirmations():
    print("== 実験 / デモ・閉じるとき・シーンを変えるときの確認 ==")
    check(gs.infer_mode("received") == "experiment" and gs.infer_mode("received_demo") == "demo",
          "保存先の名前から実験 / デモを決める（start_*.bat の保存先）")
    check(gs.infer_mode("C:\\x\\server\\received_demo\\") == "demo" and gs.infer_mode("other") is None
          and gs.infer_mode(None) is None, "絶対パス・末尾の区切りでもよい。どちらでもなければ出さない")

    idle = {"keyframe_count": 0, "kf_age": None}
    check(gs.close_warning(idle, {"cv_alive": True, "cv_done": 0, "cv_ok": 0}) is None,
          "何もしていなければ確かめずに閉じる")
    check(gs.close_warning({"keyframe_count": 12, "kf_age": 300.0}, {"cv_done": 12, "cv_ok": 10}) is None,
          "試行が終わって処理も済んでいれば確かめない")
    check(gs.close_warning({"keyframe_count": 12, "kf_age": 300.0}, {"cv_done": 9, "cv_ok": 8})
          == ("close_busy", {"n": 3}), "処理待ちが残っていれば、その枚数で確かめる")
    check(gs.close_warning(idle, {"cv_busy": True, "cv_busy_n": 2})[0] == "close_busy", "CV が処理中なら確かめる")
    check(gs.close_warning({"keyframe_count": 5, "kf_age": 3.0}, {"cv_done": 5, "cv_ok": 5})
          == ("close_receiving", {"age": "3"}), "キーフレームが届き続けている（試行の途中）なら確かめる")

    snap = {"keyframe_count": 4}
    check(gs.scene_change_warning(snap, "scene_data/a", "scene_data/b") == ("scene_busy", {"n": 4}),
          "キーフレームが届いた後のシーン変更は確かめる")
    check(gs.scene_change_warning({"keyframe_count": 0}, "scene_data/a", "scene_data/b") is None,
          "まだ何も届いていなければ確かめない")
    check(gs.scene_change_warning(snap, None, "scene_data/b") is None,
          "初めてシーンを選ぶとき（CV がまだ動いていない）は確かめない")
    check(gs.scene_change_warning(snap, "scene_data/a", "scene_data/a") is None, "同じシーンなら確かめない")


def test_cv_line_levels():
    print("== localize の出力の重要度 ==")
    lv = L.cv_event_level
    check(lv("[localize] WARNING: Sim(3) 非公開（ゲート不合格）: rmse=0.0912m(>0.08m)") == "warn",
          "ゲート不合格（WARNING）は琥珀で「重要」に出る")
    check(lv("[localize] ERROR: 終盤のキーフレーム残差が 1.20 m あります。") == "error",
          "「この試行は使えない」（ERROR）は赤")
    check(lv("Traceback (most recent call last):") == "error" and lv("RuntimeError: CUDA out of memory") == "error",
          "Python の例外は赤")
    check(lv("[2026/11/08 16:00:00 hloc WARNING] something") == "warn", "hloc の警告は琥珀")
    check(lv("[localize] WARNING: CUDA: NOT available — CV は CPU で動きます") == "warn", "GPU が無いのは琥珀")
    check(lv("[localize] CUDA: available (RTX 4070)") == "ok" and lv("[localize] watch 開始: base=x") == "ok",
          "準備ができた行は緑")
    check(lv("[localize] DB ロード: scene=studio images=120 points3D=50000") == "info", "DB のロードは「重要」")
    check(lv("[localize] KF棄却: req_000003 inliers=12") == "debug"
          and lv("[2026/11/08 16:00:00 hloc INFO] Extracting local features") == "debug",
          "キーフレームごとの採否・hloc の進み具合は「すべて」でだけ見える")
    check(lv("[localize] セッション追従: session_x （結果→ y）") == "info", "セッションの追従は「重要」")
    check(L.gui_line("10%|#   |\r50%|##  |\r100%|####|") == "100%|####|", "tqdm の進み具合は最後の表示だけ残す")
    long = "x" * 1000
    check(len(L.gui_line(long)) == L.GUI_LINE_MAX and L.gui_line(long).startswith("…"), "長すぎる行は末尾を残して切る")


def test_launcher_status_and_config():
    print("== CV 起動の状態・言語の保存 ==")
    check(L.cuda_from_line("[localize] CUDA: available (RTX 4070, torch 2.6.0, CUDA 12.4)") is True, "GPU あり")
    check(L.cuda_from_line("[localize] CUDA: NOT available — CV は CPU で動きます") is False, "GPU なし")
    check(L.cuda_from_line("[localize] watch 開始") is None, "関係ない行")

    class FakeProc:
        def poll(self):
            return 0

    with tempfile.TemporaryDirectory() as d:
        events = []
        cv = L.CvLauncher(d, Path(d) / "received", emit=lambda m: None,
                          on_event=lambda m, en=None, level="info": events.append((m, en, level)))
        check(cv.status()["phase"] == "idle", "依頼前（シーン待ち）は idle")
        cv._wanted = True
        check(cv.status()["phase"] == "starting", "依頼済みで動いていなければ starting")
        p = FakeProc()
        cv._proc = p
        cv._started_t = time.time()
        cv._on_exit(p, L.EXIT_NO_VENV)
        st = cv.status()
        check(st["phase"] == "failed" and "setup_windows.bat" in st["problem_en"]
              and "setup_windows.bat" in st["problem"], "諦めたら failed・理由は英語と日本語の両方")
        check(events and events[-1][1] and events[-1][1].startswith("[cv] ") and events[-1][2] == "error",
              "イベント欄の行に英語も付く・起動を諦めたら赤")
        cv._watchdog = object()          # 見張りのスレッドは起こさない（retry が start を呼ぶため）
        cv.retry()
        st = cv.status()
        check(not cv.gave_up and st["phase"] == "starting" and st["problem"] == "" and cv._server_wsl is None,
              "再試行で諦めを解き、ディストリとパスも決め直す")

        L.save_config(d, {"wsl_distro": "Ubuntu-24.04", "cv_venv": "~/vptcv"})
        L.save_config(d, {"gui_lang": "ja"})
        check(L.load_config(d) == {"wsl_distro": "Ubuntu-24.04", "cv_venv": "~/vptcv", "gui_lang": "ja"},
              "言語を保存しても WSL の設定が消えない")
        L.save_config(d, {"wsl_distro": "Ubuntu-22.04", "cv_venv": "~/vptcv"})
        check(L.load_config(d).get("gui_lang") == "ja", "WSL の設定を保存しても言語が消えない（setup_windows / cv_launcher）")


if __name__ == "__main__":
    test_i18n_tables()
    test_overall_status()
    test_cv_line()
    test_progress_and_solve()
    test_state_ping()
    test_state_events()
    test_mode_and_confirmations()
    test_cv_line_levels()
    test_describe_both_languages()
    test_launcher_status_and_config()
    finish("ALL GUI STATUS TESTS PASSED")
