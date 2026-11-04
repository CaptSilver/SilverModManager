#!/usr/bin/env bash
# Builds libsmmtexenc.so (BC1/BC3/BC4/BC5/BC7 encoders, from extern/texenc) in a pinned container image and writes it
# to src/SilverModManager.Wabbajack.Transforms/runtimes/linux-x64/native/libsmmtexenc.so.
#
# Same reasoning and image as build-ooz.sh: a library linked against a newer glibc than the AppImage's own
# libcoreclr.so fails to load on the machines the AppImage targets, so it is built on an old distro release, pinned
# by digest. When a gate below fails, step IMAGE back to an older release and record its digest here. Never loosen a gate.
#
# bc7e.ispc is compiled by Intel's ISPC, a release tarball pinned by version and sha256. It is downloaded on the
# host, checked, and only its bin/ispc is unpacked and run (inside the image, which has no ISPC). Set ISPC_TARBALL to
# a copy you already have; it is checked against the same sha256.
#
# Gates, each of which fails the script:
#   1. the highest GLIBC_ symbol version libsmmtexenc.so binds, which includes everything it takes from libm, is no
#      newer than the one libcoreclr.so binds, read from the restored runtime pack (override the pack directory with
#      RUNTIME_NATIVE_DIR);
#   2. libsmmtexenc.so does not depend on libstdc++ (it is linked -static-libstdc++);
#   3. the library exports exactly the smm_texenc_* functions, so bc7e's and rgbcx's own symbols stay private.
# The script also prints the GLIBC_ versions bound by libm on their own, because ISPC's math calls are what would
# pull a new libm in.
set -euo pipefail

IMAGE="docker.io/library/ubuntu:20.04@sha256:c664f8f86ed5a386b0a340d981b8f81714e21a8b9c73f658c4bea56aa179d54a"
ISPC_VERSION="1.28.2"
ISPC_URL="https://github.com/ispc/ispc/releases/download/v${ISPC_VERSION}/ispc-v${ISPC_VERSION}-linux.tar.gz"
ISPC_SHA256="32e611de1252cf1e09a6a13327f5746b8477f99e15ffa4cbd1b422386776688c"
ISPC_TARGETS="sse2-i32x4,avx2-i32x8,avx512skx-x16"
EXPECTED_EXPORTS="smm_texenc_encode_bc1 smm_texenc_encode_bc3 smm_texenc_encode_bc4 smm_texenc_encode_bc5 smm_texenc_encode_bc7 smm_texenc_init"

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out_dir="$root/src/SilverModManager.Wabbajack.Transforms/runtimes/linux-x64/native"
out="$out_dir/libsmmtexenc.so"

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
mkdir "$work/ispc" "$work/out"

tarball="${ISPC_TARBALL:-$work/ispc.tar.gz}"
[[ -f "$tarball" ]] || curl -fsSL -o "$tarball" "$ISPC_URL"
actual_sha="$(sha256sum "$tarball" | cut -d' ' -f1)"
[[ "$actual_sha" == "$ISPC_SHA256" ]] || { echo "ISPC tarball sha256 is $actual_sha, expected $ISPC_SHA256" >&2; exit 1; }
tar -xzf "$tarball" -C "$work/ispc" --strip-components=2 "ispc-v${ISPC_VERSION}-linux/bin/ispc"

podman run --rm --security-opt label=disable -v "$root/extern/texenc":/src:ro -v "$work/ispc":/ispc:ro -v "$work/out":/out "$IMAGE" bash -euc '
    apt-get update -qq >/dev/null
    apt-get install -y -qq g++ >/dev/null
    cd /out
    # --pic: ISPC objects linked into a .so need it. SSE2 is the lowest target, so every x86-64 CPU has a kernel to
    # dispatch to; the dispatcher object picks AVX2 or AVX-512 at run time.
    /ispc/ispc --pic --arch=x86-64 --target='"$ISPC_TARGETS"' -O2 --opt=disable-assertions --wno-perf \
        -o /out/bc7e.o -h /out/bc7e_ispc.h /src/bc7e.ispc
    g++ -O2 -fPIC -shared -w -fvisibility=hidden -static-libstdc++ -static-libgcc -Wl,-z,noexecstack \
        -Wl,--version-script=/src/texenc.map -I/out -I/src \
        -o /out/libsmmtexenc.so /src/texenc.cpp /src/rgbcx.cpp /out/bc7e*.o
    g++ --version | head -1 > /out/gxx-version
    # Which undefined symbols come from libm, with the version each is bound at.
    nm -D --defined-only /lib/x86_64-linux-gnu/libm.so.6 | awk "{print \$3}" | sed "s/@.*//" | sort -u > /out/libm-names
    objdump -T /out/libsmmtexenc.so | awk "/\*UND\*/ {print \$NF, \$(NF-1)}" | tr -d "()" | sort -u > /out/undefined
'

built="$work/out/libsmmtexenc.so"
baseline="$(max_glibc "$RUNTIME_NATIVE_DIR/libcoreclr.so")"
actual="$(max_glibc "$built")"
libm_bound="$(awk 'NR==FNR { m[$1]; next } ($1 in m) { print $2 }' "$work/out/libm-names" "$work/out/undefined" | sort -uV | tr '\n' ' ')"
exports="$(nm -D --defined-only "$built" | awk '{print $3}' | sort | tr '\n' ' ')"
echo "g++:      $(cat "$work/out/gxx-version")"
echo "ispc:     $ISPC_VERSION ($ISPC_SHA256) targets $ISPC_TARGETS"
echo "image:    $IMAGE"
echo "sha256:   $(sha256sum "$built" | cut -d' ' -f1)"
echo "size:     $(stat -c %s "$built") bytes"
echo "glibc:    libsmmtexenc.so binds up to ${actual:-none}; libcoreclr.so binds up to $baseline"
echo "all:      $(objdump -T "$built" | grep -o 'GLIBC_[0-9.]*' | sort -uV | tr '\n' ' ')"
echo "libm:     ${libm_bound:-none}"
echo "needed:   $(objdump -p "$built" | awk '/NEEDED/ {print $2}' | tr '\n' ' ')"
echo "exports:  $exports"

fail=0
if [[ -n "$actual" && "$(printf '%s\n%s\n' "$actual" "$baseline" | sort -V | tail -1)" != "$baseline" ]]; then
    echo "FAIL gate 1: $actual is newer than $baseline; pin an older image" >&2
    fail=1
fi
if ldd "$built" | grep -q libstdc++; then
    echo "FAIL gate 2: libsmmtexenc.so depends on libstdc++" >&2
    fail=1
fi
if [[ "$exports" != "$EXPECTED_EXPORTS " ]]; then
    echo "FAIL gate 3: exports are '$exports', expected '$EXPECTED_EXPORTS '" >&2
    fail=1
fi
if [[ $fail -ne 0 ]]; then
    echo "libsmmtexenc.so was not replaced" >&2
    exit 1
fi
cp "$built" "$out"
echo "gates:    ok"
