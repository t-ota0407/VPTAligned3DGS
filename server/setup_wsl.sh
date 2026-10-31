#!/usr/bin/env bash
# VPTAligned3DGS — WSL2 側の CV 環境（~/vptcv）を作る。setup_windows.bat から呼ばれる（再実行しても安全）。
#
# 単体で使う場合（WSL2 のターミナルで）:
#   bash /mnt/c/<repo への WSL2 パス>/server/setup_wsl.sh
# 引数: [server フォルダの WSL パス]（省略時はこのスクリプトのあるフォルダ）
# 環境変数:
#   VPT_CV_VENV    venv の場所（既定 ~/vptcv。run.py の --cv-venv と合わせる）
#   VPT_HLOC_DIR   hloc を置く場所（既定 ~/hloc）
#   VPT_TORCH_CPU  1 なら GPU があっても CPU 版 torch を入れる（通常は使わない）
#
# 方針:
#   - venv と hloc は WSL のホーム（ext4）に置く。/mnt/c 上は I/O が遅い。
#   - 版は動作確認済みのものに固定する（torch 2.6.0 / hloc c13273b / LightGlue eb42fee / constraints-cv.txt）。
#     公開後に依存の新版が出ても、初めての PC で同じ組み合わせを再現するため。
#   - すでに入っているものは入れ直さない。ただし GPU があるのに CPU 版 torch なら入れ直す
#     （NVIDIA ドライバを後から入れて再実行したとき、GPU を使えるようにする）。
#   - 最後に重み（NetVLAD・LightGlue）を取得しておく。展示会場にインターネットが無くても動くように。
set -euo pipefail

SERVER_DIR="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)}"
VENV="${VPT_CV_VENV:-$HOME/vptcv}"
HLOC_DIR="${VPT_HLOC_DIR:-$HOME/hloc}"

# 動作確認済みの版（server/README.md「セットアップ」と同期）
TORCH_VER=2.6.0
TORCHVISION_VER=0.21.0
TORCH_CUDA_INDEX=https://download.pytorch.org/whl/cu124
TORCH_CPU_INDEX=https://download.pytorch.org/whl/cpu
HLOC_URL=https://github.com/cvg/Hierarchical-Localization.git
HLOC_REV=c13273bd0ecc2917a35910fd843712a1c6243193        # v1.4-34（hloc 1.5）
LIGHTGLUE_REV=eb42fee2d71449efb0aa5c10549752b5d75384d8
TESTED_PY=3.12                                            # Ubuntu 24.04 の python3

say()  { printf '\n==> %s\n' "$*"; }
warn() { printf '\n[!] %s\n' "$*" >&2; }
die()  { printf '\n[x] %s\n' "$*" >&2; exit 1; }

[ -f "$SERVER_DIR/requirements-cv.txt" ] || die "server folder not found: $SERVER_DIR"
command -v apt-get >/dev/null 2>&1 || die "apt-get not found. This setup supports Ubuntu only (Ubuntu-24.04 recommended)."
command -v python3 >/dev/null 2>&1 || die "python3 not found in this WSL distro."

SUDO=sudo
[ "$(id -u)" = 0 ] && SUDO=""

say "VPTAligned3DGS: WSL2 CV setup"
echo "    distro : $(. /etc/os-release && echo "$PRETTY_NAME")"
echo "    server : $SERVER_DIR"
echo "    venv   : $VENV"
echo "    hloc   : $HLOC_DIR"

# ---- 1) OS のパッケージ（足りないものだけ） ----
# python3-venv: Ubuntu では venv（ensurepip）が別パッケージ。libgl1 / glib: opencv-python の import に要る。
need=()
python3 -c 'import ensurepip, venv' >/dev/null 2>&1 || need+=(python3-venv)
command -v git >/dev/null 2>&1 || need+=(git)
ldconfig -p 2>/dev/null | grep -q 'libGL\.so\.1' || need+=(libgl1)
glib_missing=0
ldconfig -p 2>/dev/null | grep -q 'libgthread-2\.0\.so\.0' || glib_missing=1
if [ ${#need[@]} -gt 0 ] || [ "$glib_missing" = 1 ]; then
  say "Installing OS packages (sudo may ask for your Ubuntu password)"
  $SUDO apt-get update
  if [ "$glib_missing" = 1 ]; then
    # Ubuntu 24.04 では libglib2.0-0t64、それより前は libglib2.0-0
    if apt-cache show libglib2.0-0t64 >/dev/null 2>&1; then need+=(libglib2.0-0t64); else need+=(libglib2.0-0); fi
  fi
  $SUDO env DEBIAN_FRONTEND=noninteractive apt-get install -y "${need[@]}"
else
  say "OS packages: OK"
fi

# ---- 2) venv ----
PYV="$(python3 -c 'import sys; print("%d.%d" % sys.version_info[:2])')"
CONSTRAINT=(-c "$SERVER_DIR/constraints-cv.txt")
if [ "$PYV" != "$TESTED_PY" ]; then
  warn "python3 is $PYV (tested: $TESTED_PY on Ubuntu 24.04). Installing without version pins; it may not work."
  CONSTRAINT=()
fi
if [ ! -x "$VENV/bin/python" ]; then
  say "Creating venv: $VENV"
  python3 -m venv "$VENV"
fi
PY="$VENV/bin/python"
PIP=("$PY" -m pip --disable-pip-version-check)
"${PIP[@]}" install -q -U pip wheel

# ---- 3) CV の依存（numpy / opencv / pycolmap） ----
# torch より先に入れる。後だと torchvision が最新の numpy を引き込み、ここで固定版へ入れ直すことになる
say "Installing CV requirements"
"${PIP[@]}" install -r "$SERVER_DIR/requirements-cv.txt" "${CONSTRAINT[@]}"

# ---- 4) torch（GPU があれば CUDA 版） ----
has_gpu=0
if [ -z "${VPT_TORCH_CPU:-}" ] && { command -v nvidia-smi >/dev/null 2>&1 || [ -x /usr/lib/wsl/lib/nvidia-smi ]; }; then
  has_gpu=1
fi
torch_state="$("$PY" -c 'import torch; print("cuda" if torch.version.cuda else "cpu")' 2>/dev/null || echo none)"
if [ "$torch_state" = none ] || { [ "$has_gpu" = 1 ] && [ "$torch_state" = cpu ]; }; then
  if [ "$has_gpu" = 1 ]; then
    say "Installing torch $TORCH_VER (CUDA 12.4). This downloads about 3 GB."
    "${PIP[@]}" install "torch==$TORCH_VER" "torchvision==$TORCHVISION_VER" --index-url "$TORCH_CUDA_INDEX"
  else
    warn "No NVIDIA GPU visible from WSL (nvidia-smi not found). Installing CPU torch: localization will be VERY slow.
    Install the latest NVIDIA driver on Windows, then run setup_windows.bat again to switch to the GPU build."
    "${PIP[@]}" install "torch==$TORCH_VER" "torchvision==$TORCHVISION_VER" --index-url "$TORCH_CPU_INDEX"
  fi
else
  say "torch: already installed ($torch_state build)"
fi

# ---- 5) LightGlue と hloc（PyPI に無い → GitHub のコミットで固定） ----
if ! "$PY" -c 'import lightglue' >/dev/null 2>&1; then
  say "Installing LightGlue ($LIGHTGLUE_REV)"
  "${PIP[@]}" install "lightglue @ git+https://github.com/cvg/LightGlue@$LIGHTGLUE_REV" "${CONSTRAINT[@]}"
fi
if "$PY" -c 'import hloc' >/dev/null 2>&1; then
  say "hloc: already installed ($("$PY" -c 'import hloc, os; print(os.path.dirname(os.path.dirname(hloc.__file__)))'))"
else
  if [ ! -d "$HLOC_DIR/.git" ]; then
    [ -e "$HLOC_DIR" ] && die "$HLOC_DIR exists but is not a git checkout. Move it away, or set VPT_HLOC_DIR."
    say "Cloning hloc into $HLOC_DIR"
    git clone "$HLOC_URL" "$HLOC_DIR"
  fi
  if [ "$(git -C "$HLOC_DIR" rev-parse HEAD)" != "$HLOC_REV" ]; then
    if git -C "$HLOC_DIR" diff --quiet && git -C "$HLOC_DIR" diff --cached --quiet; then
      git -C "$HLOC_DIR" cat-file -e "$HLOC_REV^{commit}" 2>/dev/null || git -C "$HLOC_DIR" fetch origin
      git -C "$HLOC_DIR" checkout --quiet "$HLOC_REV"
    else
      warn "$HLOC_DIR has local changes; using it as-is instead of the tested commit ${HLOC_REV:0:7}."
    fi
  fi
  git -C "$HLOC_DIR" submodule update --init --recursive
  say "Installing hloc requirements"
  # hloc の requirements から LightGlue（上で固定したもの）を除いて入れ、hloc 本体は依存を解決させずに入れる
  hloc_req="$(mktemp)"
  grep -viE '^[[:space:]]*lightglue' "$HLOC_DIR/requirements.txt" > "$hloc_req"
  "${PIP[@]}" install -r "$hloc_req" "${CONSTRAINT[@]}"
  rm -f "$hloc_req"
  "${PIP[@]}" install --no-deps -e "$HLOC_DIR"
fi

# ---- 6) 確認と重みの取得 ----
say "Checking the CV environment and downloading model weights"
cd "$SERVER_DIR"
"$PY" -m vptserver.cv.check_env

say "WSL2 CV setup finished."
