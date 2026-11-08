# ooz (vendored)

Kraken decompressor from https://github.com/powzix/ooz, GPL-3.0-or-later,
Copyright (C) 2016 Powzix. Upstream has not moved since 2019.

Vendored rather than submoduled because we have to modify it — see below.
A submodule would pin someone else's tree, so carrying these patches would
mean maintaining a fork of a dead repo or a build-time patch step that can fail.

## Local modifications

**`compat.h`** — ooz is MSVC-flavored and will not compile under g++ without
shims for `_BitScanReverse`, `_BitScanForward`, `__forceinline`, and several
MSVC typedefs. On x86 GCC's `x86intrin.h` supplies `_rotl`. On any other CPU it includes
`sse2neon.h` instead and defines `_rotl` itself; that branch never reaches the x86 translation units.

**`sse2neon.h`** — vendored unmodified so the SSE2 code in `kraken_lib.cpp` and `lzna.cpp` builds
for aarch64. Source: https://github.com/DLTcollab/sse2neon, tag `v1.8.0`, MIT license
(Copyright (c) 2015-2024 SSE2NEON Contributors, notice at the top of the file).
sha256 `07723c9f9457dd4316f1fde3dd4eb6f31dd67d9955f6c21f4e609ac1698be48a`; re-derive with
`curl -sL https://raw.githubusercontent.com/DLTcollab/sse2neon/v1.8.0/sse2neon.h | sha256sum`.
v1.8.0 is the newest tag that compiles with the g++ 9.4 of the pinned ubuntu:20.04 image: v1.9.0 and
later stop at `#error "Unsupported compiler: __has_builtin not available"`. Move the tag only together
with the image.

**`kraken.cpp`** — upstream's original, unmodified and deliberately not built.
It exists so `diff kraken.cpp kraken_lib.cpp` shows exactly what we changed.
Adding it to `SOURCES` produces duplicate-symbol link errors.

**`kraken_lib.cpp`** — drops the `oo2core` DLL loader and `main`, and adds the `extern "C"`
entry points `ooz_kraken_decompress`, `ooz_debug_decompress` and
`ooz_decompress_seekchunked`, the one we actually call.

**`stdafx.h`** — every vendored `.cpp` still `#include`s this (an MSVC
precompiled-header convention). Upstream's version pulls in `<Windows.h>` and
`<tchar.h>`, so it's replaced with a two-line shim that just forwards to
`compat.h`.

## The seek-chunk gotcha

Stock `Kraken_Decompress` fails on **every** FromSoftware DCX file, vanilla
included, after exactly one 256 KB block. This is not a build error — a correct
build decodes the whole Silesia corpus byte-perfect, including a 51 MB file.

FromSoft sets `seekChunkReset=true`, so each 256 KB chunk is coded
independently and back-references must be chunk-relative. The fix is to loop
over `Kraken_DecodeStep` with a per-chunk destination base rather than calling
`Kraken_Decompress` once:

    if (!Kraken_DecodeStep(dec, dst + written, 0, dst_len, src, src_len)) return -1;

If you are debugging a decode that dies after 262144 bytes, this is why.

## Build

`scripts/build-ooz.sh [x86_64|aarch64]` compiles `kraken_lib.cpp`, `bitknit.cpp` and `lzna.cpp` in a pinned
container image and writes `src/SilverModManager.Games.FromSoft/runtimes/linux-x64/native/libooz.so`, or
`runtimes/linux-arm64/native/libooz.so` for `aarch64` (the arm64 image runs under the host's qemu binfmt).
Both `libooz.so` files are committed; rebuild one only when these sources change. The script's two checks
(highest glibc symbol version, no libstdc++ among the NEEDED entries) must pass before the result is committed.

The x86-64 library is not rebuilt when only the aarch64 side changes, because the build is not byte-reproducible
(the image's g++ is not pinned). `g++ -E -P` of the three sources in the x86-64 image, before and after a
`compat.h` edit, shows whether the x86 translation units changed.

The aarch64 library goes through sse2neon, so it is checked against the x86-64 one on real Kraken data: decode the
same Kraken DCX files with both and compare SHA-256 of the output. No Kraken container is committed.

The C# side calls `ooz_decompress_seekchunked` through `Formats/Native/LibOoz.cs`. The other two
entry points (`ooz_kraken_decompress`, and `ooz_debug_decompress`, which prints to stderr) are not used.
