"""hloc のローカライズを、モデルと DB を読み込んだまま回す（localize の 1 回あたりの固定時間を削る）。

hloc の main 関数（extract_features / pairs_from_retrieval / match_features）は、呼ばれるたびにモデルを作り、
DB をファイルから読み直す。localize はキーフレームが届くたびにこれを呼ぶので、1 回ごとに約 10 秒の固定費が
かかっていた（RTX 3070 Ti Laptop・irt_short2・1 枚で実測: NetVLAD のモデル作成 4.9 s、DB の大域記述子
624 枚の読み込み 4.3 s。照合はペアごとに h5 を開き直して 0.1 s/ペア＝1 枚 20 ペアで 2 s）。
デモの Re-align は PC の処理待ちが 4 枚になると送信を止めるので、1 回に渡る枚数が小さく、固定費が支配的だった。

ここでは**同じ計算**を、モデル・DB の大域記述子・DB の局所特徴（LRU）をメモリに置いたまま行う。
出力は hloc と同じになるように作る（違うと Sim(3) が変わる）。ここか hloc の版を変えたら、
`python -m vptserver.cv.check_warm` で以前の経路（hloc の main）と突き合わせて同じになることを確かめる:

- 前処理は hloc の ImageDataset をそのまま使う。後処理（キーポイントの尺度・不確かさ）も extract_features と同じ
- 特徴は hloc と同じく float16 に丸めて h5 に書き、照合にも丸めた値を使う（hloc は h5 から読み戻して使う）
- 検索は hloc の pairs_from_score_matrix をそのまま使い、DB の並び順も同じ
- 照合の入力は hloc の FeaturePairsDataset ＋ DataLoader（batch 1）と同じ形・同じデバイスに置く
- PnP は呼び出し側が hloc の localize_sfm.main をそのまま呼ぶ（ここで書いた h5 を読む）

DataLoader のワーカも使わない（fork しない。cv/lifecycle.py の説明にある詰まりとも無縁になる）。
torch / hloc を import するので WSL2 の CV 環境でしか動かない。
"""
from collections import OrderedDict
from pathlib import Path

import h5py
import numpy as np
import torch
from torch.utils.data import default_collate

# DB 画像の局所特徴をメモリに置く枚数（superpoint_max は 1 枚 ≈ 2 MB → 約 0.8 GB）。
# 検索は 1 枚あたり上位 20 枚を引き、同じ部屋を歩く間は同じ DB 画像が何度も当たる
DB_FEATURE_CACHE = 400


def _as_half(pred):
    """hloc extract_features の as_half=True と同じ: float32 の配列だけ float16 にする。"""
    for k in pred:
        if pred[k].dtype == np.float32:
            pred[k] = pred[k].astype(np.float16)
    return pred


def _write_group(fd, name, arrays, uncertainty=None):
    """hloc と同じ形で h5 にグループを書く（同名があれば消して書き直す）。"""
    if name in fd:
        del fd[name]
    grp = fd.create_group(name)
    for k, v in arrays.items():
        grp.create_dataset(k, data=v)
    if uncertainty is not None and "keypoints" in arrays:
        grp["keypoints"].attrs["uncertainty"] = uncertainty


class WarmHloc:
    """hloc の 3 段（局所特徴・大域記述子・照合）を、読み込み済みの状態で回す。"""

    def __init__(self, feat_conf, retr_conf, match_conf, device=None):
        self.device = device or ("cuda" if torch.cuda.is_available() else "cpu")
        self.confs = {"feat": feat_conf, "retr": retr_conf, "match": match_conf}
        self._models = {}
        self.db_names = []
        self.db_desc = None
        self.db_features = None
        self._db_feat = OrderedDict()
        self.db_feature_reads = 0      # 照合のために DB の h5 から読んだ枚数（キャッシュの効き具合）

    # ---- 読み込み -------------------------------------------------------------------------------
    def load_models(self):
        """3 つのモデルを作っておく（最初のキーフレームで待たせないよう、起動時に呼ぶ）。"""
        for kind in ("feat", "retr", "match"):
            self._model(kind)

    def _model(self, kind):
        if kind not in self._models:
            from hloc import extractors, matchers
            from hloc.utils.base_model import dynamic_load
            conf = dict(self.confs[kind]["model"])
            Model = dynamic_load(matchers if kind == "match" else extractors, conf["name"])
            self._models[kind] = Model(conf).eval().to(self.device)
        return self._models[kind]

    def set_db(self, db_features, db_global, db_names):
        """シーンの DB を差し替える（起動時・シーン変更時）。大域記述子は全部、局所特徴は使う分だけ読む。"""
        from hloc.pairs_from_retrieval import get_descriptors
        self._db_feat.clear()
        self.db_names = list(db_names)
        # hloc の pairs_from_retrieval.main と同じ値（h5 の float16 → float32）。並び順も db_names のまま
        self.db_desc = get_descriptors(self.db_names, Path(db_global)).to(self.device)
        # DB の局所特徴の h5 は開いたままにしない（照合のたびに 1 回だけ開く）。開いたまま fork すると
        # HDF5 の状態を子が受け継ぐ（以前の経路の DataLoader は fork する）
        self.db_features = Path(db_features)

    def _db_feature(self, fd, name):
        hit = self._db_feat.get(name)
        if hit is not None:
            self._db_feat.move_to_end(name)
            return hit
        grp = fd[name]
        arrays = {k: v.__array__() for k, v in grp.items()}
        self.db_feature_reads += 1
        self._db_feat[name] = arrays
        while len(self._db_feat) > DB_FEATURE_CACHE:
            self._db_feat.popitem(last=False)
        return arrays

    # ---- 3 段 ---------------------------------------------------------------------------------
    @torch.no_grad()
    def extract(self, kind, image_dir, names, feature_path):
        """hloc extract_features.main（as_half=True）と同じ特徴を作って h5 に書き、{名前: 配列} を返す。

        返す配列は h5 に書いたもの（float16 に丸めた後）と同じ値。
        """
        from hloc.extract_features import ImageDataset
        conf = self.confs[kind]
        model = self._model(kind)
        dataset = ImageDataset(Path(image_dir), conf["preprocessing"], list(names))
        out, unc = {}, {}
        for idx, name in enumerate(dataset.names):
            # DataLoader（batch 1）と同じまとめ方をする。ImageDataset はカラー画像を転置したビュー
            # （メモリ上は HWC）で返し、collate がそれを連続な NCHW に詰め直す。そのまま GPU に送ると
            # 畳み込みのメモリ配置が変わり、NetVLAD の記述子が float16 の 1〜2 刻みずれる（検索順が変わる）
            data = default_collate([dataset[idx]])
            image = data["image"]
            pred = model({"image": image.to(self.device, non_blocking=True)})
            pred = {k: v[0].cpu().numpy() for k, v in pred.items()}
            pred["image_size"] = original_size = data["original_size"][0].numpy()
            if "keypoints" in pred:
                size = np.array(image.shape[-2:][::-1])
                scales = (original_size / size).astype(np.float32)
                pred["keypoints"] = (pred["keypoints"] + 0.5) * scales[None] - 0.5
                if "scales" in pred:
                    pred["scales"] *= scales.mean()
                unc[name] = getattr(model, "detection_noise", 1) * scales.mean()
            out[name] = _as_half(pred)
        feature_path = Path(feature_path)
        feature_path.parent.mkdir(parents=True, exist_ok=True)
        with h5py.File(str(feature_path), "a", libver="latest") as fd:
            for name, pred in out.items():
                _write_group(fd, name, pred, unc.get(name))
        return out

    def retrieve(self, q_global, query_names, pairs_path, num_matched):
        """hloc pairs_from_retrieval.main と同じペアを作ってファイルに書き、[(クエリ, DB)] を返す。"""
        from hloc.pairs_from_retrieval import pairs_from_score_matrix
        q = torch.from_numpy(np.stack([q_global[n]["global_descriptor"] for n in query_names], 0)).float()
        sim = torch.einsum("id,jd->ij", q.to(self.device), self.db_desc)
        self_match = np.array(query_names)[:, None] == np.array(self.db_names)[None]
        idx = pairs_from_score_matrix(sim, self_match, num_matched, min_score=0)
        pairs = [(query_names[i], self.db_names[j]) for i, j in idx]
        with open(pairs_path, "w") as f:
            f.write("\n".join(" ".join([i, j]) for i, j in pairs))
        return pairs

    @torch.no_grad()
    def match(self, pairs, q_feats, match_path):
        """hloc match_features.main と同じ照合をして h5 に書く。pairs は [(クエリ, DB)]。"""
        from hloc.utils.io import names_to_pair
        model = self._model("match")
        results = []
        done = set()
        with h5py.File(str(self.db_features), "r", libver="latest") as db_fd:
            todo = []
            for q, r in pairs:
                if (q, r) in done or (r, q) in done:   # hloc の find_unique_new_pairs と同じ重複除き
                    continue
                done.add((q, r))
                todo.append((q, r, self._db_feature(db_fd, r)))
        for q, r, ref in todo:
            data = {}
            for suffix, feats in (("0", q_feats[q]), ("1", ref)):
                for k, v in feats.items():
                    data[k + suffix] = torch.from_numpy(np.asarray(v)).float()
                data["image" + suffix] = torch.empty((1,) + tuple(feats["image_size"])[::-1])
            # DataLoader（batch 1）がつける次元。"image" で始まる鍵（image0・image_size0 など）は
            # hloc と同じく CPU に残す
            data = {k: (v[None] if k.startswith("image") else v[None].to(self.device, non_blocking=True))
                    for k, v in data.items()}
            pred = model(data)
            m = {"matches0": pred["matches0"][0].cpu().short().numpy()}
            if "matching_scores0" in pred:
                m["matching_scores0"] = pred["matching_scores0"][0].cpu().half().numpy()
            results.append((names_to_pair(q, r), m))
        match_path = Path(match_path)
        match_path.parent.mkdir(parents=True, exist_ok=True)
        with h5py.File(str(match_path), "a", libver="latest") as fd:
            for pair, m in results:
                _write_group(fd, pair, m)
        return match_path
