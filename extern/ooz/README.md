# ooz (vendored)

Kraken decompressor from https://github.com/powzix/ooz, GPL-3.0-or-later,
Copyright (C) 2016 Powzix. Upstream has not moved since 2019.

Vendored rather than submoduled because we have to modify it — see below.
A submodule would pin someone else's tree, so carrying these patches would
mean maintaining a fork of a dead repo or a build-time patch step that can fail.

## Local modifications

**`compat.h`** — ooz is MSVC-flavored and will not compile under g++ without
shims for `_BitScanReverse`, `_BitScanForward`, `__forceinline`, and several
MSVC typedefs. GCC's `x86intrin.h` supplies `_rotl`.

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

`scripts/build-ooz.sh` compiles `kraken_lib.cpp`, `bitknit.cpp` and `lzna.cpp` in a pinned
container image and writes `src/NexusMods.Games.FromSoft/runtimes/linux-x64/native/libooz.so`.
`libooz.so` is committed; rebuild it only when these sources change. The script's two checks
(highest glibc symbol version, no libstdc++ dependency) must pass before the result is committed.

The C# side calls `ooz_decompress_seekchunked` through `Formats/Native/LibOoz.cs`. The other two
entry points (`ooz_kraken_decompress`, and `ooz_debug_decompress`, which prints to stderr) are not used.
