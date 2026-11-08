#!/usr/bin/env bash
# Builds libooz.so (Kraken decompression, from extern/ooz) in a pinned container image and writes it to
# src/SilverModManager.Games.FromSoft/runtimes/<rid>/native/libooz.so.
#
# Usage: scripts/build-ooz.sh [x86_64|aarch64]     (default x86_64)
#   x86_64   linux-x64, in the amd64 image.
#   aarch64  linux-arm64, in the arm64 image under the host's qemu binfmt (podman run --platform linux/arm64).
#            extern/ooz/compat.h maps the SSE2 intrinsics onto NEON through the vendored sse2neon.h there.
#            The gate baseline is the arm64 runtime pack, restored in the fedora box with
#              dotnet restore src/SilverModManager.App -r linux-arm64 -p:SelfContained=true
#            (or scripts/dev-package.sh --arch aarch64 once that exists)
#            or any directory holding the pack's libcoreclr.so, passed as RUNTIME_NATIVE_DIR.
#
# It is not built in the fedora distrobox: the AppImage is built on ubuntu (build-linux-pupnet.yaml), and a
# library linked against a newer glibc than the AppImage's own native libraries fails to load on the
# machines the AppImage targets. The images are pinned by digest so the toolchain, and so the output, repeats.
# The ELF checks run inside the image because the host's objdump reads x86-64 files only.
#
# Two gates decide whether the result may be committed, and the script fails when either does not hold:
#   1. the highest GLIBC_ symbol version libooz.so binds is no newer than the one libcoreclr.so binds,
#      read from the restored runtime pack (override the pack directory with RUNTIME_NATIVE_DIR);
#   2. libooz.so does not list libstdc++ among its NEEDED entries (it is linked -static-libstdc++).
# When gate 1 fails, step the image back to an older distro release and record its digest here. Never loosen the gate.
set -euo pipefail

IMAGE_X86_64="docker.io/library/ubuntu:20.04@sha256:c664f8f86ed5a386b0a340d981b8f81714e21a8b9c73f658c4bea56aa179d54a"
IMAGE_ARM64="docker.io/library/ubuntu:20.04@sha256:722ea796ac2d57eeb3627c58a582fc1acc58be51faf815e1bce1682ae5c092f7"
SOURCES=(kraken_lib.cpp bitknit.cpp lzna.cpp)

arch="${1:-x86_64}"
case "$arch" in
    x86_64)  IMAGE="$IMAGE_X86_64"; platform=linux/amd64; rid=linux-x64 ;;
    aarch64) IMAGE="$IMAGE_ARM64";  platform=linux/arm64; rid=linux-arm64 ;;
    *) echo "usage: $0 [x86_64|aarch64]" >&2; exit 2 ;;
esac

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out_dir="$root/src/SilverModManager.Games.FromSoft/runtimes/$rid/native"
out="$out_dir/libooz.so"

max_glibc() { grep -o 'GLIBC_[0-9.]*' "$1" | sort -uV | tail -1; }

if [[ -z "${RUNTIME_NATIVE_DIR:-}" ]]; then
    pack_root="$HOME/.nuget/packages/microsoft.netcore.app.runtime.$rid"
    version="$(ls "$pack_root" 2>/dev/null | sort -V | tail -1)"
    [[ -n "$version" ]] || { echo "no restored microsoft.netcore.app.runtime.$rid under $pack_root; restore it in the fedora box: dotnet restore src/SilverModManager.App -r $rid -p:SelfContained=true" >&2; exit 1; }
    RUNTIME_NATIVE_DIR="$pack_root/$version/runtimes/$rid/native"
fi
[[ -f "$RUNTIME_NATIVE_DIR/libcoreclr.so" ]] || { echo "libcoreclr.so not found in $RUNTIME_NATIVE_DIR" >&2; exit 1; }

mkdir -p "$out_dir"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

podman run --rm --platform "$platform" --security-opt label=disable -v "$root/extern/ooz":/src:ro \
    -v "$RUNTIME_NATIVE_DIR":/pack:ro -v "$work":/out "$IMAGE" bash -euc '
    apt-get update -qq >/dev/null
    apt-get install -y -qq g++ >/dev/null
    cd /src
    g++ -O2 -fPIC -shared -w -static-libstdc++ -static-libgcc -o /out/libooz.so '"${SOURCES[*]}"'
    g++ --version | head -1 > /out/gxx-version
    objdump -T /out/libooz.so > /out/ooz-symbols
    objdump -p /out/libooz.so | awk "/NEEDED/ {print \$2}" > /out/ooz-needed
    objdump -T /pack/libcoreclr.so > /out/coreclr-symbols
'
cp "$work/libooz.so" "$out"

baseline="$(max_glibc "$work/coreclr-symbols")"
actual="$(max_glibc "$work/ooz-symbols")"
echo "g++:      $(cat "$work/gxx-version")"
echo "image:    $IMAGE ($platform)"
echo "needed:   $(tr '\n' ' ' < "$work/ooz-needed")"
echo "sha256:   $(sha256sum "$out" | cut -d' ' -f1)"
echo "glibc:    libooz.so binds up to ${actual:-none}; libcoreclr.so binds up to $baseline"

fail=0
if [[ -n "$actual" && "$(printf '%s\n%s\n' "$actual" "$baseline" | sort -V | tail -1)" != "$baseline" ]]; then
    echo "FAIL gate 1: $actual is newer than $baseline; pin an older image" >&2
    fail=1
fi
if grep -q libstdc++ "$work/ooz-needed"; then
    echo "FAIL gate 2: libooz.so lists libstdc++ as NEEDED" >&2
    fail=1
fi
[[ $fail -eq 0 ]] && echo "gates:    ok"
exit $fail
