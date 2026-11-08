#!/usr/bin/env bash
# Builds libsmmtexenc.so (BC1/BC3/BC4/BC5/BC7 encoders, from extern/texenc) in a pinned container image and writes it
# to src/SilverModManager.Wabbajack.Transforms/runtimes/<rid>/native/libsmmtexenc.so.
#
# Usage: scripts/build-texenc.sh [x86_64|aarch64]     (default x86_64)
#   x86_64   linux-x64, in the amd64 image; ISPC runs inside the image.
#   aarch64  linux-arm64, linked in the arm64 image under the host's qemu binfmt (podman run --platform linux/arm64).
#            ISPC is an x86-64 program, so it runs on the host (--arch=aarch64 --target=neon-i32x4) and only its
#            object and header go into the image. Gate 0: the pinned ISPC must list aarch64 with neon-i32x4 in
#            --support-matrix, otherwise the script stops and the arm64 app uses the managed encoder.
#            The gate baseline is the arm64 runtime pack (see build-ooz.sh), or any directory holding the pack's
#            libcoreclr.so, passed as RUNTIME_NATIVE_DIR.
#
# Same reasoning and image as build-ooz.sh: a library linked against a newer glibc than the AppImage's own
# libcoreclr.so fails to load on the machines the AppImage targets, so it is built on an old distro release, pinned
# by digest. When a gate below fails, step IMAGE back to an older release and record its digest here. Never loosen a gate.
#
# bc7e.ispc is compiled by Intel's ISPC, a release tarball pinned by version and sha256. It is downloaded on the
# host, checked, and only its bin/ispc is unpacked; x86_64 runs it inside the image (which has no ISPC), aarch64 on the host. Set ISPC_TARBALL to
# a copy you already have; it is checked against the same sha256.
#
# Gates, each of which fails the script:
#   1. the highest GLIBC_ symbol version libsmmtexenc.so binds, which includes everything it takes from libm, is no
#      newer than the one libcoreclr.so binds, read from the restored runtime pack (override the pack directory with
#      RUNTIME_NATIVE_DIR);
#   2. libsmmtexenc.so does not list libstdc++ among its NEEDED entries (it is linked -static-libstdc++);
#   3. the library exports exactly the smm_texenc_* functions, so bc7e's and rgbcx's own symbols stay private.
# The script also prints the GLIBC_ versions bound by libm on their own, because ISPC's math calls are what would
# pull a new libm in.
set -euo pipefail

IMAGE_X86_64="docker.io/library/ubuntu:20.04@sha256:c664f8f86ed5a386b0a340d981b8f81714e21a8b9c73f658c4bea56aa179d54a"
IMAGE_ARM64="docker.io/library/ubuntu:20.04@sha256:722ea796ac2d57eeb3627c58a582fc1acc58be51faf815e1bce1682ae5c092f7"
ISPC_VERSION="1.28.2"
ISPC_URL="https://github.com/ispc/ispc/releases/download/v${ISPC_VERSION}/ispc-v${ISPC_VERSION}-linux.tar.gz"
ISPC_SHA256="32e611de1252cf1e09a6a13327f5746b8477f99e15ffa4cbd1b422386776688c"
ISPC_TARGETS_X86_64="sse2-i32x4,avx2-i32x8,avx512skx-x16"
ISPC_TARGETS_AARCH64="neon-i32x4"
EXPECTED_EXPORTS="smm_texenc_encode_bc1 smm_texenc_encode_bc3 smm_texenc_encode_bc4 smm_texenc_encode_bc5 smm_texenc_encode_bc7 smm_texenc_init"

arch="${1:-x86_64}"
case "$arch" in
    x86_64)  IMAGE="$IMAGE_X86_64"; platform=linux/amd64; rid=linux-x64;   libdir=x86_64-linux-gnu;  ISPC_ARCH=x86-64;  ISPC_TARGETS="$ISPC_TARGETS_X86_64" ;;
    aarch64) IMAGE="$IMAGE_ARM64";  platform=linux/arm64; rid=linux-arm64; libdir=aarch64-linux-gnu; ISPC_ARCH=aarch64; ISPC_TARGETS="$ISPC_TARGETS_AARCH64" ;;
    *) echo "usage: $0 [x86_64|aarch64]" >&2; exit 2 ;;
esac

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out_dir="$root/src/SilverModManager.Wabbajack.Transforms/runtimes/$rid/native"
out="$out_dir/libsmmtexenc.so"

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
mkdir "$work/ispc" "$work/out"

tarball="${ISPC_TARBALL:-$work/ispc.tar.gz}"
[[ -f "$tarball" ]] || curl -fsSL -o "$tarball" "$ISPC_URL"
actual_sha="$(sha256sum "$tarball" | cut -d' ' -f1)"
[[ "$actual_sha" == "$ISPC_SHA256" ]] || { echo "ISPC tarball sha256 is $actual_sha, expected $ISPC_SHA256" >&2; exit 1; }
tar -xzf "$tarball" -C "$work/ispc" --strip-components=2 "ispc-v${ISPC_VERSION}-linux/bin/ispc"

ispc_flags=(--pic --arch="$ISPC_ARCH" --target="$ISPC_TARGETS" -O2 --opt=disable-assertions --wno-perf)
if [[ "$arch" == aarch64 ]]; then
    # Gate 0: without a NEON target in this ISPC release there is nothing to build, and the arm64 app takes the
    # managed encoder (BCnEncoder.Net), logging its usual "was not found" line once.
    matrix="$("$work/ispc/ispc" --support-matrix)"
    if ! grep -E '^neon-i32x4 ' <<<"$matrix" | grep -q aarch64; then
        echo "FAIL gate 0: ISPC $ISPC_VERSION's Linux binary has no aarch64 neon-i32x4 target; the arm64 build takes the managed encoder" >&2
        exit 1
    fi
    "$work/ispc/ispc" "${ispc_flags[@]}" -o "$work/out/bc7e.o" -h "$work/out/bc7e_ispc.h" "$root/extern/texenc/bc7e.ispc"
fi

podman run --rm --platform "$platform" --security-opt label=disable -e ARCH="$arch" -e ISPC_FLAGS="${ispc_flags[*]}" \
    -e LIBDIR="$libdir" -v "$root/extern/texenc":/src:ro -v "$work/ispc":/ispc:ro \
    -v "$RUNTIME_NATIVE_DIR":/pack:ro -v "$work/out":/out "$IMAGE" bash -euc '
    apt-get update -qq >/dev/null
    apt-get install -y -qq g++ >/dev/null
    cd /out
    # --pic: ISPC objects linked into a .so need it. On x86-64 SSE2 is the lowest target, so every CPU has a kernel to
    # dispatch to; the dispatcher object picks AVX2 or AVX-512 at run time. aarch64 is compiled on the host.
    if [[ $ARCH == x86_64 ]]; then
        /ispc/ispc $ISPC_FLAGS -o /out/bc7e.o -h /out/bc7e_ispc.h /src/bc7e.ispc
    fi
    g++ -O2 -fPIC -shared -w -fvisibility=hidden -static-libstdc++ -static-libgcc -Wl,-z,noexecstack \
        -Wl,--version-script=/src/texenc.map -I/out -I/src \
        -o /out/libsmmtexenc.so /src/texenc.cpp /src/rgbcx.cpp /out/bc7e*.o
    g++ --version | head -1 > /out/gxx-version
    # Which undefined symbols come from libm, with the version each is bound at.
    nm -D --defined-only /lib/$LIBDIR/libm.so.6 | awk "{print \$3}" | sed "s/@.*//" | sort -u > /out/libm-names
    objdump -T /out/libsmmtexenc.so | awk "/\*UND\*/ {print \$NF, \$(NF-1)}" | tr -d "()" | sort -u > /out/undefined
    # The ELF checks run here because the host binutils read x86-64 files only.
    objdump -T /out/libsmmtexenc.so > /out/symbols
    objdump -p /out/libsmmtexenc.so | awk "/NEEDED/ {print \$2}" > /out/needed
    nm -D --defined-only /out/libsmmtexenc.so | awk "{print \$3}" | sort > /out/exports
    objdump -T /pack/libcoreclr.so > /out/coreclr-symbols
'

built="$work/out/libsmmtexenc.so"
baseline="$(max_glibc "$work/out/coreclr-symbols")"
actual="$(max_glibc "$work/out/symbols")"
libm_bound="$(awk 'NR==FNR { m[$1]; next } ($1 in m) { print $2 }' "$work/out/libm-names" "$work/out/undefined" | sort -uV | tr '\n' ' ')"
exports="$(tr '\n' ' ' < "$work/out/exports")"
echo "g++:      $(cat "$work/out/gxx-version")"
echo "ispc:     $ISPC_VERSION ($ISPC_SHA256) targets $ISPC_TARGETS"
echo "image:    $IMAGE ($platform)"
echo "sha256:   $(sha256sum "$built" | cut -d' ' -f1)"
echo "size:     $(stat -c %s "$built") bytes"
echo "glibc:    libsmmtexenc.so binds up to ${actual:-none}; libcoreclr.so binds up to $baseline"
echo "all:      $(grep -o 'GLIBC_[0-9.]*' "$work/out/symbols" | sort -uV | tr '\n' ' ')"
echo "libm:     ${libm_bound:-none}"
echo "needed:   $(tr '\n' ' ' < "$work/out/needed")"
echo "exports:  $exports"

fail=0
if [[ -n "$actual" && "$(printf '%s\n%s\n' "$actual" "$baseline" | sort -V | tail -1)" != "$baseline" ]]; then
    echo "FAIL gate 1: $actual is newer than $baseline; pin an older image" >&2
    fail=1
fi
if grep -q libstdc++ "$work/out/needed"; then
    echo "FAIL gate 2: libsmmtexenc.so lists libstdc++ as NEEDED" >&2
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
