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

    /// <summary><see cref="Gradient"/> as RGBA bytes.</summary>
    public static byte[] GradientRgba(int size) => MemoryMarshal.AsBytes(Gradient(size).AsSpan()).ToArray();

    /// <summary>Seeded full-amplitude noise in every channel, alpha included. No encoder reproduces it well, which is the point.</summary>
    public static byte[] NoiseRgba(int size, int seed)
    {
        var pixels = new byte[size * size * 4];
        new Random(seed).NextBytes(pixels);
        return pixels;
    }

    /// <summary>Black and white squares one pixel wide, opaque.</summary>
    public static byte[] CheckerboardRgba(int size)
    {
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var at = (y * size + x) * 4;
            var value = (byte)((x + y) % 2 == 0 ? 255 : 0);
            pixels[at] = pixels[at + 1] = pixels[at + 2] = value;
            pixels[at + 3] = 255;
        }
        return pixels;
    }

    /// <summary>A colour ramp whose alpha falls from opaque on the left to clear on the right, with colour kept under the clear part.</summary>
    public static byte[] PartialAlphaRgba(int size)
    {
        var pixels = GradientRgba(size);
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            pixels[(y * size + x) * 4 + 3] = (byte)(255 - x * 255 / (size - 1));
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

    /// <summary>A legacy (no DX10 extension) DDS pixel format: a FourCC, or a set of bit masks.</summary>
    public readonly record struct LegacyLayout(uint PixelFlags, string FourCc, uint BitCount, uint RMask, uint GMask, uint BMask, uint AMask)
    {
        private const uint FourCcFlag = 0x4;
        private const uint RgbaFlags = 0x41;

        public static LegacyLayout Dxt1 { get; } = new(FourCcFlag, "DXT1", 0, 0, 0, 0, 0);
        public static LegacyLayout Dxt3 { get; } = new(FourCcFlag, "DXT3", 0, 0, 0, 0, 0);
        public static LegacyLayout Dxt5 { get; } = new(FourCcFlag, "DXT5", 0, 0, 0, 0, 0);
        public static LegacyLayout Ati1 { get; } = new(FourCcFlag, "ATI1", 0, 0, 0, 0, 0);
        public static LegacyLayout Ati2 { get; } = new(FourCcFlag, "ATI2", 0, 0, 0, 0, 0);
        public static LegacyLayout A8B8G8R8 { get; } = new(RgbaFlags, "", 32, 0x000000FF, 0x0000FF00, 0x00FF0000, 0xFF000000);
        public static LegacyLayout A8R8G8B8 { get; } = new(RgbaFlags, "", 32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000);
    }

    /// <summary>
    /// Encodes <see cref="Gradient"/> to <paramref name="format"/> and returns the bytes of <paramref name="levels"/> mips
    /// back to back, which is how a DDS stores them.
    /// </summary>
    public static byte[] EncodedGradient(CompressionFormat format, int size, int levels)
    {
        var encoder = Encoder(format);
        encoder.OutputOptions.MaxMipMapLevel = levels;
        return [.. encoder.EncodeToRawBytes(MemoryMarshal.AsBytes(Gradient(size).AsSpan()), size, size, PixelFormat.Rgba32).SelectMany(level => level)];
    }

    /// <summary>
    /// Writes a DDS with a legacy pixel format. <paramref name="data"/> is everything after the header: every mip of
    /// every face, face by face.
    /// </summary>
    public static void WriteLegacy(Stream output, LegacyLayout layout, int width, int height, ReadOnlySpan<byte> data, int mipLevels = 1, bool cubeMap = false)
        => WriteHeader(output, width, height, mipLevels, cubeMap, layout, dxgi: null, data);

    /// <summary>Writes a DDS with a DX10 extension naming <paramref name="dxgi"/>; <paramref name="blocks"/> is everything after the headers.</summary>
    public static void WriteDx10(Stream output, DxgiFormat dxgi, int width, int height, ReadOnlySpan<byte> blocks, int mipLevels = 1, bool cubeMap = false)
        => WriteHeader(output, width, height, mipLevels, cubeMap, new LegacyLayout(0x4, "DX10", 0, 0, 0, 0, 0), dxgi, blocks);

    private static void WriteHeader(Stream output, int width, int height, int mipLevels, bool cubeMap, LegacyLayout layout, DxgiFormat? dxgi, ReadOnlySpan<byte> data)
    {
        const uint caps = 0x1000 | 0x8 | 0x400000; // TEXTURE, COMPLEX, MIPMAP
        var header = new byte[128];
        void Put(int offset, uint value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(offset), value);

        Put(0, 0x20534444);
        Put(4, 124);
        Put(8, 0x1 | 0x2 | 0x4 | 0x1000 | (mipLevels > 1 ? 0x20000u : 0)); // CAPS, HEIGHT, WIDTH, PIXELFORMAT, MIPMAPCOUNT
        Put(12, (uint)height);
        Put(16, (uint)width);
        Put(28, (uint)mipLevels);
        Put(76, 32);
        Put(80, layout.PixelFlags);
        if (layout.FourCc.Length == 4)
            Put(84, (uint)(layout.FourCc[0] | layout.FourCc[1] << 8 | layout.FourCc[2] << 16 | layout.FourCc[3] << 24));
        Put(88, layout.BitCount);
        Put(92, layout.RMask);
        Put(96, layout.GMask);
        Put(100, layout.BMask);
        Put(104, layout.AMask);
        Put(108, caps);
        if (cubeMap && dxgi is null) Put(112, 0x200 | 0xFC00);
        output.Write(header);

        if (dxgi is { } format)
        {
            Span<byte> extension = stackalloc byte[20];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(extension, (uint)format);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(extension[4..], 3);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(extension[8..], cubeMap ? 4u : 0);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(extension[12..], 1);
            output.Write(extension);
        }

        output.Write(data);
    }
}
