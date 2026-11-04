// Shim over bc7e (ISPC) and rgbcx for libsmmtexenc.so. One calling shape per format:
//
//   int smm_texenc_encode_<fmt>(const uint8_t* rgba, uint32_t paddedWidth, uint32_t rows, uint8_t* dst, uint32_t level)
//
// rgba is paddedWidth x rows RGBA32 pixels, straight alpha, row-major; both dimensions are multiples of 4. dst takes
// one block per 4x4 pixels in the same row-major order. Returns 0, or non-zero when an argument is out of range
// (nothing is written then). The library starts no threads; the caller splits a level into bands of rows and may call
// from several threads at once once smm_texenc_init has returned.
//
// level: bc1 and bc3 take the rgbcx level (0..18); bc7 takes the bc7e preset (0 ultrafast, 1 veryfast, 2 fast,
// 3 basic, 4 slow, 5 veryslow, 6 slowest); bc4 and bc5 have a single quality and ignore it. The managed side owns
// which level is used, so changing it does not need a rebuild of this library.

#include <stdint.h>
#include <string.h>

#include "bc7e_ispc.h"
#include "rgbcx.h"

#define SMM_EXPORT extern "C" __attribute__((visibility("default")))

namespace
{
    constexpr uint32_t kBc7Levels = 7;
    constexpr uint32_t kBlockPixelBytes = 16 * 4;

    // bc7e wants blocks in order, so a band is repacked a few blocks at a time into scratch of this size on the
    // caller's stack, whatever the width of the level.
    constexpr uint32_t kBc7BlocksPerCall = 64;

    ispc::bc7e_compress_block_params g_bc7Params[kBc7Levels];

    typedef void (*Bc7ParamsInit)(ispc::bc7e_compress_block_params*, bool);

    bool validShape(uint32_t paddedWidth, uint32_t rows)
    {
        return paddedWidth >= 4 && rows >= 4 && paddedWidth % 4 == 0 && rows % 4 == 0;
    }

    // Copies the 4x4 pixel block whose top-left pixel is at (blockX * 4, blockRow * 4) into 64 contiguous bytes,
    // R first, row-major: the layout both rgbcx and bc7e take.
    inline void gatherBlock(const uint8_t* rgba, uint32_t paddedWidth, uint32_t blockX, uint32_t blockRow, uint8_t* out)
    {
        const size_t stride = (size_t)paddedWidth * 4;
        const uint8_t* top = rgba + (size_t)blockRow * 4 * stride + (size_t)blockX * 16;
        for (int y = 0; y < 4; y++)
            memcpy(out + y * 16, top + y * stride, 16);
    }

    // Walks every block of the band in row-major order and hands each gathered block to encode(block, dstBlock).
    template <typename Encode>
    int encodeBlocks(const uint8_t* rgba, uint32_t paddedWidth, uint32_t rows, uint8_t* dst, uint32_t blockBytes, Encode encode)
    {
        if (!validShape(paddedWidth, rows)) return 1;
        const uint32_t blocksAcross = paddedWidth / 4;
        const uint32_t blockRows = rows / 4;
        uint8_t block[kBlockPixelBytes];
        for (uint32_t by = 0; by < blockRows; by++)
            for (uint32_t bx = 0; bx < blocksAcross; bx++)
            {
                gatherBlock(rgba, paddedWidth, bx, by, block);
                encode(block, dst + ((size_t)by * blocksAcross + bx) * blockBytes);
            }
        return 0;
    }
}

// Not thread-safe: both codecs fill static tables with no lock. Call once, before any encode.
SMM_EXPORT void smm_texenc_init(void)
{
    ispc::bc7e_compress_block_init();
    rgbcx::init(rgbcx::bc1_approx_mode::cBC1Ideal);

    // Not perceptual: the quality rule compares by plain RMSE PSNR, so the preset is picked by the same measure.
    static const Bc7ParamsInit inits[kBc7Levels] = {
        ispc::bc7e_compress_block_params_init_ultrafast, ispc::bc7e_compress_block_params_init_veryfast,
        ispc::bc7e_compress_block_params_init_fast,      ispc::bc7e_compress_block_params_init_basic,
        ispc::bc7e_compress_block_params_init_slow,      ispc::bc7e_compress_block_params_init_veryslow,
        ispc::bc7e_compress_block_params_init_slowest,
    };
    for (uint32_t i = 0; i < kBc7Levels; i++)
        inits[i](&g_bc7Params[i], false);
}

SMM_EXPORT int smm_texenc_encode_bc1(const uint8_t* rgba, uint32_t paddedWidth, uint32_t rows, uint8_t* dst, uint32_t level)
{
    if (level > rgbcx::MAX_LEVEL) return 2;
    // 3-colour blocks allowed, but dark texels are not coded as transparent: the game's shaders do sample alpha, so
    // this stays opaque-only and cut-out BC1 goes to the managed encoder.
    return encodeBlocks(rgba, paddedWidth, rows, dst, 8, [level](const uint8_t* block, uint8_t* out) {
        rgbcx::encode_bc1(level, out, block, true, false);
    });
}

SMM_EXPORT int smm_texenc_encode_bc3(const uint8_t* rgba, uint32_t paddedWidth, uint32_t rows, uint8_t* dst, uint32_t level)
{
    if (level > rgbcx::MAX_LEVEL) return 2;
    return encodeBlocks(rgba, paddedWidth, rows, dst, 16, [level](const uint8_t* block, uint8_t* out) {
        rgbcx::encode_bc3(level, out, block);
    });
}

SMM_EXPORT int smm_texenc_encode_bc4(const uint8_t* rgba, uint32_t paddedWidth, uint32_t rows, uint8_t* dst, uint32_t)
{
    return encodeBlocks(rgba, paddedWidth, rows, dst, 8, [](const uint8_t* block, uint8_t* out) {
        rgbcx::encode_bc4(out, block, 4);
    });
}

SMM_EXPORT int smm_texenc_encode_bc5(const uint8_t* rgba, uint32_t paddedWidth, uint32_t rows, uint8_t* dst, uint32_t)
{
    return encodeBlocks(rgba, paddedWidth, rows, dst, 16, [](const uint8_t* block, uint8_t* out) {
        rgbcx::encode_bc5(out, block, 0, 1, 4);
    });
}

SMM_EXPORT int smm_texenc_encode_bc7(const uint8_t* rgba, uint32_t paddedWidth, uint32_t rows, uint8_t* dst, uint32_t level)
{
    if (level >= kBc7Levels) return 2;
    if (!validShape(paddedWidth, rows)) return 1;

    const uint32_t blocksAcross = paddedWidth / 4;
    const uint32_t blockRows = rows / 4;
    alignas(16) uint8_t scratch[kBc7BlocksPerCall * kBlockPixelBytes];

    for (uint32_t by = 0; by < blockRows; by++)
    {
        for (uint32_t bx = 0; bx < blocksAcross; bx += kBc7BlocksPerCall)
        {
            const uint32_t count = blocksAcross - bx < kBc7BlocksPerCall ? blocksAcross - bx : kBc7BlocksPerCall;
            for (uint32_t i = 0; i < count; i++)
                gatherBlock(rgba, paddedWidth, bx + i, by, scratch + i * kBlockPixelBytes);
            ispc::bc7e_compress_blocks(count, (uint64_t*)(dst + ((size_t)by * blocksAcross + bx) * 16),
                                       (const uint32_t*)scratch, &g_bc7Params[level]);
        }
    }
    return 0;
}
