"""run.py 用ネイティブ・ダッシュボード（customtkinter）。

依存: pip install customtkinter（Windows の run.py 用 venv）。
未インストールなら run.py はヘッドレスにフォールバックする（available() で判定）。

通信状態を 1 秒ごとに描画: Quest 接続・受信キーフレーム数・ポート/ビーコン・
最新 Sim(3)（scale/inliers/RMSE/返送経過）・イベントログ。
シンプル＆スタイリッシュなダークUI。
"""

# パレット（GitHub Dark 風）
BG, CARD, FG, MUT = "#0e1116", "#161b22", "#e6edf3", "#8b949e"
ACCENT, BLUE, LINE = "#3fb950", "#58a6ff", "#262d36"


def available() -> bool:
    try:
        import customtkinter  # noqa: F401
        return True
    except Exception:
        return False


def _fmt_uptime(s: int) -> str:
    h, m, x = s // 3600, (s % 3600) // 60, s % 60
    return f"{h}h {m}m {x}s"


def run_dashboard(state, on_close=None, config=None, scenes=None, on_config=None):
    """ブロッキングで GUI を実行（メインスレッドで呼ぶこと）。state は ServerState（snapshot() を持つ）。

    config:    初期 CV 設定 dict {scene, every, min}
    scenes:    選択肢にする scene 一覧（list[str]）
    on_config: 設定変更時に呼ばれる callback(cfg dict)。run.py が cv.json へ即書き出す。
    """
    import customtkinter as ctk

    ctk.set_appearance_mode("dark")
    app = ctk.CTk()
    app.title("VPTAligned3DGS — Offload Server")
    app.geometry("560x720")
    app.minsize(480, 600)
    app.configure(fg_color=BG)

    f_small = ctk.CTkFont(size=12)
    f_lbl   = ctk.CTkFont(size=12)
    f_mono  = ctk.CTkFont(family="Cascadia Code", size=13)
    f_big   = ctk.CTkFont(size=26, weight="bold")
    f_h1    = ctk.CTkFont(size=15, weight="bold")
    f_cap   = ctk.CTkFont(size=11, weight="bold")

    # ---- ヘッダ ----
    header = ctk.CTkFrame(app, fg_color="transparent")
    header.pack(fill="x", padx=18, pady=(16, 6))
    dot = ctk.CTkLabel(header, text="", width=14, height=14, corner_radius=7, fg_color=MUT)
    dot.pack(side="left", padx=(2, 10))
    ctk.CTkLabel(header, text="VPTAligned3DGS  Offload Server", font=f_h1, text_color=FG).pack(side="left")
    uptime_lbl = ctk.CTkLabel(header, text="—", font=f_small, text_color=MUT)
    uptime_lbl.pack(side="right", padx=4)

    def card(title):
        f = ctk.CTkFrame(app, fg_color=CARD, corner_radius=14)
        f.pack(fill="x", padx=18, pady=7)
        ctk.CTkLabel(f, text=title, font=f_cap, text_color=MUT).pack(anchor="w", padx=16, pady=(12, 2))
        return f

    def row(parent, label):
        r = ctk.CTkFrame(parent, fg_color="transparent")
        r.pack(fill="x", padx=16, pady=2)
        ctk.CTkLabel(r, text=label, font=f_lbl, text_color=MUT).pack(side="left")
        v = ctk.CTkLabel(r, text="—", font=f_mono, text_color=FG)
        v.pack(side="right")
        return v

    def big(parent):
        v = ctk.CTkLabel(parent, text="—", font=f_big, text_color=FG)
        v.pack(anchor="w", padx=16, pady=(0, 4))
        return v

    # ---- Quest 接続 ----
    c_q = card("QUEST 接続")
    quest_val = big(c_q)
    kfage_val = row(c_q, "最終キーフレーム")
    ctk.CTkFrame(c_q, fg_color="transparent", height=6).pack()

    # ---- 受信 ----
    c_k = card("受信キーフレーム")
    kfcount_val = big(c_k)
    session_val = row(c_k, "session")
    ports_val  = row(c_k, "data / beacon / result")
    beacon_val = row(c_k, "ビーコン")
    ctk.CTkFrame(c_k, fg_color="transparent", height=6).pack()

    # ---- Sim(3) ----
    c_r = card("最新の位置合わせ (Sim3)")
    scale_val = row(c_r, "scale")
    inl_val   = row(c_r, "inliers")
    rmse_val  = row(c_r, "RMSE")
    rage_val  = row(c_r, "返送")
    ctk.CTkFrame(c_r, fg_color="transparent", height=6).pack()

    # ---- 設定（編集可・変更は即 cv.json へ）----
    if on_config is not None:
        cfg0 = config or {}
        scene_vals = [str(s) for s in (scenes or [])]
        c_s = card("設定 (CV) — 変更は即 cv.json に反映")

        def emit(*_):
            sc = scene_menu.get()
            try:
                on_config({"scene": (None if sc in ("(なし)", "") else sc),
                           "every": int(every_menu.get()), "min": int(min_menu.get())})
            except ValueError:
                pass

        def menu_row(label, values, init):
            r = ctk.CTkFrame(c_s, fg_color="transparent")
            r.pack(fill="x", padx=16, pady=3)
            ctk.CTkLabel(r, text=label, font=f_lbl, text_color=MUT).pack(side="left")
            mm = ctk.CTkOptionMenu(r, values=[str(v) for v in values], command=emit,
                                   width=210, fg_color=BG, button_color=LINE,
                                   dropdown_fg_color=CARD)
            mm.set(str(init))
            mm.pack(side="right")
            return mm

        scene_init = cfg0.get("scene") or (scene_vals[0] if scene_vals else "(なし)")
        if scene_init not in scene_vals:
            scene_vals = [scene_init] + scene_vals
        scene_menu = menu_row("scene", scene_vals or ["(なし)"], scene_init)
        every_menu = menu_row("every（枚ごと再計算）", [1, 2, 3, 4, 5, 8, 10], cfg0.get("every", 3))
        min_menu   = menu_row("min（初回の最小枚数）", [2, 3, 4, 5, 6, 8], cfg0.get("min", 3))
        ctk.CTkFrame(c_s, fg_color="transparent", height=6).pack()

    # ---- イベントログ ----
    c_l = card("イベント")
    log_box = ctk.CTkTextbox(c_l, fg_color=BG, text_color=FG, font=f_mono,
                             corner_radius=8, border_width=0, activate_scrollbars=True)
    log_box.pack(fill="both", expand=True, padx=12, pady=(2, 12))
    log_box.configure(state="disabled")
    c_l.pack_configure(fill="both", expand=True)

    def refresh():
        s = state.snapshot()
        connected = bool(s["quest_ip"]) and s["kf_age"] is not None and s["kf_age"] < 15
        dot.configure(fg_color=ACCENT if connected else MUT)
        uptime_lbl.configure(text="uptime " + _fmt_uptime(s["uptime"]))

        quest_val.configure(text=s["quest_ip"] or "未接続",
                            text_color=FG if s["quest_ip"] else MUT)
        kfage_val.configure(
            text="—" if s["kf_age"] is None else f"{s['kf_age']}s 前",
            text_color=ACCENT if (s["kf_age"] is not None and s["kf_age"] < 15) else MUT)

        kfcount_val.configure(text=str(s["keyframe_count"]))
        session_val.configure(text=s.get("session") or "—")
        p = s["ports"] or {}
        ports_val.configure(text=f"{p.get('data','-')} / {p.get('beacon') or 'off'} / {p.get('result') or 'off'}")
        beacon_val.configure(text="ON" if s["beacon_on"] else "OFF",
                             text_color=ACCENT if s["beacon_on"] else MUT)

        r = s["last_result"]
        if r:
            scale_val.configure(text=f"{r['sigma']:+.4f}" + ("  (鏡像)" if r["sigma"] < 0 else ""))
            inl_val.configure(text=f"{r['inliers']}/{r['n']}")
            rmse_val.configure(text=f"{r['rmse']:.4f} m")
            rage_val.configure(
                text="—" if s["result_age"] is None else f"{s['result_age']}s 前",
                text_color=ACCENT if (s["result_age"] is not None and s["result_age"] < 30) else MUT)

        log_box.configure(state="normal")
        log_box.delete("1.0", "end")
        for e in s["events"]:
            log_box.insert("end", f"{e['t']}  {e['msg']}\n")
        log_box.configure(state="disabled")

        app.after(1000, refresh)

    def _close():
        try:
            if on_close:
                on_close()
        finally:
            app.destroy()

    app.protocol("WM_DELETE_WINDOW", _close)
    refresh()
    app.mainloop()
