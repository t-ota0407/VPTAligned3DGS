"""オンライン・ローカライズ（M3 step3 / hloc 経路。WSL2/Linux で実行）。

クエリ画像（パススルー or 自己テスト用 DB 画像）を、hloc_db で作った再ローカライズ DB
（SuperPoint 点群＋NetVLAD）に対して位置推定する。
  クエリ SuperPoint/NetVLAD 抽出 → 検索(top-k) → LightGlue マッチ → localize_sfm(PnP)
  → クエリのカメラ姿勢 T_colmap←cam（= 3DGS モデルフレーム）。

得られた各キーフレームの「COLMAP 中心 C_c」と Quest の「ワールド中心 C_w」を
geometry.solve_sim3_ransac に渡すと、splatRoot に与える Sim(3)（反射=負スケール込み）になる。

検証順序（重要）:
  1. --selftest: DB 画像の一部を（自分を検索から除外して）ローカライズし、既知 COLMAP 姿勢と
     一致するか確認 → 特徴抽出・マッチ・2D-3D・PnP・座標系の plumbing を Quest 抜きで検証。
  2. その後、実パススルーのキーフレームでアライン（別段で実装）。

実行（WSL2 の vptcv 環境）:
  cd /mnt/c/Users/heatf/Desktop/VPTAligned3DGS/server
  python -m vptserver.cv.localize --scene scene_data/example1 --selftest 8
"""
import argparse
import logging
from pathlib import Path

import numpy as np

from . import geometry as G
from . import gates

logging.basicConfig(level=logging.INFO, format="[localize] %(message)s")
log = logging.getLogger("localize")

# DB 構築（hloc_db.py）と完全に一致させる設定。
FEATURE_CONF = "superpoint_max"
MATCHER_CONF = "superpoint+lightglue"
RETRIEVAL_CONF = "netvlad"
SUBMODEL_DIRNAME = "sfm_superpoint+lightglue"


def _imports():
    try:
        import pycolmap  # noqa
        from hloc import (extract_features, match_features, pairs_from_retrieval,  # noqa
                          localize_sfm)
    except Exception as e:
        raise SystemExit(f"hloc/pycolmap が必要（WSL2 の vptcv 環境で実行）。詳細: {e}")


def autodetect_scene(scene_root="scene_data"):
    """scene_data/ 配下でビルド済み（hloc/sfm_* がある）シーンを探す。1つだけなら返す。"""
    root = Path(scene_root)
    if not root.is_dir():
        return None
    built = [d for d in sorted(root.iterdir())
             if d.is_dir() and (d / "hloc" / SUBMODEL_DIRNAME).is_dir()]
    return str(built[0]) if len(built) == 1 else None


def read_active_cv_config(base_dir):
    """received 配下の最新 session_*/cv.json（run.py が書く設定）を読む。無ければ {}。"""
    import json
    base = Path(base_dir)
    subs = sorted(d for d in base.glob("session_*") if d.is_dir())
    if not subs:
        return {}
    cj = subs[-1] / "cv.json"
    if cj.exists():
        try:
            return json.loads(cj.read_text())
        except Exception:
            return {}
    return {}


def _read_pnp_inliers(results_path):
    """localize_sfm が書く <results>_logs.pkl から {query_name: num_inliers} を読む。

    hloc のバージョンで logs の構造が揺れるため、取れない要素は None（＝ゲート無効）。
    ログ自体が無い/壊れている場合は WARN を出して {} を返す（num_inliers=None で握りつぶさない）。
    """
    import pickle
    p = Path(str(results_path) + "_logs.pkl")
    if not p.exists():
        log.warning("PnP ログが無い（ゲート inliers 不明）: %s", p.name)
        return {}
    try:
        logs = pickle.loads(p.read_bytes())
    except Exception as e:
        log.warning("PnP ログ読込失敗（ゲート inliers 不明）: %s", e)
        return {}
    loc = logs.get("loc", {}) if isinstance(logs, dict) else {}
    out = {}
    for name, rec in loc.items():
        ni = None
        try:
            ret = rec.get("PnP_ret", rec) if isinstance(rec, dict) else rec
            if isinstance(ret, dict):
                ni = ret.get("num_inliers")
                if ni is None and ret.get("inliers") is not None:
                    ni = int(np.sum(ret["inliers"]))
        except Exception:
            ni = None
        out[name] = int(ni) if ni is not None else None
    if not any(v is not None for v in out.values()):
        log.warning("PnP ログから inliers を取得できず（構造差の可能性・ゲート無効）")
    return out


def _image_center(im):
    """pycolmap Image からカメラ中心（world 座標）を取得。バージョン差に頑健。"""
    # 1) projection_center() があれば使う
    f = getattr(im, "projection_center", None)
    if callable(f):
        try:
            return np.asarray(f(), dtype=float)
        except Exception:
            pass
    # 2) cam_from_world(world->cam) から center = -R^T t
    cfw = im.cam_from_world
    if callable(cfw):
        cfw = cfw()
    rot = cfw.rotation
    Rm = rot.matrix() if callable(getattr(rot, "matrix", None)) else getattr(rot, "matrix", rot)
    Rm = np.asarray(Rm, dtype=float)
    t = np.asarray(cfw.translation, dtype=float)
    return -Rm.T @ t


class Localizer:
    """DB を一度ロードし、クエリ画像群をローカライズする。"""

    def __init__(self, scene_dir, topk=20):
        _imports()
        self.topk = topk
        self.scene_str = None
        self.load_scene(scene_dir)

    def load_scene(self, scene_dir):
        """シーン（COLMAP/DB）を読み込む。watch 中のシーン変更でも再呼び出しして差し替え可能。"""
        import pycolmap
        self.scene = Path(scene_dir)
        self.scene_str = str(scene_dir)
        self.hloc = self.scene / "hloc"
        self.images = self.scene / "images"

        # 三角測量済みモデル（.ply と同座標系）
        self.model = pycolmap.Reconstruction(str(self.hloc / SUBMODEL_DIRNAME))
        self.db_names = [im.name for im in self.model.images.values()]

        from hloc import extract_features, match_features
        self.feat_conf = extract_features.confs[FEATURE_CONF]
        self.retr_conf = extract_features.confs[RETRIEVAL_CONF]
        self.match_conf = match_features.confs[MATCHER_CONF]

        # DB 側の特徴/大域記述子（hloc_db が出力済み）
        self.db_features = self.hloc / (self.feat_conf["output"] + ".h5")
        self.db_global = self.hloc / (self.retr_conf["output"] + ".h5")
        assert self.db_features.exists(), f"DB 特徴が無い: {self.db_features}"
        assert self.db_global.exists(), f"DB 大域記述子が無い: {self.db_global}"
        log.info("DB ロード: scene=%s images=%d points3D=%d",
                 self.scene_str, len(self.db_names), self.model.num_points3D())

    def localize(self, query_image_dir, query_names, intrinsics, work, exclude_self=False):
        """クエリ群をローカライズ。

        query_image_dir: クエリ画像のあるディレクトリ
        query_names:     その中の相対名リスト
        intrinsics:      {name: (W,H,fx,fy,cx,cy)}（PINHOLE）
        work:            一時出力ディレクトリ
        返り値: {name: dict(qvec(4), tvec(3), center(3), num_inliers)}（失敗名は欠落）
        """
        from hloc import extract_features, match_features, pairs_from_retrieval, localize_sfm
        work = Path(work); work.mkdir(parents=True, exist_ok=True)

        # 自己テストで DB 画像をクエリにする場合は DB 特徴を再利用、そうでなければ抽出
        same_as_db = (Path(query_image_dir).resolve() == self.images.resolve())
        if same_as_db:
            q_feat, q_global = self.db_features, self.db_global
        else:
            q_feat = extract_features.main(self.feat_conf, Path(query_image_dir),
                                           export_dir=work, image_list=query_names)
            q_global = extract_features.main(self.retr_conf, Path(query_image_dir),
                                             export_dir=work, image_list=query_names)

        # 検索ペア（クエリ→DB）
        loc_pairs = work / "loc_pairs.txt"
        pairs_from_retrieval.main(q_global, loc_pairs, num_matched=self.topk,
                                  db_descriptors=self.db_global,
                                  query_list=query_names, db_list=self.db_names)
        if exclude_self:   # 自己テスト: クエリと同名 DB を除外して交差検証にする
            lines = [ln for ln in loc_pairs.read_text().splitlines()
                     if len(ln.split()) == 2 and ln.split()[0] != ln.split()[1]]
            loc_pairs.write_text("\n".join(lines) + "\n")

        # マッチ（クエリ vs DB）
        matches = match_features.main(self.match_conf, loc_pairs, q_feat,
                                      matches=work / "loc_matches.h5",
                                      features_ref=self.db_features)

        # クエリ内部パラメータファイル（localize_sfm 用）
        queries_file = work / "queries.txt"
        with open(queries_file, "w") as f:
            for n in query_names:
                W, H, fx, fy, cx, cy = intrinsics[n]
                f.write(f"{n} PINHOLE {W} {H} {fx} {fy} {cx} {cy}\n")

        results = work / "loc_results.txt"
        localize_sfm.main(self.model, queries_file, loc_pairs, q_feat, matches, results)

        # PnP インライア数（品質ゲート用）。取れない要素は None。
        inl_by_name = _read_pnp_inliers(results)

        # 結果（name qw qx qy qz tx ty tz）を読む
        out = {}
        for ln in results.read_text().splitlines():
            t = ln.split()
            if len(t) != 8:
                continue
            name = t[0]
            qvec = np.array(list(map(float, t[1:5])))   # qw,qx,qy,qz (world->cam)
            tvec = np.array(list(map(float, t[5:8])))
            R = G.colmap_quat_to_R(*qvec)
            center = -R.T @ tvec
            out[name] = dict(qvec=qvec, tvec=tvec, center=center,
                             num_inliers=inl_by_name.get(name))
        return out

    def selftest(self, n=8, work=None):
        """DB 画像 n 枚を自己ローカライズ（自分を除外）し、既知姿勢との誤差を出す。"""
        work = Path(work or (self.hloc / "selftest"))
        names = self.db_names
        sel = [names[i] for i in np.linspace(0, len(names) - 1, n).astype(int)]

        cam = next(iter(self.model.cameras.values()))
        K = cam.params  # PINHOLE: fx,fy,cx,cy
        intr = {nm: (cam.width, cam.height, K[0], K[1], K[2], K[3]) for nm in sel}

        # 既知（GT）中心（pycolmap のバージョン差に頑健に取得）
        gt_center = {im.name: _image_center(im)
                     for im in self.model.images.values() if im.name in sel}

        est = self.localize(self.images, sel, intr, work, exclude_self=True)

        log.info("自己ローカライズ %d 枚:", len(sel))
        errs = []
        for nm in sel:
            if nm not in est:
                log.warning("  %s: 失敗（推定なし）", nm); continue
            e = float(np.linalg.norm(est[nm]["center"] - gt_center[nm]))
            errs.append(e)
            log.info("  %s: 中心誤差=%.4f (COLMAP単位)", nm, e)
        if errs:
            ext = float(np.linalg.norm(self._scene_extent()))
            log.info("中心誤差 median=%.4f mean=%.4f / シーン対角=%.2f (誤差/対角 median=%.2e)",
                     float(np.median(errs)), float(np.mean(errs)), ext, float(np.median(errs)) / ext)
            log.info("→ 誤差がシーン対角の ~1e-3 以下なら plumbing/座標系は正常。")
        return errs

    def _scene_extent(self):
        pts = np.array([p.xyz for p in self.model.points3D.values()])
        return pts.max(0) - pts.min(0)

    def align_from_keyframes(self, received_dir, work=None, thresh_m=0.10):
        """received/ の実キーフレーム（req_*.jpg + .json）をローカライズし Sim(3) を解く。

        各 .json は META（w,h,sw,sh,fx..cy=K@sensor, px,py,pz=Quest world 位置, ...）。
        返り値: dict(sim3=solve結果 or None, per_kf=[...])。
        """
        import json
        from ..keyframe import k_for_image_from_meta
        received = Path(received_dir)
        work = Path(work or (self.hloc / "align_work"))

        metas = sorted(received.glob("req_*.json"))
        if not metas:
            log.warning("キーフレームが無い: %s", received); return dict(sim3=None, per_kf=[])

        import cv2
        names, intr, world_c = [], {}, {}
        for mp in metas:
            jpg = mp.with_suffix(".jpg")
            if not jpg.exists():
                continue
            if cv2.imread(str(jpg)) is None:   # 壊れた/ダミー画像（test_send 等）はスキップ
                log.info("読み込めない画像をスキップ: %s", jpg.name)
                continue
            try:
                m = json.loads(mp.read_text())
                K = k_for_image_from_meta(m)
                px, py, pz = m["px"], m["py"], m["pz"]
            except Exception as e:
                log.info("META 不正をスキップ: %s (%s)", mp.name, e)
                continue
            nm = jpg.name
            names.append(nm)
            intr[nm] = (int(m["w"]), int(m["h"]), K[0, 0], K[1, 1], K[0, 2], K[1, 2])
            world_c[nm] = np.array([px, py, pz], dtype=float)  # Quest world 中心

        log.info("キーフレーム %d 枚をローカライズ…", len(names))
        est = self.localize(received, names, intr, work)

        Cc, Cw, used = [], [], []
        for nm in names:
            if nm in est:
                Cc.append(est[nm]["center"]); Cw.append(world_c[nm]); used.append(nm)
        log.info("ローカライズ成功: %d/%d", len(used), len(names))

        if len(used) < 3:
            log.warning("成功キーフレームが3枚未満 → Sim(3) を解けません（視点を増やして再取得）。")
            return dict(sim3=None, per_kf=used)

        res = solve_alignment(Cc, Cw, thresh_m=thresh_m)
        return dict(sim3=res, per_kf=used, centers_colmap=Cc, centers_world=Cw)

    def watch(self, base_dir, every=2, min_kf=5, poll=1.0, out_result=None, thresh_m=0.10):
        """base_dir 配下の「最新 session_* フォルダ」を自動追従して逐次ローカライズし、
        Sim(3) を更新し続ける（run.py がそのフォルダの result_sim3.json を Quest へ返送）。

        - run.py は起動ごとに received/session_<ts>/ を作る。watch は常に最新セッションを見る。
        - 新セッションが現れたら状態をリセット（別セッション＝別ワールド座標系のため混ぜない）。
        - 累計成功が min_kf 以上＆前回解法から every 枚増えたら Sim(3) を解いて result を更新。
        out_result を指定するとセッションに関係なくその固定パスへ書く（通常は指定しない）。
        """
        import json
        import time
        import cv2
        from ..keyframe import k_for_image_from_meta
        base = Path(base_dir)

        def active_session():
            subs = sorted(d for d in base.glob("session_*") if d.is_dir())
            return subs[-1] if subs else base   # 最新セッション、無ければ base 自身（後方互換）

        cur = None
        work = self.hloc / "watch_work"       # セッション切替時にセッション別へ差し替える
        # Cc/Cw: 解法バッファ（P2-b でウィンドウ化）／total_ok: 累計採用数（再計算タイミングの基準）
        processed, Cc, Cw, total_ok, last_solved, out_path = set(), [], [], 0, 0, None
        cv_mtime = None
        gate_cfg = {}                    # cv.json 由来の品質ゲート設定（P1・欠損は既定）
        window = 24                      # P2-b: 直近 N 件のみで解く（0=無制限）。cv.json で上書き
        last_hb = 0.0                    # ハートビート最終書き込み時刻

        last_solve = {}                  # 直近の解の統計と採否理由（Quest まで届ける）

        busy = {}                        # 処理中かどうか（Quest 側の「待機中」表示に使う）

        def write_heartbeat(session_dir, n_window, n_total):
            """自分が生きていることをセッションフォルダに書く（run.py が読んで Quest へ伝える）。

            run.py だけ動いていて WSL2 の CV が未起動、という状態は外から見分けがつかない。
            キーフレームは届いて保存されるのに Sim(3) が永遠に返らないので、実験者は
            「位置合わせが失敗した」と誤解したまま試行を消費してしまう。これを実験前に検出する。
            /mnt/c は遅いので 2 秒に 1 回までに抑える。
            """
            import json as _json
            import os as _os
            try:
                p = Path(session_dir) / "cv_heartbeat.json"
                tmp = str(p) + ".tmp"
                d = {"t": time.time(), "pid": _os.getpid(),
                     "scene": self.scene_str, "window": n_window,
                     "total_ok": n_total}
                # 直近の解の採否理由も載せる。これが無いと「なぜロックしないか」が
                # WSL2 のターミナルにしか出ず、実機側からは原因が分からない。
                d.update(last_solve)
                # 処理中フラグ。hloc の1バッチは数秒かかり、その間ハートビートを
                # 更新できない。これが無いと Quest 側は「CV 停止」と誤表示してしまう。
                d.update(busy)
                with open(tmp, "w", encoding="utf-8") as f:
                    _json.dump(d, f, ensure_ascii=False)
                _os.replace(tmp, p)
            except Exception:
                pass          # ハートビートは補助情報。失敗しても本体は止めない
        log.info("watch 開始: base=%s every=%d min=%d window=%d（最新 session_* を自動追従・cv.json を即時反映）",
                 base, every, min_kf, window)
        try:
            while True:
                act = active_session()
                if act != cur:                   # セッション切替（または初回）→ リセット
                    cur = act
                    processed, Cc, Cw, total_ok, last_solved = set(), [], [], 0, 0
                    out_path = Path(out_result) if out_result else (cur / "result_sim3.json")
                    cv_mtime = None              # 新セッションの cv.json を読み直す
                    last_hb = 0.0                # 新セッションへ即ハートビートを書く
                    # **作業ディレクトリはセッションごとに分ける。**
                    # キーフレームは試行ごとに req_000001 から振り直されるため、共有ディレクトリだと
                    # hloc が「その名前は抽出済み」と判断して前セッションの画像特徴を使い回す。
                    # 姿勢だけが新しくなるので対応が総崩れになり、インライアが 3/24 まで落ちる。
                    work = self.hloc / "watch_work" / cur.name
                    prune_work_dirs(self.hloc / "watch_work", keep=8)
                    last_solve = {}              # 前セッションの採否理由を持ち越さない
                    log.info("セッション追従: %s （結果→ %s）",
                             cur.name if cur != base else "(base)", out_path)

                # cv.json をライブ反映（GUI/run.py の変更が即効く）
                cj = cur / "cv.json"
                if cj.exists():
                    try:
                        mt = cj.stat().st_mtime
                        if mt != cv_mtime:
                            cv_mtime = mt
                            cfg = json.loads(cj.read_text())
                            gate_cfg = cfg               # 品質ゲートしきい値もライブ反映
                            ne, nm = int(cfg.get("every", every)), int(cfg.get("min", min_kf))
                            nwin = int(cfg.get("window", window))
                            ns = cfg.get("scene")
                            if ns and ns != self.scene_str:     # scene 変更 → DB 再ロード＆蓄積リセット
                                log.info("scene 変更 → 再ロード: %s", ns)
                                self.load_scene(ns)
                                processed, Cc, Cw, total_ok, last_solved = set(), [], [], 0, 0
                            if (ne, nm, nwin) != (every, min_kf, window):
                                log.info("設定更新(cv.json): every %d→%d, min %d→%d, window %d→%d",
                                         every, ne, min_kf, nm, window, nwin)
                            every, min_kf, window = ne, nm, nwin
                    except Exception:
                        pass

                # **処理済み判定はファイル名 + 更新時刻**で行う。
                # キーフレーム名は試行ごとに req_000001 から振り直されるため、万一同じセッション
                # フォルダが再利用されるとファイルが上書きされる。名前だけで判定していると
                # 「既に処理した」と誤認して新しい試行のデータを一枚も読まなくなる（実機で発生）。
                todo = []
                for m in sorted(cur.glob("req_*.json")):
                    try:
                        key = (m.name, m.stat().st_mtime_ns)
                    except OSError:
                        continue
                    if key not in processed:
                        todo.append((m, key))

                names, intr, world, meta_of = [], {}, {}, {}
                for m, key in todo:
                    processed.add(key)
                    jpg = m.with_suffix(".jpg")
                    if not jpg.exists() or cv2.imread(str(jpg)) is None:
                        continue
                    try:
                        meta = json.loads(m.read_text())
                        K = k_for_image_from_meta(meta)
                    except Exception:
                        continue
                    nm = jpg.name
                    names.append(nm)
                    intr[nm] = (int(meta["w"]), int(meta["h"]), K[0, 0], K[1, 1], K[0, 2], K[1, 2])
                    world[nm] = np.array([meta["px"], meta["py"], meta["pz"]], dtype=float)
                    meta_of[nm] = meta

                if names:
                    # 処理に入る直前に「busy」を書いておく。hloc は数秒ブロックするので、
                    # ここで書かないと Quest 側からは沈黙＝停止に見える。
                    busy = {"busy": True, "busy_n": len(names)}
                    write_heartbeat(cur, len(Cc), total_ok)
                    last_hb = time.time()
                    est = self.localize(cur, names, intr, work)
                    busy = {"busy": False, "busy_n": 0}
                    ok, rej = 0, 0
                    for nm in names:
                        rec = est.get(nm)
                        acc, why = (False, "ローカライズ失敗") if rec is None else \
                            gates.should_accept_kf(rec["num_inliers"], gate_cfg)
                        # **各キーフレームの結果を残す**。これが無いと、あとから
                        # 「最初の t 秒分だけで解いたらどうなるか」を再現できず、
                        # 収集時間と精度の関係（評価の主結果）が引けない。hloc の再実行は高価。
                        append_kf_result(cur, nm, meta_of.get(nm, {}), rec, acc, why,
                                         world.get(nm))
                        if rec is None:
                            log.info("KF棄却: %s ローカライズ失敗", nm)
                            rej += 1
                            continue
                        if not acc:               # P1-a: 低インライア KF は蓄積しない
                            log.info("KF棄却: %s %s", nm, why)
                            rej += 1
                            continue
                        Cc.append(rec["center"]); Cw.append(world[nm]); total_ok += 1; ok += 1
                        if window > 0 and len(Cc) > window:   # P2-b: 古い KF を捨てて直近 N 件に保つ
                            del Cc[0]; del Cw[0]
                    log.info("[%s] 新着 %d 枚（採用 %d 棄却 %d）／累計 %d 窓 %d",
                             cur.name, len(names), ok, rej, total_ok, len(Cc))

                if len(Cc) >= min_kf and (total_ok - last_solved) >= every:
                    last_solved = total_ok
                    hand = gate_cfg.get("handedness", -1)
                    res = solve_alignment(Cc, Cw, thresh_m=thresh_m,
                                          handedness=(None if hand in (0, None) else int(hand)))
                    nin = int(res["inliers"].sum())
                    pub, why = gates.should_publish(       # P1-a: 解の公開ゲート
                        res["sigma"], nin, len(Cc), res["rmse_m"],
                        gate_cfg, cond=res["cond"])
                    # 採否と統計を記録し、ハートビート経由で Quest まで届ける
                    last_solve = {
                        "solve_n": len(Cc), "solve_inliers": nin,
                        "solve_ratio": round(nin / max(1, len(Cc)), 3),
                        "solve_rmse": round(float(res["rmse_m"]), 4),
                        "solve_sigma": round(float(res["sigma"]), 4),
                        "solve_baseline": round(res["cond"]["baseline"], 2),
                        "solve_extent2": round(res["cond"]["extent2"], 2),
                        "solve_published": bool(pub),
                        "solve_why": why,
                    }
                    # **終盤のキーフレームだけ残差が跳ね上がっていないか。**
                    # Quest のワールド原点が収集の途中で動くと、そこから先のキーフレームは
                    # 別座標系になる。RANSAC が古い側を採るので rmse も インライア率も
                    # 悪化せず、数値だけ見ていると気づけない。実機で 24 枚中の最後の 4 枚が
                    # 0.4→0.8→1.9→2.8 m と伸びる形で発生した（試行内で原点が飛んだ）。
                    tail = tail_residual_m(res, Cc, Cw)
                    if tail is not None and tail > 0.20:
                        log.error("終盤のキーフレーム残差が %.2f m あります。収集中に Quest の"
                                  " トラッキング原点が動いた可能性が高く、この試行は使えません"
                                  "（analysis/check_tracking.py で確認できます）。", tail)

                    if pub:
                        write_result_json(res, out_path, len(Cc))
                        log.info("Sim(3) 更新 → %s（%s / run.py が Quest へ返送）", out_path, why)
                    else:
                        log.warning("Sim(3) 非公開（ゲート不合格）: %s "
                                    "[n=%d inliers=%d rmse=%.4f scale=%+.3f 間隔=%.2f 広がり=%.2f]",
                                    why, len(Cc), nin, res["rmse_m"], res["sigma"],
                                    res["cond"]["baseline"], res["cond"]["extent2"])
                    write_heartbeat(cur, len(Cc), total_ok)   # 採否を即座に反映
                    last_hb = time.time()

                now = time.time()
                if now - last_hb >= 2.0:
                    last_hb = now
                    write_heartbeat(cur, len(Cc), total_ok)

                time.sleep(poll)
        except KeyboardInterrupt:
            log.info("watch 終了")


def tail_residual_m(res, Cc, Cw, tail_n=4):
    """直近 tail_n 枚の残差中央値[m]。判定できないときは None。

    トラッキング原点が収集の途中で動いたことを検出するための指標。
    全体の rmse は古い側の点に引っ張られて小さいままなので、**末尾だけを見る**必要がある。
    """
    if len(Cc) <= tail_n + 2:
        return None
    q = res["quat"]
    x, y, z, w = float(q[0]), float(q[1]), float(q[2]), float(q[3])
    R = np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                  [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                  [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])
    A = np.asarray(Cc, dtype=float)
    B = np.asarray(Cw, dtype=float)
    pred = (res["sigma"] * (R @ A.T)).T + res["t"]
    e = np.linalg.norm(pred - B, axis=1)
    return float(np.median(e[-tail_n:]))


def prune_work_dirs(base, keep=8):
    """セッション別の作業ディレクトリが溜まりすぎないよう、古いものを消す。

    1 セッションあたり数十 MB になるので放置すると膨らむ。中身は再生成可能な中間ファイル
    （特徴・マッチ・PnP ログ）だけなので、直近 keep 個を残して消してよい。
    """
    import shutil
    try:
        base = Path(base)
        if not base.is_dir():
            return
        subs = sorted((d for d in base.iterdir() if d.is_dir() and d.name.startswith("session_")),
                      key=lambda d: d.name)
        for d in subs[:-keep] if len(subs) > keep else []:
            shutil.rmtree(d, ignore_errors=True)
            log.info("古い作業ディレクトリを削除: %s", d.name)
    except Exception:
        pass


def append_kf_result(session_dir, name, meta, rec, accepted, why, center_world):
    """1 キーフレームのローカライズ結果を `kf_localized.jsonl` に追記する。

    これが評価の主結果（収集時間 vs 位置合わせ誤差）を引くための素材になる。
    保存しておけば、あとから「最初の t 秒分のキーフレームだけで Sim(3) を解き直す」を
    t を変えて繰り返せるので、**1 回の歩行データから曲線全体をオフラインで再構成できる**。
    hloc の再実行（GPU で数分）を避けられるのが大きい。
    """
    import json as _json
    try:
        row = {
            "name": name,
            "req": meta.get("req"),
            "ts": meta.get("ts"),                    # カメラのタイムスタンプ（ISO8601）
            "accepted": bool(accepted),
            "why": why,
            "num_inliers": (rec or {}).get("num_inliers"),
        }
        if center_world is not None:
            row["center_world"] = [float(v) for v in center_world]
        if rec is not None:
            row["center_colmap"] = [float(v) for v in rec["center"]]
        with open(Path(session_dir) / "kf_localized.jsonl", "a", encoding="utf-8") as f:
            f.write(_json.dumps(row, ensure_ascii=False) + "\n")
    except Exception:
        pass          # 記録は補助。失敗しても本体は止めない


def solve_alignment(centers_colmap, centers_world, thresh_m=0.10, handedness=-1):
    """各キーフレームの (C_colmap, C_world) から splatRoot 用 Sim(3) を解く。

    handedness の既定は **-1（反射固定）**。COLMAP 右手系 → Unity 左手系の手性の違いは
    座標規約から決まる定数であり、推定させると共面配置で鏡像解とのコインフリップになる
    （geometry.umeyama の説明を参照）。自由推定に戻したい場合のみ None を渡す。
    """
    res = G.solve_sim3_ransac(np.asarray(centers_colmap), np.asarray(centers_world),
                              thresh_m=thresh_m, handedness=handedness)
    c = res["cond"]
    log.info("Sim(3): scale=%+.4f (反射=%s) インライア=%d/%d rmse=%.4gm "
             "配置[間隔%.2fm 広がり%.2f/%.2fm]",
             res["sigma"], res["sigma"] < 0, int(res["inliers"].sum()),
             len(centers_colmap), res["rmse_m"],
             c["baseline"], c["extent2"], c["extent3"])
    return res


def write_result_json(res, path, n):
    """解いた Sim(3) を result_sim3.json に書き出す（run.py が検知して Quest へ返送）。"""
    import json
    q, t = res["quat"], res["t"]
    Path(path).write_text(json.dumps(dict(
        sigma=float(res["sigma"]),
        qx=float(q[0]), qy=float(q[1]), qz=float(q[2]), qw=float(q[3]),
        px=float(t[0]), py=float(t[1]), pz=float(t[2]),
        inliers=int(res["inliers"].sum()), n=int(n), rmse=float(res["rmse_m"]),
    ), indent=2))


def main():
    ap = argparse.ArgumentParser(
        description="hloc 再ローカライズ（オンライン側）。既定は received を監視する運用モード。")
    ap.add_argument("--scene", default=None,
                    help="DB シーン（省略時: 最新 session/cv.json か scene_data の自動検出で解決）")
    ap.add_argument("--watch", default="received",
                    help="監視するベースディレクトリ（既定 received。最新 session_* を自動追従）")
    ap.add_argument("--topk", type=int, default=20)
    ap.add_argument("--selftest", type=int, default=0, help="DB画像N枚で自己ローカライズ検証")
    ap.add_argument("--keyframes", default=None, help="指定フォルダから一度だけSim(3)を解く")
    ap.add_argument("--every", type=int, default=2,
                help="watch: この枚数の新規成功ごとに Sim(3) 再計算（連続一致の機会を増やすため小さめ）")
    ap.add_argument("--min", type=int, default=5, dest="min_kf",
                help="watch: 初回 Sim(3) に必要な最小成功枚数（5未満だと RANSAC が回らない）")
    ap.add_argument("--fresh", action="store_true", help="（互換用・無視）")
    ap.add_argument("--out-result", default=None,
                    help="Sim(3) の書き出し先（既定: 対象フォルダの result_sim3.json）")
    args = ap.parse_args()

    # シーン解決: --scene > 最新セッションの cv.json（run.py が書く）> scene_data 自動検出
    scene = args.scene or read_active_cv_config(args.watch).get("scene") or autodetect_scene()
    if scene is None:
        raise SystemExit(
            "scene を解決できません。次のいずれかにしてください: (1) --scene を指定, "
            "(2) run.py を起動して cv.json を生成, (3) scene_data/ のビルド済みシーンを1つに。")
    log.info("scene = %s", scene)

    loc = Localizer(scene, topk=args.topk)
    if args.selftest:
        loc.selftest(args.selftest)
    elif args.keyframes:
        out = loc.align_from_keyframes(args.keyframes)
        if out["sim3"] is not None:
            r = out["sim3"]
            log.info("=== Sim(3)（splatRoot 用）===")
            log.info("  localScale = %.6f x3 (反射=%s)", r["sigma"], r["sigma"] < 0)
            log.info("  rotation(quat xyzw) = %s", np.round(r["quat"], 6).tolist())
            log.info("  position = %s", np.round(r["t"], 6).tolist())
            op = args.out_result or (Path(args.keyframes) / "result_sim3.json")
            write_result_json(r, op, len(out["per_kf"]))
            log.info("結果を書き出し: %s （run.py が検出して Quest へ返送）", op)
    else:
        # 既定: 運用モード（received を監視）
        loc.watch(args.watch, every=args.every, min_kf=args.min_kf, out_result=args.out_result)


if __name__ == "__main__":
    main()
