"""すべてのテストを順に実行する（server/ で `python -m tests`）。

各テストファイルは別プロセスで動かす（テストどうしで状態が混ざらないように）。
落ちたものだけ出力を表示し、1 つでも落ちたら終了コード 1。
"""
import os
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent


def main():
    names = sorted(p.stem for p in HERE.glob("test_*.py"))
    env = dict(os.environ, PYTHONIOENCODING="utf-8")
    failed = []
    for name in names:
        t0 = time.time()
        r = subprocess.run([sys.executable, "-m", f"tests.{name}"], cwd=HERE.parent, env=env,
                           capture_output=True, text=True, encoding="utf-8", errors="replace")
        ok = r.returncode == 0
        print(f"{'PASS' if ok else 'FAIL'}  {name}  ({time.time() - t0:.1f} s)", flush=True)
        if not ok:
            failed.append(name)
            print(r.stdout[-4000:])
            print(r.stderr[-4000:])
    print()
    if failed:
        print(f"{len(failed)} of {len(names)} test files FAILED: {', '.join(failed)}")
        sys.exit(1)
    print(f"ALL {len(names)} TEST FILES PASSED")


if __name__ == "__main__":
    main()
