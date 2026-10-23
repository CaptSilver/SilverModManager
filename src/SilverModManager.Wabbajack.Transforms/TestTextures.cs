using System.Runtime.InteropServices;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using BCnEncoder.Shared.ImageFiles;

namespace SilverModManager.Wabbajack.Transforms;

/// <summary>
/// Builds small DDS files for tests, so fixtures need no binary assets.
/// </summary>
internal static class TestTextures
{
    /// <summary>A square image with a red ramp across, a green ramp down and a constant blue.</summary>
    public static ColorRgba32[] Gradient(int size)
    {
        var pixels = new ColorRgba32[size * size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            pixels[y * size + x] = new ColorRgba32((byte)(x * 255 / (size - 1)), (byte)(y * 255 / (size - 1)), 128, 255);
        return pixels;
    }

    private static BcEncoder Encoder(CompressionFormat format) => new()
    {
        OutputOptions =
        {
            Format = format,
            FileFormat = OutputFileFormat.Dds,
            GenerateMipMaps = true,
            Quality = CompressionQuality.Fast,
        },
    };

    /// <summary>Writes a <paramref name="size"/> square BC1 DDS of <see cref="Gradient"/> with a full mip chain.</summary>
    public static void WriteBc1(Stream output, int size = 64)
        => Encoder(CompressionFormat.Bc1).EncodeToDds(MemoryMarshal.AsBytes(Gradient(size).AsSpan()), size, size, PixelFormat.Rgba32).Write(output);

    /// <summary>Writes a 64x64 BC1 cube map whose six faces all hold <see cref="Gradient"/>.</summary>
    public static void WriteBc1CubeMap(Stream output)
    {
        var face = MemoryMarshal.AsBytes(Gradient(64).AsSpan());
        Encoder(CompressionFormat.Bc1)
            .EncodeCubeMapToDds(face, face, face, face, face, face, 64, 64, PixelFormat.Rgba32)
            .Write(output);
    }

    /// <summary>
    /// Writes a 4x4 DDS whose DX10 header claims <paramref name="format"/>, for tests of unsupported sources.
    /// BC7 is encoded first because it is written with a DX10 header; the legacy BC1 header has no format field to patch.
    /// </summary>
    public static void WriteDds(Stream output, DxgiFormat format)
    {
        var pixels = MemoryMarshal.AsBytes(Gradient(4).AsSpan());
        var file = Encoder(CompressionFormat.Bc7).EncodeToDds(pixels, 4, 4, PixelFormat.Rgba32);
        file.dx10Header.dxgiFormat = (BCnEncoder.Shared.ImageFiles.DxgiFormat)(uint)format;
        file.Write(output);
    }
}
