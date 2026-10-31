"""Quest から届く実験イベント（type=7）の保存と、model 側 CP の JSON への組み直し。"""
import json
import threading
from datetime import datetime, timezone
from pathlib import Path


def cp_store_filename(model_id):
    """model 側 CP の JSON ファイル名。

    Unity 側 `ControlPointStore.FileName` と**同じ規則**にすること。名前が食い違うと、
    Editor の「JSON を読み込んで反映」で人がファイルを選び直す羽目になる。
    """
    # C# の char.IsLetterOrDigit は Unicode 対応（日本語のアセット名もそのまま残る）ので、
    # ASCII に限定しないこと。Python の str.isalnum() がほぼ同じ判定になる。
    kept = [c if (c.isalnum() or c in "-_.") else "_"
            for c in (model_id or "default")]
    name = "".join(kept)
    return "controlpoints_" + (name if name else "default") + ".json"


class EventSink:
    """Quest から届く実験イベント（type=7）を保存する。

    **2 か所に書く。**

    1. `<out>/quest_events.jsonl`（トップレベル・正本）
       到着順にすべて追記する。イベントは試行の境目をまたいで流れる（試行終了直後の
       再送など）ので、「今のセッション」に入れる方式では、遅れて届いた行が次の試行の
       フォルダに紛れ込む。まずここに全部落とすことで、取りこぼしも取り違えも起きない。

    2. `<out>/session_*/quest_events.jsonl`（セッション別・複製）
       **到着時刻ではなく、行に載っている試行ラベルで振り分ける。** ラベルは
       `trial_start` で分かり、PC 側の `session_*/trial.json` と 1 対 1 に対応する。
       遅れて届いても正しいフォルダに入るので、1 の懸念は生じない。
       セッションフォルダを開けばその試行のキーフレームと経過が揃うので、
       試行単位で見たいときに扱いやすい。

    正本は 1 のほう。2 は「そのセッションに属すると確定した行」だけなので、
    試行に属さない行（session_start、**CP の測量**など）や、ラベルが未確定のうちに
    届いた行は入らない。測量は実験前に 1 回だけで、どの試行にも属さない（trial=0）ため、
    正本にだけ載るのが正しい。試行との対応は `trial_start.survey` が持つ。

    重複除去は (run, seq)。UDP の再送で同じ行が何度も来るため。
    `ack` は「連続して受け取れた seq」で、これを PONG に載せると Quest は
    穴の開いた分だけを再送できる。CP の測量は実験を通して 6 点しかなく、
    後から測り直すと座標系が変わってやり直しが効かないので、
    ベストエフォート（投げっぱなし）では足りない。
    """

    def __init__(self, path):
        self.path = Path(path)
        self._lock = threading.Lock()
        self._run = None
        self._seen = set()
        self._ack = 0
        self._count = 0
        self._last = None
        self._label_of = {}      # (run, trial) -> label（trial_start で覚える）
        self._dir_of = {}        # label -> session ディレクトリ
        self._cp = {}            # model -> model 側 CP の組み立て中バッファ（下記）

    def _session_dir(self, label):
        """ラベルから session ディレクトリを引く（trial.json を走査。結果はキャッシュ）。"""
        if label in self._dir_of:
            return self._dir_of[label]
        try:
            for d in sorted(self.path.parent.glob("session_*")):
                tj = d / "trial.json"
                if not tj.exists():
                    continue
                try:
                    lbl = json.loads(tj.read_text(encoding="utf-8")).get("label")
                except Exception:
                    continue
                if lbl:
                    self._dir_of[lbl] = d
        except Exception:
            pass
        return self._dir_of.get(label)

    def add(self, seq, payload):
        """1 イベントを取り込む。新規なら True。"""
        try:
            d = json.loads(payload.decode("utf-8"))
        except Exception:
            return False
        run = str(d.get("run", ""))
        with self._lock:
            if run != self._run:
                # アプリを再起動すると seq は 1 から振り直される。run が変われば数え直す。
                self._run, self._seen, self._ack, self._count = run, set(), 0, 0
                self._label_of = {}
                # CP の組み立てバッファも捨てる。前の起動の点が新しい編集結果に混ざると、
                # 「送ったつもりのない点」が JSON に残ってシーンへ戻ってしまう。
                self._cp = {}
            if seq in self._seen:
                return False
            self._seen.add(seq)
            while (self._ack + 1) in self._seen:
                self._ack += 1
            self._count += 1
            d.setdefault("seq", seq)
            if d.get("type") == "trial_start" and d.get("label"):
                self._label_of[(run, d.get("trial"))] = d["label"]
            label = self._label_of.get((run, d.get("trial")))
            cp_model = self._note_cp(d)
            line = json.dumps(d, ensure_ascii=False)
            self._last = d
        if cp_model:
            self._write_cp_json(cp_model)
        try:
            with open(self.path, "a", encoding="utf-8") as f:
                f.write(line + "\n")
        except Exception as e:
            print("[server] イベント書き出し失敗:", e)
            return False

        # セッション別の複製。**到着時刻ではなくラベルで振り分ける**ので、
        # 遅れて届いた行でも正しい試行のフォルダに入る。
        if label:
            sd = self._session_dir(label)
            if sd is not None:
                try:
                    with open(sd / "quest_events.jsonl", "a", encoding="utf-8") as f:
                        f.write(line + "\n")
                except Exception as e:
                    print("[server] セッション別イベント書き出し失敗:", e)
        return True

    @property
    def ack(self):
        with self._lock:
            return self._ack

    @property
    def count(self):
        with self._lock:
            return self._count

    # ---- model 側 CP（VR の中で置き直した結果）を JSON に組み直す ----------------
    #
    # Quest は `cp_model_edit_done`(phase=begin) → **1 点 1 行**の `cp_model_point` →
    # `cp_model_edit_done`(phase=end) の順で送る。1 行にまとめないのは UDP イベントの
    # 1100 B 制限のため（測量の cp_survey と同じ流儀）。
    #
    # ここで Unity の `ControlPointStore` と**同じ形**の JSON に戻すと、Editor の
    # Control Points ウィンドウの「JSON を読み込んで反映」でそのままシーンへ書き戻せる。
    # **adb pull が要らない**のがこの経路の値打ち（実機の控えは冗長化として別にある）。

    def _note_cp(self, d):
        """model 側 CP のイベントを溜める。書き出すべきなら model 名を返す。呼び出しはロック内。"""
        t = d.get("type")
        if t not in ("cp_model_point", "cp_model_edit_done"):
            return None
        model = str(d.get("model") or "default")
        st = self._cp.setdefault(model, {"pts": {}, "n": 0, "edited": 0, "done": False})

        if t == "cp_model_point":
            p = d.get("p")
            if not (isinstance(p, list) and len(p) >= 3):
                return None
            st["pts"][int(d.get("i", len(st["pts"])))] = {
                "name": str(d.get("name") or ""),
                "p": [float(p[0]), float(p[1]), float(p[2])],
            }
            # 締めが先に届いていたら（欠けた点が再送で後から埋まる）そのつど書き直す。
            # 書き直さないと、取りこぼした点だけ古い座標のまま JSON に残る。
            return model if st["done"] else None

        st["n"] = max(st["n"], int(d.get("points") or 0))
        st["edited"] = max(st["edited"], int(d.get("edited") or 0))
        if d.get("phase") == "end":
            st["done"] = True
            return model
        return None

    def _write_cp_json(self, model):
        """received/controlpoints_<model>.json を ControlPointStore と同じ形で書く。"""
        with self._lock:
            st = self._cp.get(model)
            if not st or not st["pts"]:
                return None
            have = sorted(st["pts"])
            pts = [st["pts"][i] for i in have]
            expected, edited = st["n"], st["edited"]

        out = {"modelId": model,
               "savedUtc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ"),
               "points": pts}
        path = self.path.parent / cp_store_filename(model)
        try:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(json.dumps(out, ensure_ascii=False, indent=2), encoding="utf-8")
        except Exception as e:
            print("[server] CP JSON 書き出し失敗:", e)
            return None

        # 欠けたまま黙って書くと、Editor に取り込んだとき「その点だけ古い」状態になる。
        # UDP の再送で後から埋まればもう一度書き直されるので、警告だけ出して待つ。
        if expected and len(pts) < expected:
            missing = [i for i in range(expected) if i not in set(have)]
            print(f"[server] CP JSON: {len(pts)}/{expected} 点しか届いていません "
                  f"(欠け: {missing}) — 再送を待っています: {path}")
        else:
            print(f"[server] CP JSON を書きました（{len(pts)} 点 / うち編集 {edited}）: {path}")
        return path

    def describe(self, d):
        """GUI/コンソール用の 1 行説明。"""
        t = d.get("type", "?")
        # CP の測量は実験前に 1 回だけ（試行ごとではない）。survey 番号で試行と対応づく。
        if t == "cp_survey":
            return f"CP 測量 {d.get('name')} (survey #{d.get('survey')})"
        if t == "cp_survey_start":
            return f"CP 測量 開始 ({d.get('points')} 点)"
        if t == "cp_survey_done":
            return (f"CP 測量 #{d.get('survey')} 完了 "
                    f"({d.get('points')} 点 / 広がり {d.get('span_m')} m)")
        if t == "cp_survey_cancel":
            return f"CP 測量 中断 ({d.get('done')} 点まで)"
        if t == "cp_survey_invalidated":
            return f"CP 測量 #{d.get('survey')} が無効化: {d.get('why')}"
        # model 側 CP の VR 内編集（位置合わせ前の準備。測量とは別物）
        if t == "cp_model_edit":
            a = d.get("action")
            if a == "start":
                return f"model CP 編集 開始 ({d.get('points')} 点)"
            if a == "end":
                return "model CP 編集 終了（変更なし）"
            return (f"model CP {d.get('name')} を置き直し "
                    f"(ビルド値から {float(d.get('moved_m') or 0) * 100:.1f} cm)")
        if t == "cp_model_edit_revert":
            return f"model CP {d.get('name')} をビルド値へ戻した"
        if t == "cp_model_point":
            return f"model CP 座標 {d.get('name')}"
        if t == "cp_model_edit_done":
            if d.get("phase") == "begin":
                return f"model CP 編集 完了 → 受信中 ({d.get('points')} 点)"
            return (f"model CP 編集 完了: {d.get('edited')}/{d.get('points')} 点を変更 → "
                    f"{cp_store_filename(str(d.get('model') or 'default'))}")
        if t == "trial_start":
            return f"試行開始 {d.get('label')}"
        if t == "trial_end":
            return f"試行終了 {d.get('label') or d.get('trial')}: {d.get('verdict')}"
        if t == "tracking_origin_changed":
            return f"トラッキング原点が移動 ({d.get('cause')})"
        return str(t)
