"""テストの共通部品。

    from tests._check import check, finish
    check(cond, "説明")              # OK / FAIL を出して続ける（1 つ落ちても残りを見る）
    ...
    finish("ALL GATE TESTS PASSED")  # 失敗があれば一覧を出して終了コード 1

各テストは別プロセスで動かすので（`python -m tests.test_x` / `python -m tests`）、失敗の記録は
プロセスごとに独立している。
"""
import sys

_fails = []


def check(cond, label):
    print(("  OK   " if cond else "  FAIL ") + label)
    if not cond:
        _fails.append(label)
    return bool(cond)


def finish(passed_message):
    print()
    if _fails:
        print(f"{len(_fails)} FAILED:")
        for f in _fails:
            print("  -", f)
        sys.exit(1)
    print(passed_message)
