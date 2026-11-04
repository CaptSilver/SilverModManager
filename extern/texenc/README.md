# texenc (vendored)

Texture block encoders for `libsmmtexenc.so`, from https://github.com/richgel999/bc7enc_rdo
at commit `b9438627eef73a1157e84201b6fa6eb2ffd6d9f0`. Both upstream pieces are archived or
quiet, so the sources live here instead of in a submodule; nothing below is modified.

## What is here

| File | Upstream | Licence | Built? |
|---|---|---|---|
| `bc7e.ispc` | Binomial LLC | Apache-2.0 (file header, repo `LICENSE`) | yes, by ISPC, BC7 |
| `rgbcx.h`, `rgbcx.cpp`, `rgbcx_table4.h` | Richard Geldreich | MIT or Unlicense, we take MIT | yes, BC1/BC3/BC4/BC5 |
| `bc7decomp.cpp`, `bc7decomp.h` | Richard Geldreich | MIT or Unlicense, we take MIT | no |
| `LICENSE` | upstream repo licence file, verbatim | | |
| `texenc.cpp`, `texenc.map` | ours | GPL-3.0-only | yes |

`rgbcx.h` is not a single-header library at this commit: `RGBCX_IMPLEMENTATION` is an empty block and the code is
in `rgbcx.cpp`, which includes `rgbcx_table4.h`. `rgbcx_table4_small.h` (the `RGBCX_USE_SMALLER_TABLES` variant) is
not vendored.

`bc7decomp` is vendored for a later decode path and deliberately left out of the build, like `kraken.cpp` in
`extern/ooz`. It includes `<immintrin.h>`, so it also needs a decision about the SSE2 floor before it is linked.

## The shim

`texenc.cpp` is C++ because rgbcx is. It exports `smm_texenc_init` and one `smm_texenc_encode_<fmt>` per format
(`bc1`, `bc3`, `bc4`, `bc5`, `bc7`), all with the shape documented at the top of the file. `texenc.map` is the
linker version script that keeps everything else private, so bc7e's own entry points and rgbcx's C++ symbols are not
exported. The quality level is an argument of each encode call, not a constant in the library: changing it is a
managed-side edit, not a rebuild.

## Build

`scripts/build-texenc.sh` compiles `bc7e.ispc` with ISPC 1.28.2 (release tarball
`https://github.com/ispc/ispc/releases/download/v1.28.2/ispc-v1.28.2-linux.tar.gz`, sha256
`32e611de1252cf1e09a6a13327f5746b8477f99e15ffa4cbd1b422386776688c`, also pinned in the script) for
`sse2-i32x4`, `avx2-i32x8` and `avx512skx-x16`, then links the objects with `texenc.cpp` and `rgbcx.cpp` in the same
digest-pinned `ubuntu:20.04` image `build-ooz.sh` uses, and writes
`src/SilverModManager.Wabbajack.Transforms/runtimes/linux-x64/native/libsmmtexenc.so`.
`libsmmtexenc.so` is committed; rebuild it only when these sources, the ISPC pin or the flags change.

The script fails unless the highest glibc symbol version (libm's included) is no newer than `libcoreclr.so`'s, the
library does not depend on libstdc++, and it exports exactly the `smm_texenc_*` functions (every symbol type counts, weak ones included). The committed library is only replaced when all three gates pass. It prints the glibc
versions bound, the ones bound from libm on their own, the exports and the sha256 of the result. Two runs give the
same sha256.

The SSE2 target is the floor, so every x86-64 CPU has a kernel to dispatch to; no CPU check is needed before loading.
`ISPC_TARBALL=<path>` points the script at a copy of the tarball you already have; it is verified against the same sha256.

`tests/SilverModManager.Wabbajack.Transforms.Tests/NativeTextureLibraryTests.cs` loads the committed binary, compares the library's whole dynamic symbol table with the export list and encodes every format, alpha included.
It does not tie the binary to the sources, so rebuild after any change here.
