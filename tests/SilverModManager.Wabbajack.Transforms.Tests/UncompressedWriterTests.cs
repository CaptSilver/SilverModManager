using System.Buffers.Binary;
using BCnEncoder.Shared;
using SilverModManager.Wabbajack.Transforms;
using DdsFile = BCnEncoder.Shared.ImageFiles.DdsFile;

namespace SilverModManager.Wabbajack.Transforms.Tests;

public class UncompressedWriterTests
{
    private const uint PixelAlphaPixels = 0x1, PixelAlpha = 0x2, PixelRgb = 0x40, PixelLuminance = 0x20000, PixelFourCc = 0x4;
    private const uint FlagPitch = 0x8, FlagLinearSize = 0x80000;

    /// <summary>Four pixels whose channels are all different, so a swapped or dropped channel shows.</summary>
    private static readonly byte[] SourcePixels = [10, 20, 30, 40, 250, 130, 70, 200, 0, 255, 128, 99, 33, 66, 99, 132];

    private static byte[] Source(DxgiFormat format, int width, int height, byte[] pixels, int mips = 1, bool cube = false)
    {
        var stream = new MemoryStream();
        TestTextures.WriteDx10(stream, format, width, height, pixels, mips, cube);
        return stream.ToArray();
    }

    private static async Task<byte[]> Recompress(byte[] source, DdsTarget target)
    {
        var result = new MemoryStream();
        await DdsRecompressor.Managed().Recompress(new MemoryStream(source), target, result, 1, CancellationToken.None);
        return result.ToArray();
    }

    private static uint U32(byte[] dds, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(offset));

    private static byte[] Rgba(DdsHeader header, byte[] dds, int level = 0, int face = 0)
    {
        var width = Math.Max(1, header.Width >> level);
        var height = Math.Max(1, header.Height >> level);
        var data = dds.AsSpan((int)header.LevelOffset(face, level), (int)header.LevelSize(level));
        return System.Runtime.InteropServices.MemoryMarshal.AsBytes(header.Reader!.Unpack(data, width, height).AsSpan()).ToArray();
    }

    // The 8-bit layouts: what the target keeps of (r, g, b, a).
    public static TheoryData<DxgiFormat, DxgiFormat, int> ExactLayouts => new()
    {
        { DxgiFormat.R8G8B8A8_UNORM, DxgiFormat.R8G8B8A8_UNORM, 0 },
        { DxgiFormat.B8G8R8A8_UNORM, DxgiFormat.R8G8B8A8_UNORM, 0 },
        { DxgiFormat.B8G8R8X8_UNORM, DxgiFormat.R8G8B8A8_UNORM, 1 },
        { DxgiFormat.R8_UNORM, DxgiFormat.R8G8B8A8_UNORM, 2 },
        { DxgiFormat.R8G8_UNORM, DxgiFormat.R8G8B8A8_UNORM, 3 },
        { DxgiFormat.A8_UNORM, DxgiFormat.R8G8B8A8_UNORM, 4 },
        { DxgiFormat.R16_UNORM, DxgiFormat.R8G8B8A8_UNORM, 2 },
        { DxgiFormat.R8G8B8A8_UNORM_SRGB, DxgiFormat.R8G8B8A8_UNORM_SRGB, 0 },
        { DxgiFormat.B8G8R8A8_UNORM_SRGB, DxgiFormat.R8G8B8A8_UNORM_SRGB, 0 },
        { DxgiFormat.B8G8R8X8_UNORM_SRGB, DxgiFormat.R8G8B8A8_UNORM_SRGB, 1 },
    };

    private static byte[] Expected(int kind)
    {
        var expected = (byte[])SourcePixels.Clone();
        for (var i = 0; i < expected.Length; i += 4)
        {
            switch (kind)
            {
                case 1: expected[i + 3] = 255; break;
                case 2: expected[i + 1] = 0; expected[i + 2] = 0; expected[i + 3] = 255; break;
                case 3: expected[i + 2] = 0; expected[i + 3] = 255; break;
                case 4: expected[i] = 0; expected[i + 1] = 0; expected[i + 2] = 0; break;
            }
        }
        return expected;
    }

    [Theory]
    [MemberData(nameof(ExactLayouts))]
    public async Task Recompress_ToAnEightBitLayout_RoundTripsThroughOurReader(DxgiFormat target, DxgiFormat sourceFormat, int kind)
    {
        var result = await Recompress(Source(sourceFormat, 2, 2, SourcePixels), new DdsTarget(2, 2, 1, target));

        var header = DdsHeader.Read(result);
        Assert.Equal(Expected(kind), Rgba(header, result));
        Assert.Equal(target.IsSrgb(), header.IsSrgb);
    }

    [Theory]
    [InlineData(DxgiFormat.R8G8B8A8_UNORM)]
    [InlineData(DxgiFormat.B8G8R8A8_UNORM)]
    public async Task Recompress_ToFourByteLayouts_LoadsInBcnEncoder(DxgiFormat target)
    {
        var result = await Recompress(Source(DxgiFormat.R8G8B8A8_UNORM, 2, 2, SourcePixels), new DdsTarget(2, 2, 1, target));

        var data = DdsFile.Load(new MemoryStream(result)).Faces.Single().MipMaps[0].Data;
        // BCnEncoder hands the bytes back as stored.
        var expected = target == DxgiFormat.B8G8R8A8_UNORM
            ? SourcePixels.Chunk(4).SelectMany(p => new[] { p[2], p[1], p[0], p[3] }).ToArray()
            : SourcePixels;
        Assert.Equal(expected, data);
    }

    public static TheoryData<DxgiFormat, int> SixteenBitLayouts => new()
    {
        { DxgiFormat.B5G6R5_UNORM, 4 },
        { DxgiFormat.B5G5R5A1_UNORM, 4 },
        { DxgiFormat.B4G4R4A4_UNORM, 9 },
    };

    [Theory]
    [MemberData(nameof(SixteenBitLayouts))]
    public async Task Recompress_ToASixteenBitLayout_StaysWithinTheQuantisation(DxgiFormat target, int tolerance)
    {
        var result = await Recompress(Source(DxgiFormat.R8G8B8A8_UNORM, 2, 2, SourcePixels), new DdsTarget(2, 2, 1, target));

        var pixels = Rgba(DdsHeader.Read(result), result);
        for (var i = 0; i < pixels.Length; i++)
        {
            var isAlpha = i % 4 == 3;
            if (isAlpha && target == DxgiFormat.B5G6R5_UNORM) { Assert.Equal(255, pixels[i]); continue; }
            if (isAlpha && target == DxgiFormat.B5G5R5A1_UNORM) { Assert.Equal(SourcePixels[i] >= 128 ? 255 : 0, pixels[i]); continue; }
            Assert.InRange(pixels[i], SourcePixels[i] - tolerance, SourcePixels[i] + tolerance);
        }
    }

    // Raw pixel-format fields DirectXTex writes for each legacy layout: flags, bit count and the four masks.
    public static TheoryData<DxgiFormat, uint, uint, uint, uint, uint, uint> LegacyHeaders => new()
    {
        { DxgiFormat.R8G8B8A8_UNORM, PixelRgb | PixelAlphaPixels, 32, 0x000000FF, 0x0000FF00, 0x00FF0000, 0xFF000000 },
        { DxgiFormat.B8G8R8A8_UNORM, PixelRgb | PixelAlphaPixels, 32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000 },
        { DxgiFormat.B8G8R8X8_UNORM, PixelRgb, 32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0 },
        { DxgiFormat.R8_UNORM, PixelLuminance, 8, 0xFF, 0, 0, 0 },
        { DxgiFormat.R8G8_UNORM, PixelLuminance | PixelAlphaPixels, 16, 0x00FF, 0, 0, 0xFF00 },
        { DxgiFormat.B5G6R5_UNORM, PixelRgb, 16, 0xF800, 0x07E0, 0x001F, 0 },
        { DxgiFormat.B5G5R5A1_UNORM, PixelRgb | PixelAlphaPixels, 16, 0x7C00, 0x03E0, 0x001F, 0x8000 },
        { DxgiFormat.B4G4R4A4_UNORM, PixelRgb | PixelAlphaPixels, 16, 0x0F00, 0x00F0, 0x000F, 0xF000 },
        { DxgiFormat.R16_UNORM, PixelLuminance, 16, 0xFFFF, 0, 0, 0 },
        { DxgiFormat.A8_UNORM, PixelAlpha, 8, 0, 0, 0, 0xFF },
    };

    [Theory]
    [MemberData(nameof(LegacyHeaders))]
    public async Task Recompress_ToAnUncompressedLayout_WritesTheLegacyBitmaskHeaderWithPitch(
        DxgiFormat target, uint flags, uint bits, uint r, uint g, uint b, uint a)
    {
        var result = await Recompress(Source(DxgiFormat.R8G8B8A8_UNORM, 2, 2, SourcePixels), new DdsTarget(2, 2, 1, target));

        Assert.Equal(FlagPitch, U32(result, 8) & (FlagPitch | FlagLinearSize));
        Assert.Equal(2 * bits / 8, U32(result, 20));
        Assert.Equal(flags, U32(result, 80));
        Assert.Equal(bits, U32(result, 88));
        Assert.Equal([r, g, b, a], new[] { U32(result, 92), U32(result, 96), U32(result, 100), U32(result, 104) });
        Assert.Equal(128 + 2 * 2 * (int)bits / 8, result.Length);
    }

    [Theory]
    [InlineData(DxgiFormat.R8G8B8A8_UNORM_SRGB)]
    [InlineData(DxgiFormat.B8G8R8A8_UNORM_SRGB)]
    [InlineData(DxgiFormat.B8G8R8X8_UNORM_SRGB)]
    public async Task Recompress_ToAnSrgbUncompressedLayout_WritesADx10Header(DxgiFormat target)
    {
        var result = await Recompress(Source(DxgiFormat.R8G8B8A8_UNORM_SRGB, 2, 2, SourcePixels), new DdsTarget(2, 2, 1, target));

        Assert.Equal(PixelFourCc, U32(result, 80));
        Assert.Equal(0x30315844u, U32(result, 84));
        Assert.Equal((uint)target, U32(result, 128));
        Assert.Equal(FlagPitch, U32(result, 8) & (FlagPitch | FlagLinearSize));
        Assert.Equal(148 + 2 * 2 * 4, result.Length);
    }

    [Fact]
    public async Task Recompress_ToAnUncompressedLayoutWithMips_StoresEveryLevelUnpadded()
    {
        var flat = Enumerable.Repeat<byte>(100, 8 * 8 * 4).ToArray();
        for (var i = 3; i < flat.Length; i += 4) flat[i] = 255;

        var result = await Recompress(Source(DxgiFormat.R8G8B8A8_UNORM, 8, 8, flat), new DdsTarget(8, 8, 0, DxgiFormat.R8_UNORM));

        var header = DdsHeader.Read(result);
        Assert.Equal(4, header.MipLevels);
        Assert.Equal(128 + 64 + 16 + 4 + 1, result.Length);
        for (var level = 0; level < 4; level++)
            Assert.All(Rgba(header, result, level).Chunk(4), p => Assert.Equal([100, 0, 0, 255], p));
    }

    [Fact]
    public async Task Recompress_CubeMapToASixteenBitLayout_WritesSixFaces()
    {
        var face = Enumerable.Repeat<byte>(200, 4 * 4 * 4).ToArray();
        var cube = Source(DxgiFormat.R8G8B8A8_UNORM, 4, 4, [.. Enumerable.Repeat(face, 6).SelectMany(x => x)], cube: true);

        var result = await Recompress(cube, new DdsTarget(4, 4, 1, DxgiFormat.B5G6R5_UNORM));

        var header = DdsHeader.Read(result);
        Assert.Equal(6, header.FaceCount);
        Assert.Equal(128 + 6 * 4 * 4 * 2, result.Length);
        for (var f = 0; f < 6; f++)
            Assert.All(Rgba(header, result, face: f).Chunk(4), p => Assert.InRange(p[0], 196, 204));
    }

    [Fact]
    public void Pack_RefusesAFormatItHasNoLayoutFor()
    {
        Assert.Throws<UnsupportedTextureFormatException>(() => UncompressedWriter.Pack(new byte[4], DxgiFormat.BC7_UNORM));
    }
}
