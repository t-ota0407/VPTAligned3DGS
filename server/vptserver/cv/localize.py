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
import json
import logging
import os
import time
from pathlib import Path

import numpy as np

from . import geometry as G
from . import gates
# DB 構築（hloc_db.py）と完全に一致させる設定（→ vptserver/scenes.py）
from ..scenes import (FEATURE_CONF, MATCHER_CONF, RETRIEVAL_CONF,
                      SUBMODEL_DIRNAME, autodetect_scene)

class _LevelFormatter(logging.Formatter):
    """警告以上だけ重要度を書く（`[localize] WARNING: …` / `[localize] ERROR: …`）。

    run.py（cv_launcher.cv_event_level）がこれを見て GUI のイベント欄を色分けする。以前は重要度が
    行に無く、文言のキーワードで拾っていたため、ゲート不合格や「この試行は使えない」の警告が GUI に
    届いていなかった。INFO は今までどおり `[localize] …`（コンソールと cv.log を読みやすく保つ）。
    """

    def format(self, record):
        msg = super().format(record)
        if record.levelno >= logging.WARNING:
            return f"[localize] {record.levelname}: {msg}"
        return f"[localize] {msg}"


_handler = logging.StreamHandler()
_handler.setFormatter(_LevelFormatter("%(message)s"))
logging.basicConfig(level=logging.INFO, handlers=[_handler])
log = logging.getLogger("localize")


def _imports():
    try:
        import pycolmap  # noqa
        from hloc import (extract_features, match_features, pairs_from_retrieval,  # noqa
                          localize_sfm)
    except Exception as e:
        raise SystemExit(f"hloc/pycolmap が必要（WSL2 の vptcv 環境で実行）。詳細: {e}")


def read_active_cv_config(base_dir):
    """received 配下の最新 session_*/cv.json（run.py が書く設定）を読む。無ければ {}。"""
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
        self.warm = None             # モデルと DB を読み込んだまま回す経路（warm_hloc）。None なら hloc の main を呼ぶ
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
        self._init_warm()

    def _init_warm(self):
        """モデルと DB をメモリに置いたまま回す経路（cv/warm_hloc.py）を用意する。

        起動時に済ませておけば、最初のキーフレームで待たせない。用意できなければ hloc の main を毎回呼ぶ
        （以前の動き。遅いが結果は同じ）。環境変数 VPT_CV_PLAIN_HLOC=1 で常に以前の動きにできる。
        """
        if os.environ.get("VPT_CV_PLAIN_HLOC"):
            self.warm = None
            return
        try:
            from .warm_hloc import WarmHloc
            t0 = time.time()
            if self.warm is None:
                self.warm = WarmHloc(self.feat_conf, self.retr_conf, self.match_conf)
                self.warm.load_models()
            self.warm.set_db(self.db_features, self.db_global, self.db_names)
            log.info("モデルと DB をメモリに読み込みました（%.1f s。以後キーフレームごとには読み直さない）",
                     time.time() - t0)
        except Exception as e:
            log.warning("モデルと DB をメモリに置く準備に失敗 → 毎回読み直す以前のやり方で処理します（遅い）: %s", e)
            self.warm = None

    def localize(self, query_image_dir, query_names, intrinsics, work, exclude_self=False):
        """クエリ群をローカライズ。

        query_image_dir: クエリ画像のあるディレクトリ
        query_names:     その中の相対名リスト
        intrinsics:      {name: (W,H,fx,fy,cx,cy)}（PINHOLE）
        work:            一時出力ディレクトリ
        返り値: {name: dict(qvec(4), tvec(3), center(3), num_inliers)}（失敗名は欠落）
        """
        work = Path(work); work.mkdir(parents=True, exist_ok=True)
        # 自己テストで DB 画像をクエリにする場合は DB 特徴を再利用する（hloc の main で処理）
        same_as_db = (Path(query_image_dir).resolve() == self.images.resolve())
        if self.warm is not None and not same_as_db:
            try:
                return self._localize_warm(query_image_dir, query_names, intrinsics, work)
            except Exception as e:
                # 一度失敗したら以後も通常経路（失敗のたびに時間を無駄にしない）
                log.warning("モデルを読み込んだままの処理で失敗 → 以後は毎回読み直す以前のやり方で処理します: %r", e)
                self.warm = None
                work = work / "plain"
                work.mkdir(parents=True, exist_ok=True)
        return self._localize_plain(query_image_dir, query_names, intrinsics, work,
                                    exclude_self, same_as_db)

    def _localize_warm(self, query_image_dir, query_names, intrinsics, work):
        """warm_hloc でローカライズする（hloc の main と同じ結果。違いは読み込み済みのものを使い回すことだけ）。"""
        t0 = time.time()
        q_feat = work / (self.feat_conf["output"] + ".h5")
        feats = self.warm.extract("feat", query_image_dir, query_names, q_feat)
        q_global = self.warm.extract("retr", query_image_dir, query_names,
                                     work / (self.retr_conf["output"] + ".h5"))
        t1 = time.time()
        loc_pairs = work / "loc_pairs.txt"
        pairs = self.warm.retrieve(q_global, query_names, loc_pairs, self.topk)
        reads0 = self.warm.db_feature_reads
        matches = self.warm.match(pairs, feats, work / "loc_matches.h5")
        t2 = time.time()
        out = self._pnp(query_names, intrinsics, work, loc_pairs, q_feat, matches)
        t3 = time.time()
        log.info("ローカライズ %d 枚: %.1f s（特徴 %.1f / 照合 %.1f・DB 読込 %d 枚 / PnP %.1f）",
                 len(query_names), t3 - t0, t1 - t0, t2 - t1,
                 self.warm.db_feature_reads - reads0, t3 - t2)
        return out

    def _localize_plain(self, query_image_dir, query_names, intrinsics, work, exclude_self, same_as_db):
        """hloc の main 関数を呼んでローカライズする（以前のやり方。呼ぶたびにモデルと DB を読み直す）。"""
        from hloc import extract_features, match_features, pairs_from_retrieval
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
        return self._pnp(query_names, intrinsics, work, loc_pairs, q_feat, matches)

    def _pnp(self, query_names, intrinsics, work, loc_pairs, q_feat, matches):
        """hloc の localize_sfm（2D-3D → PnP）を回して結果を読む。両経路で共通。"""
        from hloc import localize_sfm
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
        received = Path(received_dir)
        work = Path(work or (self.hloc / "align_work"))

        metas = sorted(received.glob("req_*.json"))
        if not metas:
            log.warning("キーフレームが無い: %s", received); return dict(sim3=None, per_kf=[])

        names, intr, world_c = [], {}, {}
        for mp in metas:
            try:
                nm, intr[nm], world_c[nm], _ = read_keyframe(mp)   # world_c: Quest world 中心
            except FileNotFoundError:
                continue
            except ValueError as e:                               # 壊れた/ダミー画像・META 不正
                log.info("スキップ: %s", e)
                continue
            names.append(nm)

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

    def watch(self, base_dir, every=2, min_kf=5, poll=1.0, out_result=None, thresh_m=0.10, stop_event=None):
        """base_dir 配下の「最新 session_* フォルダ」を自動追従して逐次ローカライズし、
        Sim(3) を更新し続ける（run.py がそのフォルダの result_sim3.json を Quest へ返送）。

        - run.py は起動ごとに received/session_<ts>/ を作る。watch は常に最新セッションを見る。
        - 新セッションが現れたら状態をリセット（別セッション＝別ワールド座標系のため混ぜない）。
        - 累計成功が min_kf 以上＆前回解法から every 枚増えたら Sim(3) を解いて result を更新。
        out_result を指定するとセッションに関係なくその固定パスへ書く（通常は指定しない）。
        1 周の中身は _Watcher.step()。
        """
        import threading
        from .heartbeat import Heartbeat

        # 生存信号（ハートビート）は別スレッドが 2 秒ごとに書く（→ heartbeat.py）。
        # run.py だけ動いていて WSL2 の CV が未起動、という状態は外から見分けがつかない。
        # キーフレームは届いて保存されるのに Sim(3) が永遠に返らないので、これを Quest に伝える。
        # **hloc の処理中もスレッドが書き続ける**ので、生存信号の鮮度は PC の速さに依存しない
        # （メインループからしか書かなかった頃は、遅い PC で正常な処理中を「停止」と誤判定された）。
        stop = stop_event if stop_event is not None else threading.Event()   # 起動元が終わったら立つ（lifecycle.py）
        hb = Heartbeat(Path(base_dir), self.scene_str)
        hb.start()
        w = _Watcher(self, base_dir, every, min_kf, out_result, thresh_m, hb)
        log.info("watch 開始: base=%s every=%d min=%d window=%d（最新 session_* を自動追従・cv.json を即時反映）",
                 w.base, every, min_kf, w.window)
        try:
            while not stop.is_set():
                w.step()
                stop.wait(poll)          # time.sleep の代わり（起動元が終わればすぐ抜ける）
            log.info("watch 終了")
        except KeyboardInterrupt:
            log.info("watch 終了")
        finally:
            hb.stop()


def read_keyframe(meta_path):
    """キーフレーム 1 枚（req_*.json の META と対の .jpg）を、ローカライズの入力に読む。

    返り値: (画像名, (w, h, fx, fy, cx, cy), Quest ワールドのカメラ中心, META)。
    画像が無いときは FileNotFoundError、読めないとき（壊れた画像・tools/test_send.py のダミー・
    META の欠け）は ValueError（理由つき）。
    """
    import cv2
    from ..keyframe import k_for_image_from_meta
    meta_path = Path(meta_path)
    jpg = meta_path.with_suffix(".jpg")
    if not jpg.exists():
        raise FileNotFoundError(jpg.name)
    if cv2.imread(str(jpg)) is None:
        raise ValueError(f"読み込めない画像: {jpg.name}")
    try:
        meta = json.loads(meta_path.read_text())
        K = k_for_image_from_meta(meta)
        intr = (int(meta["w"]), int(meta["h"]), K[0, 0], K[1, 1], K[0, 2], K[1, 2])
        center = np.array([meta["px"], meta["py"], meta["pz"]], dtype=float)
    except Exception as e:
        raise ValueError(f"META 不正: {meta_path.name} ({e})")
    return jpg.name, intr, center, meta


class _WatchSession:
    """watch が 1 セッション（= 1 つの Quest ワールド座標系）について持つ状態。
    新しいセッションへ移るときは作り直す（別座標系のキーフレームを混ぜない）。"""

    def __init__(self, path, out_path, work):
        self.path = path
        self.out_path = out_path     # 結果（result_sim3.json）の書き出し先
        # **作業ディレクトリはセッションごとに分ける。**
        # キーフレームは試行ごとに req_000001 から振り直されるため、共有ディレクトリだと
        # hloc が「その名前は抽出済み」と判断して前セッションの画像特徴を使い回す。
        # 姿勢だけが新しくなるので対応が総崩れになり、インライアが 3/24 まで落ちる。
        self.work = work
        self.cv_mtime = None         # 最後に読んだ cv.json の更新時刻（新セッションでは読み直す）
        self.last_solve = {}         # 直近の解の統計と採否理由（Quest まで届ける。前セッションのは持ち越さない）
        self.clear()

    def clear(self):
        """蓄積を捨てる（新しいセッション・シーンの変更）。"""
        self.processed = set()       # 処理済みのキーフレーム（名前 + 更新時刻）
        self.Cc, self.Cw = [], []    # 解法バッファ（P2-b でウィンドウ化）
        self.total_ok = 0            # 累計採用数（再計算タイミングの基準）
        self.last_solved = 0
        # done_n: このセッションで処理し終えたキーフレーム数（採否を問わない。解き直しまで済んだ分）。
        # Quest のデモは「送信を止めてから PC が全部処理し終えたか」をこれで判定する
        # （busy だけだと「届いたがまだ処理を始めていない」瞬間を「終わった」と取り違える）。
        self.done_n = 0


class _Watcher:
    """Localizer.watch の 1 周ぶんの処理（step）と、セッションをまたいで持つ設定。"""

    def __init__(self, loc, base_dir, every, min_kf, out_result, thresh_m, hb):
        self.loc = loc
        self.base = Path(base_dir)
        self.every, self.min_kf = every, min_kf
        self.window = 24             # P2-b: 直近 N 件のみで解く（0=無制限）。cv.json で上書き
        self.gate_cfg = {}           # cv.json 由来の品質ゲート設定（P1・欠損は既定）
        self.out_result = out_result
        self.thresh_m = thresh_m
        self.hb = hb
        self.s = None                # いまのセッション（_WatchSession）

    def step(self):
        self._follow_latest_session()
        self._reload_config()
        todo = self._new_keyframes()
        names = self._localize_new(todo)
        # 処理済み枚数（画像が欠けて読めなかった分も数える。二度と処理されないので、待ち続けないように）。
        # ハートビートへ渡すのは**解き直しの後**（_report）。ハートビートは別スレッドが 2 秒ごとに書くので、
        # ここで渡すと「処理済み」が先に Quest へ届き、結果がまだ無いのに「処理し終えた」と判定される。
        self.s.done_n += len(todo)
        solved_now = self._maybe_solve()
        self._report(write_now=bool(names) or solved_now)

    def _follow_latest_session(self):
        """最新の session_* へ移る（初回・Quest の RESET で新しいセッションができたとき）。"""
        subs = sorted(d for d in self.base.glob("session_*") if d.is_dir())
        act = subs[-1] if subs else self.base   # 最新セッション、無ければ base 自身（後方互換）
        if self.s is not None and act == self.s.path:
            return
        out_path = Path(self.out_result) if self.out_result else (act / "result_sim3.json")
        self.s = _WatchSession(act, out_path, self.loc.hloc / "watch_work" / act.name)
        # 新セッションへ即ハートビートを書く（前のセッションの進み具合は持ち越さない）
        self.hb.reset_session(act)
        self.hb.update(busy=False, busy_n=0, window=0, total_ok=0, done=0)
        self.hb.write_now()
        prune_work_dirs(self.loc.hloc / "watch_work", keep=8)
        log.info("セッション追従: %s （結果→ %s）",
                 act.name if act != self.base else "(base)", out_path)

    def _reload_config(self):
        """cv.json をライブ反映する（GUI/run.py の変更が即効く）。"""
        cj = self.s.path / "cv.json"
        if not cj.exists():
            return
        try:
            mt = cj.stat().st_mtime
            if mt == self.s.cv_mtime:
                return
            self.s.cv_mtime = mt
            cfg = json.loads(cj.read_text())
            self.gate_cfg = cfg                      # 品質ゲートしきい値もライブ反映
            ne, nm = int(cfg.get("every", self.every)), int(cfg.get("min", self.min_kf))
            nwin = int(cfg.get("window", self.window))
            ns = cfg.get("scene")
            if ns and ns != self.loc.scene_str:      # scene 変更 → DB 再ロード＆蓄積リセット
                log.info("scene 変更 → 再ロード: %s", ns)
                self.loc.load_scene(ns)
                self.hb.set_scene(self.loc.scene_str)
                self.s.clear()
            if (ne, nm, nwin) != (self.every, self.min_kf, self.window):
                log.info("設定更新(cv.json): every %d→%d, min %d→%d, window %d→%d",
                         self.every, ne, self.min_kf, nm, self.window, nwin)
            self.every, self.min_kf, self.window = ne, nm, nwin
        except Exception:
            pass

    def _new_keyframes(self):
        """まだ処理していないキーフレーム（META の json と、処理済み判定の鍵）。

        **処理済み判定はファイル名 + 更新時刻**で行う。
        キーフレーム名は試行ごとに req_000001 から振り直されるため、万一同じセッション
        フォルダが再利用されるとファイルが上書きされる。名前だけで判定していると
        「既に処理した」と誤認して新しい試行のデータを一枚も読まなくなる（実機で発生）。
        """
        todo = []
        for m in sorted(self.s.path.glob("req_*.json")):
            try:
                key = (m.name, m.stat().st_mtime_ns)
            except OSError:
                continue
            if key not in self.s.processed:
                todo.append((m, key))
        return todo

    def _localize_new(self, todo):
        """新着キーフレームをローカライズし、品質ゲートを通ったものを解法バッファに積む。処理した画像名を返す。"""
        s = self.s
        names, intr, world, meta_of = [], {}, {}, {}
        for m, key in todo:
            s.processed.add(key)
            try:
                nm, intr_nm, center, meta = read_keyframe(m)
            except (OSError, ValueError):
                continue
            names.append(nm)
            intr[nm], world[nm], meta_of[nm] = intr_nm, center, meta
        if not names:
            return names

        # 処理に入る直前に「busy」を立ててすぐ書く。hloc は数秒〜数分ブロックするが、
        # その間もハートビートのスレッドが busy=True のまま書き続ける。
        # busy は解き直しが終わって処理済み枚数を更新するまで下ろさない（_report）。
        self.hb.update(busy=True, busy_n=len(names))
        self.hb.write_now()
        est = self.loc.localize(s.path, names, intr, s.work)
        ok, rej = 0, 0
        for nm in names:
            rec = est.get(nm)
            acc, why = (False, "ローカライズ失敗") if rec is None else \
                gates.should_accept_kf(rec["num_inliers"], self.gate_cfg)
            # **各キーフレームの結果を残す**。これが無いと、あとから
            # 「最初の t 秒分だけで解いたらどうなるか」を再現できず、
            # 収集時間と精度の関係（評価の主結果）が引けない。hloc の再実行は高価。
            append_kf_result(s.path, nm, meta_of.get(nm, {}), rec, acc, why, world.get(nm))
            if rec is None:
                log.info("KF棄却: %s ローカライズ失敗", nm)
                rej += 1
                continue
            if not acc:               # P1-a: 低インライア KF は蓄積しない
                log.info("KF棄却: %s %s", nm, why)
                rej += 1
                continue
            s.Cc.append(rec["center"]); s.Cw.append(world[nm]); s.total_ok += 1; ok += 1
            if self.window > 0 and len(s.Cc) > self.window:   # P2-b: 古い KF を捨てて直近 N 件に保つ
                del s.Cc[0]; del s.Cw[0]
        log.info("[%s] 新着 %d 枚（採用 %d 棄却 %d）／累計 %d 窓 %d",
                 s.path.name, len(names), ok, rej, s.total_ok, len(s.Cc))
        return names

    def _maybe_solve(self):
        """採用が min_kf 枚以上あり、前回の解き直しから every 枚増えていれば Sim(3) を解き直す。解いたら True。"""
        s = self.s
        if not (len(s.Cc) >= self.min_kf and (s.total_ok - s.last_solved) >= self.every):
            return False
        s.last_solved = s.total_ok
        hand = self.gate_cfg.get("handedness", -1)
        res = solve_alignment(s.Cc, s.Cw, thresh_m=self.thresh_m,
                              handedness=(None if hand in (0, None) else int(hand)))
        nin = int(res["inliers"].sum())
        pub, why = gates.should_publish(       # P1-a: 解の公開ゲート
            res["sigma"], nin, len(s.Cc), res["rmse_m"],
            self.gate_cfg, cond=res["cond"])
        # 採否と統計を記録し、ハートビート経由で Quest まで届ける
        s.last_solve = {
            "solve_n": len(s.Cc), "solve_inliers": nin,
            "solve_ratio": round(nin / max(1, len(s.Cc)), 3),
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
        tail = tail_residual_m(res, s.Cc, s.Cw)
        if tail is not None and tail > 0.20:
            log.error("終盤のキーフレーム残差が %.2f m あります。収集中に Quest の"
                      " トラッキング原点が動いた可能性が高く、この試行は使えません"
                      "（analysis/check_tracking.py で確認できます）。", tail)

        if pub:
            write_result_json(res, s.out_path, len(s.Cc))
            log.info("Sim(3) 更新 → %s（%s / run.py が Quest へ返送）", s.out_path, why)
        else:
            log.warning("Sim(3) 非公開（ゲート不合格）: %s "
                        "[n=%d inliers=%d rmse=%.4f scale=%+.3f 間隔=%.2f 広がり=%.2f]",
                        why, len(s.Cc), nin, res["rmse_m"], res["sigma"],
                        res["cond"]["baseline"], res["cond"]["extent2"])
        return True

    def _report(self, write_now):
        """進み具合をハートビートへ（結果ファイルを書いた後なので、Quest が done を見た時点で
        解き直しも済んでいる）。直近の解の採否理由も載せる — これが無いと「なぜロックしないか」が
        WSL2 のターミナルにしか出ず、実機側からは原因が分からない。"""
        s = self.s
        self.hb.update(busy=False, busy_n=0, window=len(s.Cc), total_ok=s.total_ok, done=s.done_n,
                       **s.last_solve)
        if write_now:
            self.hb.write_now()       # 採否・処理済みを即座に反映（定期書き込みを待たない）


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
            f.write(json.dumps(row, ensure_ascii=False) + "\n")
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
    ap.add_argument("--exit-on-stdin-eof", action="store_true",
                    help="標準入力が閉じたら終了する（Windows の run.py が自動起動するときに付ける。"
                         "run.py が終わると localize も終わる。→ lifecycle.py）")
    args = ap.parse_args()

    import os
    import threading
    stop_event = threading.Event()
    if args.exit_on_stdin_eof:
        from .lifecycle import exit_when_parent_gone
        exit_when_parent_gone(stop_event, log)
    # run.py（cv_launcher）が終了時にこのプロセスを確実に止められるよう、PID を最初に出す
    log.info("pid=%d", os.getpid())
    log_compute_device()

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
        loc.watch(args.watch, every=args.every, min_kf=args.min_kf, out_result=args.out_result,
                  stop_event=stop_event)


def log_compute_device():
    """CV が GPU で動くかを起動時に出す。CPU だと 1 枚に十数秒以上かかり、位置合わせに何分もかかる。

    初めての環境では「動くが遅い」が最も気づきにくい失敗なので、起動した瞬間に分かるようにする。
    """
    try:
        import torch
        if torch.cuda.is_available():
            log.info("CUDA: available (%s, torch %s, CUDA %s)",
                     torch.cuda.get_device_name(0), torch.__version__, torch.version.cuda)
        else:
            log.warning("CUDA: NOT available — CV は CPU で動きます（1 枚に十数秒以上かかります）。"
                        "Windows の NVIDIA ドライバと、CUDA 版 torch（server/setup_wsl.sh）を確認してください")
    except Exception as e:
        log.warning("torch を確認できません（CV 環境が不完全です。server/setup_wsl.sh を実行してください）: %s", e)


if __name__ == "__main__":
    main()
