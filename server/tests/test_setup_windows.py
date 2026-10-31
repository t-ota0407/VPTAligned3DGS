"""初めての Windows のセットアップ（vptserver/setup_windows.py）の判断の部分の検証。

ここで守りたいこと:
  1. CV に使うディストリを、CV 環境のあるもの → 動作確認済みの Ubuntu-24.04 の順に選ぶ。
     既定の別ディストリ（Python の版が違う）に勝手に入れない。
  2. シーンの DB が「できている / 作りかけ / 未作成 / COLMAP ではない」を見分ける。
     作りかけ（途中で止めた）を「できている」と扱うと、localize が起動後に落ちる。
  3. ファイアウォール: Python のブロック規則は必ず外す（許可規則より優先されるので、
     規則を足しても Quest が届かない）。パブリックのネットワークは利用者が選んだときだけ許可する。
  4. 管理者で実行する PowerShell に、規則名の ' がそのまま入らない（構文が壊れない）。
  5. 入力の無い自動実行では「はい」と答えない（管理者の変更や長い DB 構築を勝手に始めない）。

WSL・ファイアウォールを実際に操作する部分はここでは試さない（実機で確認する）。
実行: python -m tests.test_setup_windows
"""
import builtins
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from tests._check import check, finish  # noqa: E402

from vptserver import scenes, setup_windows as S  # noqa: E402


def test_plan_distro():
    print("== CV に使うディストリ ==")
    names = ["Ubuntu-20.04", "Ubuntu-24.04"]
    check(S.plan_distro(names, "Ubuntu-20.04") == "Ubuntu-20.04", "CV 環境のあるものをそのまま使う")
    check(S.plan_distro(names, None) == "Ubuntu-24.04", "どこにも無ければ動作確認済みの Ubuntu-24.04 に作る")
    check(S.plan_distro(["Ubuntu-20.04", "Debian"], None) is None,
          "Ubuntu-24.04 が無ければ None（既定の別ディストリに勝手に入れない）")
    check(S.plan_distro([], None) is None, "ディストリが無い → None（入れる）")


def test_scene_state():
    print("== シーンの DB の状態 ==")
    with tempfile.TemporaryDirectory() as d:
        s = Path(d) / "scene"
        check(scenes.scene_state(s) == "invalid", "空 → COLMAP ではない")
        (s / "images").mkdir(parents=True)
        (s / "sparse" / "0").mkdir(parents=True)
        (s / "sparse" / "0" / "images.bin").write_bytes(b"")
        check(scenes.scene_state(s) == "raw", "images/ と sparse/0 だけ → DB 未作成")
        (s / "hloc" / scenes.SUBMODEL_DIRNAME).mkdir(parents=True)
        check(scenes.scene_state(s) == "partial",
              "SfM はあるが NetVLAD が無い → 作りかけ（最後の段階の前で止まった）")
        (s / "hloc" / scenes.NETVLAD_FILE).write_bytes(b"")
        check(scenes.scene_state(s) == "built", "NetVLAD まである → できている")

        b = Path(d) / "built_only"
        (b / "hloc" / scenes.SUBMODEL_DIRNAME).mkdir(parents=True)
        (b / "hloc" / scenes.NETVLAD_FILE).write_bytes(b"")
        check(scenes.scene_state(b) == "built", "DB だけ配布されたシーン（images/ 無し）も使える")

        # run.py のシーン一覧と localize の自動検出は、DB ができているものだけを数える
        # （作りかけのシーンを選ぶと localize が起動時に落ちる）
        p = Path(d) / "partial"
        (p / "images").mkdir(parents=True)
        (p / "sparse" / "0").mkdir(parents=True)
        (p / "sparse" / "0" / "images.bin").write_bytes(b"")
        (p / "hloc" / scenes.SUBMODEL_DIRNAME).mkdir(parents=True)
        built = scenes.list_built_scenes(d)
        check([Path(x).name for x in built] == ["built_only", "scene"],
              "一覧は DB ができているものだけ（作りかけ・空は出さない）")
        check(all("\\" not in x for x in built), "一覧のパスは / 区切り（WSL の localize も読む）")
        check(scenes.autodetect_scene(d) is None, "2 つあれば自動検出しない（曖昧）")
        check(scenes.autodetect_scene(str(b)) is None, "シーンの親フォルダでなければ空")


# 作者の PC の実際の出力（パブリックの学内 Wi-Fi・python に過去の許可規則）を縮めたもの
FW_PUBLIC = (
    "NET|Public|bld2-guest 4\r\n"
    "APP|Allow|Public|UDP Query User{7337C9AE}C:\\python312\\python.exe|Python\r\n"
)
FW_BLOCKED = (
    "RULE|True|Domain, Private\r\n"
    "NET|Private|home\r\n"
    "APP|Allow|Private|UDP Query User{AAAA}C:\\python312\\python.exe|Python\r\n"
    "APP|Block|Public|UDP Query User{BBBB}C:\\python312\\python.exe|Python\r\n"
)


def test_firewall():
    print("== ファイアウォール ==")
    st = S.parse_firewall_state(FW_PUBLIC)
    check(st["rule"] is None and S.on_public_network(st), "規則が無く、パブリックのネットワーク")
    p, b = S.plan_firewall(st, allow_public=True)
    check(p == ["Domain", "Private", "Public"] and b == [], "パブリックも許可すると選んだら 3 つのプロファイルで作る")
    p, b = S.plan_firewall(st, allow_public=False)
    check(p == ["Domain", "Private"], "選ばなければパブリックは開けない")

    st = S.parse_firewall_state(FW_BLOCKED)
    check(st["rule"] == {"enabled": True, "profile": "Domain, Private"}, "既存の規則を読める")
    p, b = S.plan_firewall(st, allow_public=False)
    check(p is None, "規則がプライベートを覆っていれば作り直さない")
    check(b == ["UDP Query User{BBBB}C:\\python312\\python.exe"],
          "python のブロック規則は外す（許可規則より優先され、Quest が届かなくなる）")
    p, _ = S.plan_firewall(st, allow_public=True)
    check(p == ["Domain", "Private", "Public"], "パブリックも要るなら規則のプロファイルを広げる")
    p, _ = S.plan_firewall(S.parse_firewall_state("RULE|False|Any\n"), allow_public=True)
    check(p is not None, "無効にされた規則は有効に戻す")
    p, _ = S.plan_firewall(S.parse_firewall_state("RULE|True|Any\n"), allow_public=True)
    check(p is None, "Any（全プロファイル）なら今のままでよい")

    s = S.firewall_fix_script(["Domain", "Private"], ["UDP Query User{X}C:\\users\\o'brien\\python.exe"])
    check("'UDP Query User{X}C:\\users\\o''brien\\python.exe'" in s,
          "規則名の ' は二重にする（管理者で実行する PowerShell が壊れない）")
    check(f"-LocalPort {S.DATA_PORT}" in s and "-Protocol UDP" in s and "-Direction Inbound" in s,
          "受信の UDP 50000 だけを許可する")
    check("New-NetFirewallRule" not in S.firewall_fix_script(None, ["x"]), "規則が足りていればブロック規則だけを外す")


def test_no_input_means_no():
    print("== 入力の無い自動実行 ==")
    orig = builtins.input

    def eof(_prompt=""):
        raise EOFError

    builtins.input = eof
    try:
        check(S.ask_yes("go?", default=True) is False, "EOF なら既定が「はい」でも実行しない")
    finally:
        builtins.input = orig
    builtins.input = lambda _p="": ""
    try:
        check(S.ask_yes("go?", default=True) is True and S.ask_yes("go?", default=False) is False,
              "Enter だけなら既定に従う")
    finally:
        builtins.input = orig


if __name__ == "__main__":
    test_plan_distro()
    test_scene_state()
    test_firewall()
    test_no_input_means_no()
    finish("ALL SETUP TESTS PASSED")
