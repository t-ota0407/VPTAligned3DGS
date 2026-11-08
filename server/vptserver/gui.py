"""run.py 用ネイティブ・ダッシュボード（customtkinter）。

依存: pip install customtkinter（Windows の run.py 用 venv）。
未インストールなら run.py はヘッドレスにフォールバックする（available() で判定）。

**PC の前にいる人が、GUI だけで「準備できたか・止まっていないか・なぜ確定しないか・次に何をするか」を
判断できるようにする。** 以前は通信の数字を並べるだけで、CV の状態や確定しない理由はコンソールの
`[cv]` の行にしか出ず、Quest の接続もキーフレームを送っている間しか表示されなかった。

  ヘッダ          評価実験 / デモ展示の別（保存先から決まる）・言語の切替
  一番上          総合ステータス（色＋次にやること）。判定は gui_status.overall_status（HUD と同じ材料）
  状態            Quest（PING で生存を見る）・CV（起動・GPU・処理中）・セッション
  このセッション  受信・処理済み・採用・待ちの枚数、直近の解の採否と理由、最後に返送した Sim(3)
  設定            scene / every / min（変更は即 cv.json。キーフレームが届いた後のシーン変更は確かめる）
  イベント        重要度で色分け・「重要 / すべて」の切替・保存先と cv.log を開くボタン
  下の 1 行       保存先・この PC の IP・ポート・稼働時間

閉じるとき、CV が処理中・キーフレームが届き続けているなら確かめる（閉じると CV も止まるため）。
文言は i18n.py（既定は英語。右上で日本語に切り替える）。判定は gui_status.py（純粋関数・テスト対象）。
ここは描画だけを受け持つ。
"""
import os
import traceback
from tkinter import messagebox

from . import gui_status as gs
from . import i18n

# パレット（GitHub Dark 風）
BG, CARD, FG, MUT = "#0e1116", "#161b22", "#e6edf3", "#8b949e"
ACCENT, BLUE, LINE = "#3fb950", "#58a6ff", "#262d36"
AMBER, RED, PURPLE = "#d29922", "#f85149", "#bc8cff"
LEVEL_COLOR = {"ok": ACCENT, "busy": BLUE, "warn": AMBER, "error": RED}
# イベント欄の行の色（state.LEVELS）
EVENT_COLOR = {"debug": MUT, "info": FG, "mark": BLUE, "ok": ACCENT, "warn": AMBER, "error": RED}
MODE_COLOR = {"experiment": BLUE, "demo": PURPLE}

REFRESH_MS = 1000
# イベント欄に残す行数（これを超えたら古い行から消す。ServerState 側の保持とは別）
EVENT_BOX_KEEP = 1000


def available() -> bool:
    try:
        import customtkinter  # noqa: F401
        return True
    except Exception:
        return False


def _work_area_logical(app, ctk):
    """画面の作業領域（タスクバーを除く）の大きさを、customtkinter の論理 px で返す。

    Windows では Win32 の作業領域（物理 px）を表示倍率で割る。tk の winfo_screenheight は
    環境によって倍率で割った値を返す（試した PC では 2560x1440・150% で 960 だった）ので使わない。
    """
    scale = ctk.ScalingTracker.get_window_scaling(app) or 1.0
    try:
        import ctypes
        from ctypes import wintypes
        r = wintypes.RECT()
        if ctypes.windll.user32.SystemParametersInfoW(0x30, 0, ctypes.byref(r), 0):   # SPI_GETWORKAREA
            return (r.right - r.left) / scale, (r.bottom - r.top) / scale
    except Exception:
        pass
    return float(app.winfo_screenwidth()), float(app.winfo_screenheight())


def run_dashboard(state, on_close=None, config=None, scenes=None, on_config=None,
                  probe=None, on_retry=None, lang=None, on_lang=None, mode=None, out_dir=None):
    """ブロッキングで GUI を実行（メインスレッドで呼ぶこと）。

    state:     ServerState（snapshot() を持つ）
    config:    初期 CV 設定 dict {scene, every, min}
    scenes:    選択肢にする scene 一覧（list[str]）
    on_config: 設定変更時に呼ばれる callback(cfg dict)。run.py が cv.json へ即書き出す。
    probe:     1 秒ごとに呼ぶ。dict を返す:
                 cv       … liveness.read_cv_status（PONG と同じ。HUD と判定を揃える）
                 hb       … セッションの生存信号の中身（liveness.read_session_heartbeat）
                 launcher … CvLauncher.status()（--no-cv なら None）
                 scene    … 今の cv.json の scene / out … 保存先（--out）
    on_retry:  CV の起動を諦めた後の「再試行」（CvLauncher.retry）。None ならボタンを出さない。
    lang:      "en" / "ja"（既定は英語）
    on_lang:   言語を切り替えたときに呼ぶ callback(lang)。run.py が launch_config.json に残す。
    mode:      "experiment" / "demo" / None（ヘッダと窓の題名に出す）
    out_dir:   保存先（--out）。下の 1 行に出し、「フォルダを開く」で開く
    """
    import customtkinter as ctk

    cur = {"lang": i18n.normalize(lang), "log": None, "btns": None, "err": None,
           "detail": None, "relayout": None, "ev_filter": "important"}
    ev = {"last": 0, "tail": None, "lines": 0}   # イベント欄に書いた最後の id・最後の行 (id, rev)・行数
    out_abs = os.path.abspath(out_dir) if out_dir else ""
    mode_key = {"experiment": "mode_experiment", "demo": "mode_demo"}.get(mode)
    statics = []          # (widget, key): 言語を切り替えたら書き直す固定の文言
    wraps_full = []       # (label, 差し引く幅): 窓の幅に合わせて折り返す（総合ステータス）
    wraps_left = []       # (label, 差し引く幅): 左の列の幅に合わせて折り返す（状態・このセッション）

    def T(key, **kw):
        return i18n.t(cur["lang"], key, **kw)

    def tr(widget, key):
        statics.append((widget, key))
        widget.configure(text=T(key))
        return widget

    ctk.set_appearance_mode("dark")
    app = ctk.CTk()

    def set_title():
        app.title(T("win_title", mode=T(mode_key)) if mode_key else T("win_title_plain"))

    set_title()
    # 画面に収まる大きさにする。ノート PC（1080p・表示倍率 150%）だと作業領域は論理 690px ほどしかなく、
    # 縦 1 列に並べると下（設定・イベント欄）が画面の外へはみ出す。そこで総合ステータスの下を
    # 左右 2 列（左: 状態・このセッション・設定 / 右: イベント）にし、高さは作業領域に合わせる。
    work_w, work_h = _work_area_logical(app, ctk)
    win_w = int(max(860, min(1000, work_w - 40)))
    win_h = int(max(560, min(760, work_h - 50)))      # 題名の帯の分を空ける
    app.geometry(f"{win_w}x{win_h}+20+10")
    app.minsize(860, min(560, win_h))
    app.configure(fg_color=BG)

    f_small  = ctk.CTkFont(size=12)
    f_val    = ctk.CTkFont(size=13)
    f_mono   = ctk.CTkFont(family="Cascadia Code", size=12)
    f_h1     = ctk.CTkFont(size=15, weight="bold")
    f_cap    = ctk.CTkFont(size=11, weight="bold")
    f_banner = ctk.CTkFont(size=19, weight="bold")
    f_count  = ctk.CTkFont(size=15, weight="bold")

    # ---- ヘッダ（題名と言語の切替）----
    header = ctk.CTkFrame(app, fg_color="transparent")
    header.pack(fill="x", padx=18, pady=(14, 4))
    ctk.CTkLabel(header, text="VPTAligned3DGS  Offload Server", font=f_h1, text_color=FG).pack(side="left")
    if mode_key:
        # 評価実験とデモ展示は同じ GUI なので、どちらの .bat で起動したかを色つきで見せる
        tr(ctk.CTkLabel(header, text="", font=f_cap, text_color=BG, fg_color=MODE_COLOR[mode],
                        corner_radius=8, height=24), mode_key).pack(side="left", padx=(12, 0), ipadx=8)
    lang_btn = ctk.CTkSegmentedButton(header, values=[i18n.LANG_LABELS[c] for c in i18n.LANGS],
                                      font=f_small, fg_color=CARD, unselected_color=CARD,
                                      selected_color=LINE, selected_hover_color=LINE,
                                      text_color=FG, command=lambda label: set_lang(label))
    lang_btn.set(i18n.LANG_LABELS[cur["lang"]])
    lang_btn.pack(side="right")

    # ---- 下の 1 行（ポート・稼働時間）。判断には使わないので目立たせない。イベント欄が伸びる前に置く ----
    footer = ctk.CTkLabel(app, text="", font=f_small, text_color=MUT, anchor="w", justify="left")
    footer.pack(side="bottom", fill="x", padx=22, pady=(0, 8))
    wraps_full.append((footer, 50))

    # ---- 総合ステータス ----
    banner = ctk.CTkFrame(app, fg_color=CARD, corner_radius=14, border_width=2, border_color=MUT)
    banner.pack(fill="x", padx=18, pady=6)
    b_row = ctk.CTkFrame(banner, fg_color="transparent")
    b_row.pack(fill="x", padx=16, pady=(12, 0))
    dot = ctk.CTkLabel(b_row, text="", width=16, height=16, corner_radius=8, fg_color=MUT)
    dot.pack(side="left", padx=(0, 10))
    b_title = ctk.CTkLabel(b_row, text="—", font=f_banner, text_color=FG, anchor="w", justify="left")
    b_title.pack(side="left", fill="x", expand=True)
    wraps_full.append((b_title, 110))
    b_detail = ctk.CTkLabel(banner, text="", font=f_small, text_color=MUT, anchor="w", justify="left")
    wraps_full.append((b_detail, 110))
    # 説明が無いときの下の余白。左右と下を空けて置く（枠の線と角の丸みを覆わないように）
    b_pad = ctk.CTkFrame(banner, fg_color="transparent", height=4)
    b_btns = ctk.CTkFrame(banner, fg_color="transparent")    # 起動に失敗したときだけ出す

    def set_detail(text):
        """説明の行。空なら詰める（出す・出さないが変わったときだけ詰め直す。毎秒やるとちらつく）。"""
        b_detail.configure(text=text)
        want = bool(text)
        if cur["detail"] == want:
            return
        cur["detail"] = want
        b_detail.pack_forget()
        b_pad.pack_forget()
        kw = {"before": b_btns} if b_btns.winfo_manager() else {}
        if want:
            b_detail.pack(fill="x", padx=(42, 16), pady=(2, 12), **kw)
        else:
            b_pad.pack(fill="x", padx=16, pady=(0, 8), **kw)

    def open_path(p):
        """ファイル・フォルダを Windows の既定のアプリで開く（無ければ何もしない）。"""
        if p and os.path.exists(p) and hasattr(os, "startfile"):
            os.startfile(p)

    def open_log():
        open_path(cur["log"])

    btn_retry = tr(ctk.CTkButton(b_btns, width=120, fg_color=BLUE, hover_color="#4c8fdc", text_color=BG,
                                 command=lambda: on_retry and on_retry()), "btn_retry")
    btn_log = tr(ctk.CTkButton(b_btns, width=120, fg_color=LINE, hover_color="#30363d", text_color=FG,
                               command=open_log), "btn_log")

    def layout_buttons(show_retry, show_log):
        """ボタンの出し入れ（変わったときだけ詰め直す。毎秒やると並びがちらつく）。"""
        want = (show_retry, show_log)
        if cur["btns"] == want:
            return
        cur["btns"] = want
        for w in (btn_retry, btn_log, b_btns):
            w.pack_forget()
        if show_retry or show_log:
            b_btns.pack(fill="x", padx=(42, 16), pady=(0, 12))
            if show_retry:
                btn_retry.pack(side="left", padx=(0, 8))
            if show_log:
                btn_log.pack(side="left")

    # ---- 総合ステータスの下: 左右 2 列 ----
    body = ctk.CTkFrame(app, fg_color="transparent")
    body.pack(fill="both", expand=True, padx=12)
    body.grid_columnconfigure(0, weight=3, uniform="col")
    body.grid_columnconfigure(1, weight=2, uniform="col")
    body.grid_rowconfigure(0, weight=1)
    left = ctk.CTkFrame(body, fg_color="transparent")
    left.grid(row=0, column=0, sticky="nsew")
    right = ctk.CTkFrame(body, fg_color="transparent")
    right.grid(row=0, column=1, sticky="nsew")

    def card(parent, key, expand=False):
        f = ctk.CTkFrame(parent, fg_color=CARD, corner_radius=14)
        f.pack(fill="both" if expand else "x", expand=expand, padx=6, pady=6)
        tr(ctk.CTkLabel(f, text="", font=f_cap, text_color=MUT), key).pack(anchor="w", padx=16, pady=(10, 2))
        return f

    def row(parent, key):
        r = ctk.CTkFrame(parent, fg_color="transparent")
        r.pack(fill="x", padx=16, pady=2)
        tr(ctk.CTkLabel(r, text="", font=f_small, text_color=MUT), key).pack(side="left", anchor="n")
        v = ctk.CTkLabel(r, text="—", font=f_val, text_color=FG, justify="right", anchor="e")
        v.pack(side="right")
        wraps_left.append((v, 170))
        return v

    def spacer(parent):
        ctk.CTkFrame(parent, fg_color="transparent", height=6).pack()

    # ---- 状態 ----
    c_st = card(left, "sec_status")
    quest_val = row(c_st, "row_quest")
    cv_val = row(c_st, "row_cv")
    session_val = row(c_st, "row_session")
    spacer(c_st)

    # ---- このセッション ----
    c_se = card(left, "sec_session")
    count_lbl = ctk.CTkLabel(c_se, text="—", font=f_count, text_color=FG, anchor="w", justify="left")
    count_lbl.pack(fill="x", padx=16, pady=(0, 4))
    wraps_left.append((count_lbl, 50))
    solve_val = row(c_se, "row_last_solve")
    sent_val = row(c_se, "row_last_sent")
    spacer(c_se)

    # ---- 設定（編集可・変更は即 cv.json へ）----
    sync_scene_menu = None
    if on_config is not None:
        cfg0 = config or {}
        scene_paths = [str(s) for s in (scenes or [])]
        sel = {"scene": cfg0.get("scene")}
        if sel["scene"] and sel["scene"] not in scene_paths:     # 引数で一覧に無いシーンを指定した
            scene_paths = [sel["scene"]] + scene_paths
        c_cfg = card(left, "sec_settings")
        # 未決定のときに出す「（シーンを選択）」。どの言語の文言でも「選んでいない」とみなす
        placeholder_texts = {i18n.t(lg, k) for lg in i18n.LANGS for k in ("scene_placeholder", "scene_none")}

        def scene_menu_values():
            if sel["scene"]:
                return list(scene_paths)
            return [T("scene_placeholder" if scene_paths else "scene_none")] + scene_paths

        def emit(src):
            sc = scene_menu.get()
            if sc in placeholder_texts:
                if src == "scene":
                    return              # 「（シーンを選択）」を選び直しただけ
                sc = None
            if src == "scene" and sc:
                # キーフレームが届いた後のシーン変更は、CV がこのセッションの途中経過を捨てる。うっかり防止
                w = gs.scene_change_warning(state.snapshot(), sel["scene"], sc)
                if w is not None and not messagebox.askyesno(
                        T("scene_title"), T(w[0], **w[1]) + "\n\n" + T("scene_question"),
                        icon="warning", default="no", parent=app):
                    sync_scene_menu()   # 選ぶ前に戻す
                    return
            if sc:
                sel["scene"] = sc
            try:
                on_config({"scene": sel["scene"], "every": int(every_menu.get()), "min": int(min_menu.get())})
            except ValueError:
                pass
            sync_scene_menu()

        def menu_row(key, values, init, src):
            r = ctk.CTkFrame(c_cfg, fg_color="transparent")
            r.pack(fill="x", padx=16, pady=3)
            tr(ctk.CTkLabel(r, text="", font=f_small, text_color=MUT), key).pack(side="left")
            mm = ctk.CTkOptionMenu(r, values=[str(v) for v in values], command=lambda _v: emit(src),
                                   width=230, fg_color=BG, button_color=LINE,
                                   dropdown_fg_color=CARD)
            mm.set(str(init))
            mm.pack(side="right")
            return mm

        def _sync():
            """シーンの選択肢を今の言語・選択に合わせる。未決定なら琥珀で目立たせる（CV がまだ起動しない）。"""
            vals = scene_menu_values()
            scene_menu.configure(values=vals, state="normal" if scene_paths else "disabled",
                                 text_color=FG if sel["scene"] else AMBER)
            scene_menu.set(sel["scene"] or vals[0])

        scene_menu = menu_row("lbl_scene", scene_menu_values(), "", "scene")
        every_menu = menu_row("lbl_every", [1, 2, 3, 4, 5, 8, 10], cfg0.get("every", 3), "every")
        min_menu   = menu_row("lbl_min", [2, 3, 4, 5, 6, 8], cfg0.get("min", 3), "min")
        sync_scene_menu = _sync
        sync_scene_menu()
        spacer(c_cfg)

    # ---- イベント欄（見出しの行に「重要 / すべて」、下にフォルダと cv.log を開くボタン）----
    c_l = ctk.CTkFrame(right, fg_color=CARD, corner_radius=14)
    c_l.pack(fill="both", expand=True, padx=6, pady=6)
    l_head = ctk.CTkFrame(c_l, fg_color="transparent")
    l_head.pack(fill="x", padx=(16, 12), pady=(8, 2))
    tr(ctk.CTkLabel(l_head, text="", font=f_cap, text_color=MUT), "sec_events").pack(side="left")

    def filter_labels():
        return [T("ev_important"), T("ev_all")]

    def set_filter(label):
        f = "all" if label == T("ev_all") else "important"
        if f != cur["ev_filter"]:
            cur["ev_filter"] = f
            render_events(state.snapshot(), full=True)

    ev_seg = ctk.CTkSegmentedButton(l_head, values=filter_labels(), font=f_small, height=24,
                                    fg_color=BG, unselected_color=BG, selected_color=LINE,
                                    selected_hover_color=LINE, text_color=FG, command=set_filter)
    ev_seg.set(T("ev_important"))
    ev_seg.pack(side="right")

    l_foot = ctk.CTkFrame(c_l, fg_color="transparent")
    l_foot.pack(side="bottom", fill="x", padx=12, pady=(0, 10))
    btn_folder = tr(ctk.CTkButton(l_foot, height=26, width=10, font=f_small, fg_color=LINE,
                                  hover_color="#30363d", text_color=FG,
                                  command=lambda: open_path(out_abs)), "btn_folder")
    btn_folder.pack(side="left", padx=(0, 8))
    btn_cvlog = tr(ctk.CTkButton(l_foot, height=26, width=10, font=f_small, fg_color=LINE,
                                 hover_color="#30363d", text_color=FG, command=open_log), "btn_cvlog")
    btn_cvlog.pack(side="left")

    log_box = ctk.CTkTextbox(c_l, fg_color=BG, text_color=FG, font=f_mono,
                             corner_radius=8, border_width=0, activate_scrollbars=True, wrap="word")
    log_box.pack(fill="both", expand=True, padx=12, pady=(2, 8))
    log_box.tag_config("t", foreground=MUT)
    for lv, color in EVENT_COLOR.items():
        log_box.tag_config(lv, foreground=color)
    log_box.configure(state="disabled")

    def ev_text(e):
        return e.get("en") if cur["lang"] == "en" and e.get("en") else e["msg"]

    def write_event(e):
        # 行の頭に印を置く（左に寄る印なので、後ろに書き足しても行の頭を指し続ける）。
        # 最後の行が書き換わったとき（キーフレームの件数の行）は、ここから後ろを消して書き直す
        log_box.mark_set("ev_tail", "end-1c")
        log_box.mark_gravity("ev_tail", "left")
        log_box.insert("end", e["t"] + "  ", ("t",))
        log_box.insert("end", ev_text(e) + "\n", (e.get("level", "info"),))
        ev["tail"] = (e["id"], e["rev"])
        ev["lines"] += 1

    def render_events(s, full=False):
        """イベント欄に**増えた行だけ**を書き足す（毎秒すべてを書き直すと、読んでいる途中でスクロールが戻る）。

        いちばん下を見ているときだけ新しい行へ送る。上へ戻って読んでいるときは動かさない。
        full=True（言語・「重要 / すべて」の切替）のときだけ全部を書き直す。
        """
        evs = s["events_all"] if cur["ev_filter"] == "all" else s["events"]
        if full:
            ev.update(last=0, tail=None, lines=0)
        tail = ev["tail"]
        upd = None
        if tail is not None:
            upd = next((e for e in reversed(evs) if e["id"] == tail[0]), None)
            if upd is not None and upd["rev"] == tail[1]:
                upd = None
        new = [e for e in evs if e["id"] > ev["last"]]
        if not (full or upd or new):
            return
        # いちばん下を見ているか = 最後の行が画面に出ているか。yview の割合は折り返しのある行で
        # 見積もりになり、いちばん下にいても 0.9 程度を返すことがあるので使わない
        at_bottom = full or log_box.dlineinfo("end-2c") is not None or log_box.dlineinfo("end-1c") is not None
        log_box.configure(state="normal")
        if full:
            log_box.delete("1.0", "end")
        if upd is not None:
            log_box.delete("ev_tail", "end-1c")
            ev["lines"] -= 1
            write_event(upd)
        for e in new:
            write_event(e)
        if evs:
            ev["last"] = max(ev["last"], evs[-1]["id"])
        if ev["lines"] > EVENT_BOX_KEEP + 200:
            drop = ev["lines"] - EVENT_BOX_KEEP
            log_box.delete("1.0", f"{drop + 1}.0")
            ev["lines"] -= drop
        log_box.configure(state="disabled")
        if at_bottom:
            log_box.see("end")

    # ---- 窓・左の列の幅に合わせて折り返す ----
    # 窓の大きさが変わったとき（after_idle で 1 回）と、毎秒の描画のついでに幅を見て、変わっていれば
    # 折り返し幅を直す。起動直後は左の列の幅がまだ決まっていない（1 px）ので、窓の Configure だけに
    # 頼ると狭いまま折り返してしまう。
    def relayout():
        cur["relayout"] = None
        widths = (app.winfo_width(), left.winfo_width())
        if widths == cur.get("widths") or widths[1] <= 1:
            return
        cur["widths"] = widths
        scale = ctk.ScalingTracker.get_widget_scaling(app) or 1.0
        for lbl, minus in wraps_full:
            lbl.configure(wraplength=max(160, int(widths[0] / scale - minus)))
        for lbl, minus in wraps_left:
            lbl.configure(wraplength=max(160, int(widths[1] / scale - minus)))

    def on_resize(e):
        if e.widget is app and cur["relayout"] is None:
            cur["relayout"] = app.after_idle(relayout)

    app.bind("<Configure>", on_resize, add="+")

    # ---- 描画 ----
    def render():
        s = state.snapshot()
        pr = probe() if probe else {}
        cv = pr.get("cv") or {}
        la = pr.get("launcher")
        scene = pr.get("scene", (config or {}).get("scene"))

        # 総合ステータス
        level, key, params = gs.overall_status(s, cv, la, scene)
        color = LEVEL_COLOR[level]
        dot.configure(fg_color=color)
        banner.configure(border_color=color)
        b_title.configure(text=T(key, **params), text_color=color)
        la_ = la or {}
        problem = la_.get("problem_en") if cur["lang"] == "en" else la_.get("problem")
        ips = ", ".join(gs.local_ipv4s()) or "?"
        set_detail(T(key + "_detail", **dict(
            params, problem=problem or "", log=la_.get("log", ""), out=pr.get("out", ""), ips=ips)))
        # CV のログ: 自動起動なら CvLauncher が書く <out>/cv.log（--no-cv のときも同じ場所を見る）
        cur["log"] = la_.get("log") or (os.path.join(out_abs, "cv.log") if out_abs else None)
        has_log = bool(cur["log"]) and os.path.exists(cur["log"])
        btn_cvlog.configure(state="normal" if has_log else "disabled")
        btn_folder.configure(state="normal" if out_abs and os.path.isdir(out_abs) else "disabled")
        layout_buttons(key == "st_cv_failed" and on_retry is not None,
                       key in ("st_cv_failed", "st_cv_silent") and has_log)

        # Quest（PING かキーフレームのどちらかが届いていれば生きている）
        qa = s.get("quest_age")
        if not s.get("quest_ip") or qa is None:
            quest_val.configure(text=T("quest_none"), text_color=MUT)
        elif gs.quest_alive(s):
            quest_val.configure(text=T("quest_ok", ip=s["quest_ip"], age=f"{qa:.0f}"), text_color=ACCENT)
        else:
            quest_val.configure(text=T("quest_stale", ip=s["quest_ip"], age=f"{qa:.0f}"), text_color=AMBER)

        # CV
        lv, parts = gs.cv_line(cv, la, scene)
        cv_val.configure(text=" · ".join(p["text"] if k == "_raw" else T(k, **p) for k, p in parts),
                         text_color=LEVEL_COLOR[lv])
        session_val.configure(text=s.get("session") or "—")

        # このセッション
        rx = s["keyframe_count"]
        prog = gs.session_progress(rx, cv)
        if prog is None and rx == 0:
            prog = {"rx": 0, "done": 0, "ok": 0, "queue": 0}
        count_lbl.configure(text=T("counts", **prog) if prog else T("counts_rx_only", rx=rx))

        ls = gs.last_solve(pr.get("hb"))
        if ls is None:
            solve_val.configure(text="—", text_color=MUT)
        else:
            solve_val.configure(text=T("solve_pub" if ls["published"] else "solve_rej", **ls),
                                text_color=ACCENT if ls["published"] else AMBER)

        r = s["last_result"]
        if r:
            sig = r.get("sigma")
            # 反射を handedness=-1 で固定しているので scale は常に負で、それが正常。大きさだけ出す
            scale_txt = gs.fmt_num(abs(sig) if isinstance(sig, (int, float)) else None, 4)
            age = s.get("result_age")
            sent_val.configure(text=T("sent_val", scale=scale_txt, inl=r.get("inliers"), n=r.get("n"),
                                      rmse=gs.fmt_num(r.get("rmse"), 4),
                                      age="—" if age is None else f"{age:.0f}"), text_color=FG)
        else:
            sent_val.configure(text="—", text_color=MUT)

        p = s["ports"] or {}
        footer.configure(text=T("footer", out=out_abs or "—", ips=ips, data=p.get("data", "-"),
                                beacon=p.get("beacon") or T("off"), result=p.get("result") or T("off"),
                                uptime=gs.fmt_uptime(s["uptime"])))

        render_events(s)

    def tick():
        try:
            relayout()
            render()
            cur["err"] = None
        except Exception:
            # 1 回の描画の失敗で更新を止めない（after を予約し直せず GUI が固まる）。同じ失敗は 1 回だけ出す
            msg = traceback.format_exc()
            if msg != cur["err"]:
                cur["err"] = msg
                print("[gui] 描画エラー:\n" + msg)
        app.after(REFRESH_MS, tick)

    def set_lang(label):
        code = next((c for c, lb in i18n.LANG_LABELS.items() if lb == label), i18n.DEFAULT_LANG)
        if code == cur["lang"]:
            return
        cur["lang"] = code
        for w, key in statics:
            w.configure(text=T(key))
        if sync_scene_menu is not None:
            sync_scene_menu()
        ev_seg.configure(values=filter_labels())
        ev_seg.set(T("ev_all") if cur["ev_filter"] == "all" else T("ev_important"))
        set_title()
        if on_lang:
            on_lang(code)
        try:
            render_events(state.snapshot(), full=True)
            render()
        except Exception:
            pass

    def _close():
        """閉じる。CV が処理中・キーフレームが届き続けているなら確かめる（閉じると CV も止まる）。"""
        try:
            w = gs.close_warning(state.snapshot(), (probe() if probe else {}).get("cv"))
        except Exception:
            w = None
        if w is not None and not messagebox.askyesno(
                T("close_title"), T(w[0], **w[1]) + "\n\n" + T("close_question"),
                icon="warning", default="no", parent=app):
            return
        try:
            if on_close:
                on_close()
        finally:
            app.destroy()

    app.protocol("WM_DELETE_WINDOW", _close)
    tick()
    app.mainloop()
