"""GUI の文言（英語・日本語）。既定は英語、GUI の右上で日本語に切り替えられる。

**なぜ英語が既定か。** リポジトリは公開を前提にしていて、README も英語が主。研究室の中では
日本語を選べばよい。選んだ言語は launch_config.json の `gui_lang` に残る（run.py の `--lang` が優先）。

ここにあるのは GUI に固定で出る文言だけ。イベント欄の行は、起きた場所で英語と日本語の両方を
作って ServerState.event(msg, en=...) に渡す（その場の数値を埋め込むため）。CV（localize）の
出力そのもの（`[cv] ...`）はログなので訳さない。

両方の言語に同じ鍵・同じ差し込み名（{n} など）があることを tests/test_gui_status.py が検査する。
"""
import string

LANGS = ("en", "ja")
DEFAULT_LANG = "en"
LANG_LABELS = {"en": "English", "ja": "日本語"}

STRINGS = {
    "en": {
        # ---- 一番上の総合ステータス（gui_status.overall_status の鍵）----
        "st_cv_failed": "CV could not start",
        "st_cv_failed_detail": "{problem}",
        "st_need_scene": "Select a scene",
        "st_need_scene_detail": "The CV worker starts once a scene is chosen in Settings below.",
        "st_cv_starting": "Starting the CV worker…",
        "st_cv_starting_detail": "The first start can take a minute (WSL boot and scene DB load).",
        "st_cv_silent": "The CV worker is running but not reporting",
        "st_cv_silent_detail": "Check the log: {log}",
        "st_cv_manual": "CV worker not detected (auto-start is off)",
        "st_cv_manual_detail": "Started with --no-cv. Run localize in WSL2 with --watch {out}.",
        "st_cv_other": "The CV worker is finishing the previous session",
        "st_cv_other_detail": "It moves to this session when done ({session}).",
        "st_wait_quest": "Waiting for the Quest",
        "st_wait_quest_detail": "Start the app on the Quest. If it never connects, check the network "
                                "(OperationGuide §6). This PC: {ips}",
        "st_processing": "Processing {n} keyframe(s)…",
        "st_processing_detail": "",
        "st_ready": "Ready",
        "st_ready_detail": "The Quest and the CV worker are both connected.",
        "st_ready_cpu": "Ready — but the CV worker runs on the CPU (slow)",
        "st_ready_cpu_detail": "Install the latest NVIDIA driver on Windows and run setup_windows.bat again.",
        "btn_retry": "Retry",
        "btn_log": "Open log",

        # ---- 見出し ----
        "sec_status": "STATUS",
        "sec_session": "THIS SESSION",
        "sec_settings": "SETTINGS (CV) — written to cv.json immediately",
        "sec_events": "EVENTS",

        # ---- 状態の行 ----
        "row_quest": "Quest",
        "row_cv": "CV worker",
        "row_session": "Session",
        "quest_ok": "{ip} · ping {age} s ago",
        "quest_stale": "{ip} · silent for {age} s",
        "quest_none": "not connected",
        "cv_running": "running",
        "cv_external": "running (started outside this window)",
        "cv_starting": "starting…",
        "cv_silent": "running, no heartbeat",
        "cv_failed": "could not start",
        "cv_wait_scene": "waiting for a scene",
        "cv_not_detected": "not detected",
        "cv_noauto": "auto-start off",
        "cv_gpu": "GPU",
        "cv_cpu": "CPU only (slow)",
        "cv_busy": "processing {n}",
        "cv_on_prev": "finishing {session}",

        # ---- このセッション ----
        "counts": "Received {rx}  ·  Processed {done}  ·  Accepted {ok}  ·  Queue {queue}",
        "counts_rx_only": "Received {rx}  ·  not processed by the CV worker yet",
        "row_last_solve": "Last solve",
        "solve_pub": "✓ sent ({inl}/{n}) — {why}",
        "solve_rej": "✕ rejected ({inl}/{n}) — {why}",
        "row_last_sent": "Last sent to Quest",
        "sent_val": "|scale| {scale} · inliers {inl}/{n} · RMSE {rmse} m · {age} s ago",

        # ---- 設定 ----
        "lbl_scene": "Scene",
        "lbl_every": "every (re-solve every N keyframes)",
        "lbl_min": "min (keyframes before the first solve)",
        "scene_placeholder": "(select a scene)",
        "scene_none": "(no scene with a built DB)",

        # ---- 下の 1 行 ----
        "footer": "Saved to {out} · this PC {ips} · UDP data {data} · beacon {beacon} · result {result} "
                  "· up {uptime}",
        "off": "off",

        # ---- 実験 / デモ・窓の題名 ----
        "mode_experiment": "Experiment",
        "mode_demo": "Demo",
        "win_title": "VPTAligned3DGS server — {mode}",
        "win_title_plain": "VPTAligned3DGS server",

        # ---- イベント欄 ----
        "ev_important": "Important",
        "ev_all": "All",
        "btn_folder": "Open folder",
        "btn_cvlog": "Open cv.log",

        # ---- 確かめるダイアログ ----
        "close_title": "Close the server?",
        "close_busy": "The CV worker is still processing {n} keyframe(s).",
        "close_receiving": "The Quest sent a keyframe {age} s ago — a trial or a re-alignment may be in progress.",
        "close_question": "Closing this window also stops the CV worker. Close anyway?",
        "scene_title": "Change the scene?",
        "scene_busy": "This session already has {n} keyframe(s). Changing the scene makes the CV worker reload "
                      "the scene DB and discard its progress for this session.",
        "scene_question": "If you can, start over on the Quest first (Start trial / Re-align starts a new "
                          "session). Change the scene anyway?",
    },
    "ja": {
        "st_cv_failed": "CV を起動できません",
        "st_cv_failed_detail": "{problem}",
        "st_need_scene": "シーンを選んでください",
        "st_need_scene_detail": "下の設定でシーンを選ぶと CV が起動します。",
        "st_cv_starting": "CV を起動しています…",
        "st_cv_starting_detail": "初回は WSL の起動とシーン DB の読み込みで 1 分ほどかかります。",
        "st_cv_silent": "CV は動いていますが、応答がありません",
        "st_cv_silent_detail": "ログを確認してください: {log}",
        "st_cv_manual": "CV が見つかりません（自動起動 OFF）",
        "st_cv_manual_detail": "--no-cv で起動しています。WSL2 で localize を --watch {out} で起動してください。",
        "st_cv_other": "CV が前のセッションを処理しています",
        "st_cv_other_detail": "終わるとこのセッションへ移ります（{session}）。",
        "st_wait_quest": "Quest の接続を待っています",
        "st_wait_quest_detail": "Quest でアプリを起動してください。つながらなければネットワークを確認"
                                "（OperationGuide §6）。この PC: {ips}",
        "st_processing": "{n} 枚を処理中…",
        "st_processing_detail": "",
        "st_ready": "準備完了",
        "st_ready_detail": "Quest と CV の両方とつながっています。",
        "st_ready_cpu": "準備完了 — ただし CV が CPU で動いています（遅い）",
        "st_ready_cpu_detail": "Windows に NVIDIA の最新ドライバを入れ、setup_windows.bat を再実行してください。",
        "btn_retry": "再試行",
        "btn_log": "ログを開く",

        "sec_status": "状態",
        "sec_session": "このセッション",
        "sec_settings": "設定 (CV) — 変更は即 cv.json に反映",
        "sec_events": "イベント",

        "row_quest": "Quest",
        "row_cv": "CV",
        "row_session": "セッション",
        "quest_ok": "{ip} · PING {age} 秒前",
        "quest_stale": "{ip} · {age} 秒応答なし",
        "quest_none": "未接続",
        "cv_running": "動作中",
        "cv_external": "動作中（このウインドウの外で起動したもの）",
        "cv_starting": "起動中…",
        "cv_silent": "動作中・生存信号なし",
        "cv_failed": "起動できません",
        "cv_wait_scene": "シーン待ち",
        "cv_not_detected": "未検出",
        "cv_noauto": "自動起動 OFF",
        "cv_gpu": "GPU",
        "cv_cpu": "CPU のみ（遅い）",
        "cv_busy": "{n} 枚処理中",
        "cv_on_prev": "{session} を処理中",

        "counts": "受信 {rx}  ·  処理済 {done}  ·  採用 {ok}  ·  待ち {queue}",
        "counts_rx_only": "受信 {rx}  ·  CV はまだこのセッションを処理していません",
        "row_last_solve": "直近の解",
        "solve_pub": "✓ 返送 ({inl}/{n}) — {why}",
        "solve_rej": "✕ 非公開 ({inl}/{n}) — {why}",
        "row_last_sent": "Quest へ返送",
        "sent_val": "|scale| {scale} · inliers {inl}/{n} · RMSE {rmse} m · {age} 秒前",

        "lbl_scene": "シーン",
        "lbl_every": "every（枚ごと再計算）",
        "lbl_min": "min（初回の最小枚数）",
        "scene_placeholder": "（シーンを選択）",
        "scene_none": "（DB のあるシーンなし）",

        "footer": "保存先 {out} · この PC {ips} · UDP データ {data} · ビーコン {beacon} · 返送 {result} "
                  "· 稼働 {uptime}",
        "off": "OFF",

        "mode_experiment": "評価実験",
        "mode_demo": "デモ展示",
        "win_title": "VPTAligned3DGS サーバ — {mode}",
        "win_title_plain": "VPTAligned3DGS サーバ",

        "ev_important": "重要",
        "ev_all": "すべて",
        "btn_folder": "フォルダを開く",
        "btn_cvlog": "cv.log を開く",

        "close_title": "サーバを終了しますか？",
        "close_busy": "CV がまだ {n} 枚を処理しています。",
        "close_receiving": "Quest から {age} 秒前にキーフレームが届いています。試行か再位置合わせの途中かもしれません。",
        "close_question": "このウインドウを閉じると CV も止まります。終了しますか？",
        "scene_title": "シーンを変更しますか？",
        "scene_busy": "このセッションには既に {n} 枚のキーフレームがあります。シーンを変えると CV がシーン DB を"
                      "読み直し、このセッションの途中経過を捨てます。",
        "scene_question": "できれば先に Quest でやり直してください（試行の開始・Re-align で新しいセッションになります）。"
                          "それでもシーンを変更しますか？",
    },
}


def normalize(lang):
    """知らない言語・未指定は既定（英語）にする。"""
    return lang if lang in LANGS else DEFAULT_LANG


def t(lang, key, **kw):
    """文言を引いて差し込む。鍵が無ければ英語 → 鍵そのもの。差し込みに失敗しても落とさない（GUI を止めない）。"""
    s = STRINGS.get(normalize(lang), {}).get(key)
    if s is None:
        s = STRINGS[DEFAULT_LANG].get(key, key)
    try:
        return s.format(**kw)
    except (KeyError, IndexError, ValueError):
        return s


def placeholders(s):
    """文言の差し込み名の集合（テスト用: 両言語で一致させる）。"""
    return {f for _, f, _, _ in string.Formatter().parse(s) if f}
