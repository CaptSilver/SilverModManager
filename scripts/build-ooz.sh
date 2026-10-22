#!/usr/bin/env bash
# Builds libooz.so (Kraken decompression, from extern/ooz) in a pinned container image and writes it to
# src/NexusMods.Games.FromSoft/runtimes/linux-x64/native/libooz.so.
#
# It is not built in the fedora distrobox: the AppImage is built on ubuntu (build-linux-pupnet.yaml), and a
# library linked against a newer glibc than the AppImage's own native libraries fails to load on the
# machines the AppImage targets. The image is pinned by digest so the toolchain, and so the output, repeats.
#
# Two gates decide whether the result may be committed, and the script fails when either does not hold:
#   1. the highest GLIBC_ symbol version libooz.so binds is no newer than the one libcoreclr.so binds,
#      read from the restored runtime pack (override the pack directory with RUNTIME_NATIVE_DIR);
#   2. libooz.so does not depend on libstdc++ (it is linked -static-libstdc++).
# When gate 1 fails, step IMAGE back to an older distro release and record its digest here. Never loosen the gate.
set -euo pipefail

IMAGE="docker.io/library/ubuntu:20.04@sha256:c664f8f86ed5a386b0a340d981b8f81714e21a8b9c73f658c4bea56aa179d54a"
SOURCES=(kraken_lib.cpp bitknit.cpp lzna.cpp)

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out_dir="$root/src/NexusMods.Games.FromSoft/runtimes/linux-x64/native"
out="$out_dir/libooz.so"

max_glibc() { objdump -T "$1" | grep -o 'GLIBC_[0-9.]*' | sort -uV | tail -1; }

if [[ -z "${RUNTIME_NATIVE_DIR:-}" ]]; then
    pack_root="$HOME/.nuget/packages/microsoft.netcore.app.runtime.linux-x64"
    version="$(ls "$pack_root" 2>/dev/null | sort -V | tail -1)"
    [[ -n "$version" ]] || { echo "no restored microsoft.netcore.app.runtime.linux-x64 under $pack_root; restore the solution first" >&2; exit 1; }
    RUNTIME_NATIVE_DIR="$pack_root/$version/runtimes/linux-x64/native"
fi
[[ -f "$RUNTIME_NATIVE_DIR/libcoreclr.so" ]] || { echo "libcoreclr.so not found in $RUNTIME_NATIVE_DIR" >&2; exit 1; }

mkdir -p "$out_dir"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

podman run --rm --security-opt label=disable -v "$root/extern/ooz":/src:ro -v "$work":/out "$IMAGE" bash -euc '
    apt-get update -qq >/dev/null
    apt-get install -y -qq g++ >/dev/null
    cd /src
    g++ -O2 -fPIC -shared -w -static-libstdc++ -static-libgcc -o /out/libooz.so '"${SOURCES[*]}"'
    g++ --version | head -1 > /out/gxx-version
'
cp "$work/libooz.so" "$out"

baseline="$(max_glibc "$RUNTIME_NATIVE_DIR/libcoreclr.so")"
actual="$(max_glibc "$out")"
echo "g++:      $(cat "$work/gxx-version")"
echo "image:    $IMAGE"
echo "sha256:   $(sha256sum "$out" | cut -d' ' -f1)"
echo "glibc:    libooz.so binds up to ${actual:-none}; libcoreclr.so binds up to $baseline"

fail=0
if [[ -n "$actual" && "$(printf '%s\n%s\n' "$actual" "$baseline" | sort -V | tail -1)" != "$baseline" ]]; then
    echo "FAIL gate 1: $actual is newer than $baseline; pin an older image" >&2
    fail=1
fi
if ldd "$out" | grep -q libstdc++; then
    echo "FAIL gate 2: libooz.so depends on libstdc++" >&2
    fail=1
fi
[[ $fail -eq 0 ]] && echo "gates:    ok"
exit $fail
