"""CV 環境（WSL2）の確認と、モデルの重みの事前取得。setup_wsl.sh が最後に呼ぶ。

ここで守りたいこと（**初めての PC・展示会場でも動く**ための前提）:
  1. localize が使うものが全部 import できる（numpy / cv2 / pycolmap / torch / hloc / lightglue）。
     cv2 は OS のライブラリ（libGL・glib）が無いと import の時点で落ちるので、ここで分かるようにする。
  2. GPU で動くか。CPU でも動くが 1 枚に十数秒以上かかり、位置合わせに何分もかかる。
     「動くが遅い」が最も気づきにくい失敗なので、セットアップの最後に必ず出す。
  3. **重み（NetVLAD・LightGlue）を今ダウンロードしておく。** hloc は初回の使用時にネットから取るので、
     インターネットの無い展示会場で初めて localize を動かすと、そこで止まる。
     取得先は ~/.cache/torch/hub（SuperPoint の重みは hloc の third_party に同梱）。

実行（WSL2 の CV 環境）: python -m vptserver.cv.check_env
終了コード: 0 = 使える（CPU だけでも 0。警告を出す）/ 1 = 足りないものがある
"""
import importlib
import sys

# localize.py / hloc_db.py と同じ設定名（重みはこの 3 つ分を取得する）
from ..scenes import FEATURE_CONF, MATCHER_CONF, RETRIEVAL_CONF

MODULES = [
    ("numpy", "numpy"),
    ("cv2", "opencv-python"),
    ("pycolmap", "pycolmap"),
    ("torch", "torch"),
    ("hloc", "hloc"),
    ("lightglue", "lightglue"),
]


def check_imports():
    ok = True
    for mod, pkg in MODULES:
        try:
            m = importlib.import_module(mod)
            print(f"  OK   {pkg:14s} {getattr(m, '__version__', '')}")
        except Exception as e:
            ok = False
            print(f"  FAIL {pkg:14s} {type(e).__name__}: {e}")
            if mod == "cv2" and "lib" in str(e):
                print("       (OS のライブラリが不足しています: sudo apt-get install -y libgl1 libglib2.0-0t64)")
    return ok


def check_cuda():
    import torch
    if torch.cuda.is_available():
        print(f"  OK   CUDA: {torch.cuda.get_device_name(0)} (torch {torch.__version__}, CUDA {torch.version.cuda})")
        return True
    print(f"  WARN CUDA: not available (torch {torch.__version__}) — CV runs on CPU and will be very slow.")
    print("       CUDA が使えません。CV は CPU で動き、とても遅くなります。")
    print("       Windows 側に NVIDIA の最新ドライバを入れてから、setup_windows.bat をもう一度実行してください。")
    return False


def prefetch_weights():
    """3 つのモデルを一度作る。作る時点で重みが無ければダウンロードされる（2 回目以降はキャッシュを読むだけ）。"""
    from hloc import extract_features, extractors, match_features, matchers
    from hloc.utils.base_model import dynamic_load

    ok = True
    for confs, root, name in [(extract_features.confs, extractors, FEATURE_CONF),
                              (extract_features.confs, extractors, RETRIEVAL_CONF),
                              (match_features.confs, matchers, MATCHER_CONF)]:
        try:
            conf = confs[name]["model"]
            dynamic_load(root, conf["name"])(conf)
            print(f"  OK   weights: {name}")
        except Exception as e:
            ok = False
            print(f"  FAIL weights: {name}: {type(e).__name__}: {e}")
            print("       重みを取得できません。インターネットに繋がっているか確認して、もう一度実行してください。")
    return ok


def main():
    print("== CV environment (WSL2) ==")
    if not check_imports():
        print("\nCV environment is incomplete. / CV 環境が不完全です。")
        return 1
    check_cuda()
    print("== Model weights (downloaded once, used offline afterwards) ==")
    if not prefetch_weights():
        return 1
    print("\nCV environment OK. / CV 環境の準備ができました。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
