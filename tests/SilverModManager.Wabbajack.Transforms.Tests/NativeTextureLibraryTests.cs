using System.Runtime.InteropServices;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using SilverModManager.Wabbajack.Transforms;

namespace SilverModManager.Wabbajack.Transforms.Tests;

/// <summary>
/// Loads the committed libsmmtexenc.so straight through <see cref="NativeLibrary"/> and checks its export list and that
/// every export encodes. A stale binary after a change to extern/texenc fails here.
/// </summary>
public sealed unsafe class NativeTextureLibraryTests : IDisposable
{
    private static readonly string[] Exports =
    [
        "smm_texenc_init",
        "smm_texenc_encode_bc1",
        "smm_texenc_encode_bc3",
        "smm_texenc_encode_bc4",
        "smm_texenc_encode_bc5",
        "smm_texenc_encode_bc7",
    ];

    // The shim's own contract: the level a format takes is checked there, so the managed side cannot pass garbage through.
    private const int Bc7Levels = 7;
    private const int RgbcxLevels = 19;

    private static readonly string LibraryPath = Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64", "native", "libsmmtexenc.so");

    private readonly nint _library = NativeLibrary.Load(LibraryPath);

    public void Dispose() => NativeLibrary.Free(_library);

    private nint Export(string name) => NativeLibrary.GetExport(_library, name);

    [Fact]
    public void ExportsEverySymbolTheManagedSideBinds()
    {
        Assert.All(Exports, name => Assert.True(NativeLibrary.TryGetExport(_library, name, out var address) && address != 0, name));
    }

    [Fact]
    public void ExportsNothingBeyondTheShim()
    {
        // bc7e's own entry points and rgbcx's C++ symbols stay private to the library, so the list above is the whole surface.
        Assert.False(NativeLibrary.TryGetExport(_library, "bc7e_compress_blocks", out _));
        Assert.False(NativeLibrary.TryGetExport(_library, "bc7e_compress_block_init", out _));
    }

    private int Encode(string export, byte[] rgba, int width, int rows, byte[] dst, uint level)
    {
        fixed (byte* src = rgba)
        fixed (byte* d = dst)
            return ((delegate* unmanaged<byte*, uint, uint, byte*, uint, int>)Export(export))(src, (uint)width, (uint)rows, d, level);
    }

    private void Init() => ((delegate* unmanaged<void>)Export("smm_texenc_init"))();

    // Wide enough that the shim's block scratch (a fixed number of blocks) is crossed more than once per block row.
    private const int Width = 4 * 150;
    private const int Rows = 8;

    private static byte[] Texture()
    {
        var rgba = new byte[Width * Rows * 4];
        for (var y = 0; y < Rows; y++)
        for (var x = 0; x < Width; x++)
        {
            var i = (y * Width + x) * 4;
            rgba[i] = (byte)(x * 255 / (Width - 1));
            rgba[i + 1] = (byte)(255 - y * 30);
            rgba[i + 2] = (byte)(255 - x * 255 / (Width - 1));
            rgba[i + 3] = 255;
        }
        return rgba;
    }

    public static TheoryData<string, CompressionFormat, int, uint, int> Formats => new()
    {
        { "smm_texenc_encode_bc1", CompressionFormat.Bc1, 8, 10, 3 },
        { "smm_texenc_encode_bc3", CompressionFormat.Bc3, 16, 10, 3 },
        { "smm_texenc_encode_bc4", CompressionFormat.Bc4, 8, 0, 1 },
        { "smm_texenc_encode_bc5", CompressionFormat.Bc5, 16, 0, 2 },
        { "smm_texenc_encode_bc7", CompressionFormat.Bc7, 16, 0, 4 },
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void EncodesEveryBlockOfARowBandToTheRightSizeAndPlace(string export, CompressionFormat format, int blockBytes, uint level, int channels)
    {
        Init();
        var rgba = Texture();
        var dst = new byte[Width / 4 * (Rows / 4) * blockBytes];

        Assert.Equal(0, Encode(export, rgba, Width, Rows, dst, level));

        var decoded = new BcDecoder().DecodeRaw(dst, Width, Rows, format);
        for (var i = 0; i < Width * Rows; i++)
        {
            var expected = new[] { rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3] };
            var actual = new[] { decoded[i].r, decoded[i].g, decoded[i].b, decoded[i].a };
            for (var c = 0; c < channels; c++)
                Assert.InRange(Math.Abs(expected[c] - actual[c]), 0, 40);
        }
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void RejectsALevelOrShapeItCannotEncode(string export, CompressionFormat format, int blockBytes, uint level, int channels)
    {
        Init();
        _ = format; _ = channels;
        var rgba = Texture();
        var dst = new byte[Width / 4 * (Rows / 4) * blockBytes];

        Assert.NotEqual(0, Encode(export, rgba, Width - 2, Rows, dst, level));
        Assert.NotEqual(0, Encode(export, rgba, Width, Rows - 1, dst, level));
        if (export is "smm_texenc_encode_bc1" or "smm_texenc_encode_bc3")
            Assert.NotEqual(0, Encode(export, rgba, Width, Rows, dst, (uint)RgbcxLevels));
        if (export is "smm_texenc_encode_bc7")
            Assert.NotEqual(0, Encode(export, rgba, Width, Rows, dst, (uint)Bc7Levels));
    }
}
