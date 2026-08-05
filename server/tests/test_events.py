"""実験イベント（type=7）の受信・重複除去・ACK を検証する。

ここで守りたいこと:
  1. **CP 測量の実測値を落とさない。** 測量は実験前に 1 回だけ・全部で 6 点しかなく、
     後から測り直すと座標系が変わってやり直しが効かないので、UDP の取りこぼしを
     ACK＋再送で埋められる形になっていること。
  2. 再送で同じ行が何度届いても、保存は 1 回だけ（重複除去）。
  3. ACK は「連続して受信済みの seq」であること。穴が開いたら、そこで止まる
     （止まらないと Quest が未達分を捨ててしまう）。
  4. アプリを再起動して seq が 1 に戻っても、前の run のぶんと混ざらない。
  5. 測量の行（trial=0）は正本にだけ入り、どのセッションフォルダにも複製されない。
     測量はどの試行にも属さないので、特定の試行のフォルダに入れると誤解の元になる。
  6. VR 内で置き直した model 側 CP が、Editor がそのまま取り込める JSON になる
     （`ControlPointStore` と同じ鍵名・並び）。ここが崩れると adb pull が必要になる。

実行: python -m tests.test_events
"""
import json
import socket
import sys
import tempfile
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import importlib.util

_spec = importlib.util.spec_from_file_location(
    "runmod", Path(__file__).resolve().parents[1] / "run.py")
run = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(run)

from vptserver.protocol import TYPE_EVENT, TYPE_PING, build  # noqa: E402
from vptserver.udp_receiver import UdpReceiver  # noqa: E402
from vptserver.reassembler import Reassembler  # noqa: E402

_fails = []


def check(cond, label):
    print(("  OK   " if cond else "  FAIL ") + label)
    if not cond:
        _fails.append(label)


def ev(run_id, seq, typ="cp_survey", **kw):
    d = {"run": run_id, "seq": seq, "t": 1.0, "trial": 1, "mode": "collect", "type": typ}
    d.update(kw)
    return json.dumps(d, ensure_ascii=False).encode("utf-8")


def test_sink_dedup_and_ack():
    print("== EventSink: 重複除去と ACK ==")
    with tempfile.TemporaryDirectory() as d:
        sink = run.EventSink(Path(d) / "quest_events.jsonl")
        check(sink.ack == 0 and sink.count == 0, "初期状態は ack=0, count=0")

        check(sink.add(1, ev("r1", 1, name="CP_01")) is True, "1 件目を受け取る")
        check(sink.add(1, ev("r1", 1, name="CP_01")) is False, "同じ seq の再送は保存しない")
        check(sink.count == 1, "保存件数は 1 のまま")
        check(sink.ack == 1, "ack=1")

        # 2 を落として 3 が先に届く（UDP は順序も保証しない）
        sink.add(3, ev("r1", 3, name="CP_03"))
        check(sink.ack == 1, "穴が開いている間 ack は 1 のまま（Quest が 2 を再送できる）")
        sink.add(2, ev("r1", 2, name="CP_02"))
        check(sink.ack == 3, "穴が埋まったら ack が 3 まで進む")
        check(sink.count == 3, "保存は 3 件")

        lines = [json.loads(x) for x in
                 (Path(d) / "quest_events.jsonl").read_text(encoding="utf-8").splitlines()]
        check(len(lines) == 3, "ファイルにも 3 行（重複は書かれない）")
        check([x["name"] for x in lines] == ["CP_01", "CP_03", "CP_02"],
              "到着順に追記される（順序は seq で復元する）")

        # アプリ再起動: run が変わり seq は 1 から振り直される
        sink.add(1, ev("r2", 1, name="CP_01"))
        check(sink.ack == 1 and sink.count == 1, "run が変われば数え直す（前の run と混ざらない）")
        lines = (Path(d) / "quest_events.jsonl").read_text(encoding="utf-8").splitlines()
        check(len(lines) == 4, "ファイルには追記され続ける（run で区別できる）")


def test_session_demux():
    """セッション別の複製が、**到着時刻ではなくラベル**で振り分けられるか。

    ここが到着時刻ベースだと、試行終了後に再送されてきた行が次の試行のフォルダに
    紛れ込む。ラベルで振り分ければ、遅れて届いても正しいフォルダに入る。
    """
    print("== セッション別の複製（ラベルで振り分け）==")
    with tempfile.TemporaryDirectory() as d:
        base = Path(d)
        for name, label in [("session_A", "trial-01"), ("session_B", "trial-02")]:
            (base / name).mkdir()
            (base / name / "trial.json").write_text(
                json.dumps({"label": label}), encoding="utf-8")

        sink = run.EventSink(base / "quest_events.jsonl")
        sink.add(1, ev("r1", 1, typ="trial_start", label="trial-01", survey=1))
        sink.add(2, ev("r1", 2, typ="auto_aligned", mark="A1"))
        sink.add(3, ev("r1", 3, typ="trial_start", label="trial-02", trial=2, survey=1))
        sink.add(4, ev("r1", 4, typ="auto_aligned", mark="B1", trial=2))
        # 試行 2 が始まったあとに、試行 1 の行が遅れて届く（UDP の再送）
        sink.add(5, ev("r1", 5, typ="auto_aligned", mark="A2"))

        def marks(p):
            if not p.exists():
                return []
            return [(json.loads(x)["trial"], json.loads(x).get("mark"))
                    for x in p.read_text(encoding="utf-8").splitlines()]

        top = marks(base / "quest_events.jsonl")
        a = marks(base / "session_A" / "quest_events.jsonl")
        b = marks(base / "session_B" / "quest_events.jsonl")

        check(len(top) == 5, f"正本には全 5 行（実際 {len(top)}）")
        check([x for x in a if x[1]] == [(1, "A1"), (1, "A2")],
              f"session_A には試行 1 の行が 2 件（実際 {[x for x in a if x[1]]}）")
        check([x for x in b if x[1]] == [(2, "B1")],
              f"session_B には試行 2 の行が 1 件（実際 {[x for x in b if x[1]]}）")
        check(all(t == 1 for t, _ in a) and all(t == 2 for t, _ in b),
              "遅れて届いた行も正しい試行のフォルダに入る（到着順では判断していない）")


def test_survey_stays_in_canonical():
    """測量の行（trial=0）は正本にだけ入り、セッションフォルダには複製されない。

    測量は実験の前に 1 回だけ行うので、どの試行にも属さない。特定の試行のフォルダに
    入れてしまうと「この試行で測った CP」に見えてしまう。試行との対応は
    trial_start.survey が持つ（→ analysis/accuracy_vs_time.py の trial_survey_key）。
    """
    print("== 測量の行は正本だけに入る ==")
    with tempfile.TemporaryDirectory() as d:
        base = Path(d)
        (base / "session_A").mkdir()
        (base / "session_A" / "trial.json").write_text(
            json.dumps({"label": "trial-01"}), encoding="utf-8")

        sink = run.EventSink(base / "quest_events.jsonl")
        # 実験前の測量（trial=0）→ そのあとで最初の試行が始まる
        sink.add(1, ev("r1", 1, typ="cp_survey_start", trial=0, points=6))
        sink.add(2, ev("r1", 2, typ="cp_survey", trial=0, survey=1, name="CP_01"))
        sink.add(3, ev("r1", 3, typ="cp_survey_done", trial=0, survey=1, points=6))
        sink.add(4, ev("r1", 4, typ="trial_start", label="trial-01", survey=1))
        sink.add(5, ev("r1", 5, typ="auto_aligned"))

        def lines(p):
            if not p.exists():
                return []
            return [json.loads(x) for x in p.read_text(encoding="utf-8").splitlines()]

        top = lines(base / "quest_events.jsonl")
        a = lines(base / "session_A" / "quest_events.jsonl")

        check(len(top) == 5, f"正本には測量 3 行＋試行 2 行の計 5 行（実際 {len(top)}）")
        check([x["type"] for x in top].count("cp_survey") == 1, "正本に測量の実測値がある")
        check(all(x["trial"] == 1 for x in a),
              f"session_A に入るのは試行 1 の行だけ（実際 {[x['trial'] for x in a]}）")
        check(not any(x["type"].startswith("cp_survey") for x in a),
              "測量の行はセッションフォルダに複製されない")
        check(top[3].get("survey") == 1,
              "trial_start が survey 番号を持つ（試行と測量の対応はこれで取る）")


def cp_pt(run_id, seq, i, name, p, model="scene", edited=False):
    return ev(run_id, seq, typ="cp_model_point", trial=0,
              model=model, i=i, name=name, p=list(p), edited=edited)


def cp_done(run_id, seq, phase, n, edited, model="scene"):
    return ev(run_id, seq, typ="cp_model_edit_done", trial=0,
              phase=phase, model=model, points=n, edited=edited)


def test_model_cp_json():
    """VR で置き直した model 側 CP が ControlPointStore と同じ JSON になるか。

    Editor の「JSON を読み込んで反映」がそのまま使える形でないと、adb pull なしで
    シーンへ戻すという狙いが崩れる。鍵名（modelId / points / name / p）は
    Unity の ControlPointStore に一致させること。
    """
    print("== model 側 CP → controlpoints_<model>.json ==")
    with tempfile.TemporaryDirectory() as d:
        base = Path(d)
        sink = run.EventSink(base / "quest_events.jsonl")
        out = base / "controlpoints_scene.json"

        sink.add(1, cp_done("r1", 1, "begin", 3, 2))
        sink.add(2, cp_pt("r1", 2, 0, "CP_01", (1.0, 2.0, 3.0), edited=True))
        sink.add(3, cp_pt("r1", 3, 1, "CP_02", (-0.5, 0.25, 4.0), edited=True))
        check(not out.exists(), "締めが来るまでは書かない（途中の状態を Editor に取り込ませない）")

        sink.add(4, cp_pt("r1", 4, 2, "CP_03", (0.0, 0.0, 0.0)))
        sink.add(5, cp_done("r1", 5, "end", 3, 2))
        check(out.exists(), f"{out.name} が書かれた")

        j = json.loads(out.read_text(encoding="utf-8"))
        check(j.get("modelId") == "scene", "modelId が入る")
        check(bool(j.get("savedUtc")), "savedUtc が入る")
        check([e["name"] for e in j["points"]] == ["CP_01", "CP_02", "CP_03"],
              f"i の順に並ぶ（実際 {[e['name'] for e in j['points']]}）")
        check(j["points"][1]["p"] == [-0.5, 0.25, 4.0], "座標が float 3 要素で入る")
        check(all(set(e) == {"name", "p"} for e in j["points"]),
              "余計な鍵を書かない（ControlPointStore.Entry は name と p だけ）")

        # 取りこぼした点が締めのあとで届く。書き直さないと、その点だけ古い座標のまま残る。
        sink.add(6, cp_pt("r1", 6, 1, "CP_02", (9.0, 9.0, 9.0), edited=True))
        j = json.loads(out.read_text(encoding="utf-8"))
        check(j["points"][1]["p"] == [9.0, 9.0, 9.0],
              "締めのあとに届いた再送でも JSON を書き直す")

        # アプリを再起動して別の編集を送る。前の run の点が混ざってはいけない
        # （「送ったつもりのない点」がシーンへ戻ってしまう）。
        sink.add(1, cp_done("r2", 1, "begin", 1, 1))
        sink.add(2, cp_pt("r2", 2, 0, "CP_01", (7.0, 7.0, 7.0), edited=True))
        sink.add(3, cp_done("r2", 3, "end", 1, 1))
        j = json.loads(out.read_text(encoding="utf-8"))
        check([e["name"] for e in j["points"]] == ["CP_01"],
              f"run が変われば前の点を捨てる（実際 {[e['name'] for e in j['points']]}）")

        # ファイル名の規則が Unity の ControlPointStore.FileName と揃っているか
        check(run.cp_store_filename("my scene/1") == "controlpoints_my_scene_1.json",
              "使えない文字は _ に潰す")
        check(run.cp_store_filename("") == "controlpoints_default.json", "空なら default")


def test_sink_bad_payload():
    print("== 壊れたペイロード ==")
    with tempfile.TemporaryDirectory() as d:
        sink = run.EventSink(Path(d) / "quest_events.jsonl")
        check(sink.add(1, b"not json") is False, "JSON でなければ捨てる（例外を出さない）")
        check(sink.count == 0, "件数は増えない")


def test_end_to_end_socket():
    """実ソケットで udp_loop に流し、保存と PONG の ack を確認する。"""
    print("== 実ソケット: 受信 → 保存 → PONG で ACK ==")
    import threading

    with tempfile.TemporaryDirectory() as d:
        sink = run.EventSink(Path(d) / "quest_events.jsonl")

        # 空きポートを 2 つ取る
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.bind(("127.0.0.1", 0))
        data_port = s.getsockname()[1]; s.close()
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.bind(("127.0.0.1", 0))
        result_port = s.getsockname()[1]; s.close()

        rx = UdpReceiver(data_port)
        asm = Reassembler()

        class Sess:
            dir = d
        th = threading.Thread(target=run.udp_loop,
                              args=(rx, asm, Sess(), result_port, sink), daemon=True)
        th.start()
        time.sleep(0.3)

        # PONG を受ける口
        pong_rx = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        pong_rx.bind(("127.0.0.1", result_port))
        pong_rx.settimeout(3.0)

        tx = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        for seq in (1, 2, 2, 3):        # 2 は再送
            tx.sendto(build(TYPE_EVENT, ev("r1", seq, trial=0, survey=1,
                                           name=f"CP_{seq:02d}"), req=seq),
                      ("127.0.0.1", data_port))
        time.sleep(0.4)
        check(sink.count == 3, f"3 件が保存された（重複は 1 回・実際 {sink.count}）")

        tx.sendto(build(TYPE_PING, b"{}", req=42), ("127.0.0.1", data_port))
        try:
            buf, _ = pong_rx.recvfrom(4096)
            body = json.loads(buf[13:].decode("utf-8"))
            check(body.get("ev_ack") == 3, f"PONG の ev_ack=3（実際 {body.get('ev_ack')}）")
            check(body.get("ev_n") == 3, f"PONG の ev_n=3（実際 {body.get('ev_n')}）")
        except socket.timeout:
            check(False, "PONG が返ってこない")

        rx.close(); pong_rx.close(); tx.close()
        time.sleep(0.2)


if __name__ == "__main__":
    test_sink_dedup_and_ack()
    test_session_demux()
    test_survey_stays_in_canonical()
    test_model_cp_json()
    test_sink_bad_payload()
    test_end_to_end_socket()
    print()
    if _fails:
        print(f"{len(_fails)} FAILED:")
        for f in _fails:
            print("  -", f)
        sys.exit(1)
    print("ALL EVENT TESTS PASSED")
