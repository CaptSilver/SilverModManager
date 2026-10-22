#!/usr/bin/env bash
# Builds libloot's Python binding from source, for when the interpreter LOOT_ORACLE_PYTHON points
# at (see README.md) is gone. Nothing is installed on the host: the build runs in the fedora
# distrobox and everything it produces lands in the directory given as the argument.
#
#   tools/loot-oracle/build-oracle.sh <scratch dir>
#
# It prints the export line for LOOT_ORACLE_PYTHON when it is done. The binary is never committed.
set -euo pipefail

LIBLOOT_TAG="0.29.6"
# The expected libloot_revision for that tag; generate.py records whatever the module reports.
EXPECTED_REVISION="136f398"

if [[ $# -ne 1 ]]; then
    echo "usage: $0 <scratch dir>" >&2
    exit 2
fi
mkdir -p "$1"
out="$(cd "$1" && pwd)"
box="${SILVER_BOX:-fedora}"

build_script='
set -euo pipefail
out="$1"; tag="$2"; revision="$3"

command -v git >/dev/null || sudo dnf install -y git
command -v gcc >/dev/null || sudo dnf install -y gcc
command -v python3.12 >/dev/null || sudo dnf install -y python3.12
if ! command -v cargo >/dev/null && [[ ! -x "$HOME/.cargo/bin/cargo" ]]; then
    curl --proto "=https" --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y --profile minimal
fi
export PATH="$HOME/.cargo/bin:$PATH"

# The binding is the importable module "loot", built by maturin against the interpreter it will
# run under, so the interpreter is a venv holding both maturin and the resulting wheel.
python3.12 -m venv "$out/venv"
"$out/venv/bin/pip" install --quiet "maturin>=1.8,<2.0"

rm -rf "$out/libloot" "$out/wheels"
git clone --depth 1 --branch "$tag" https://github.com/loot/libloot "$out/libloot"
"$out/venv/bin/maturin" build --release \
    --manifest-path "$out/libloot/python/Cargo.toml" \
    --interpreter "$out/venv/bin/python3" \
    --out "$out/wheels"
"$out/venv/bin/pip" install --quiet --force-reinstall "$out"/wheels/*.whl

"$out/venv/bin/python3" -I -c "import loot; print(loot.libloot_version(), loot.libloot_revision())"
echo "expected revision $revision"
'

if [[ -z "${CONTAINER_ID:-}" ]] && command -v distrobox >/dev/null; then
    distrobox enter "$box" -- bash -c "$build_script" _ "$out" "$LIBLOOT_TAG" "$EXPECTED_REVISION"
else
    bash -c "$build_script" _ "$out" "$LIBLOOT_TAG" "$EXPECTED_REVISION"
fi

echo
echo "export LOOT_ORACLE_PYTHON=$out/venv/bin/python3"
