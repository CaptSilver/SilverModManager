using System.Buffers.Binary;
using SilverModManager.Wabbajack.Transforms;

namespace SilverModManager.Wabbajack.Transforms.Tests;

/// <summary>
/// Reads the raw header bytes of what <see cref="DdsRecompressor"/> writes. The FourCCs, pixel-format flags, caps and
/// DX10 fields are the values the BCnEncoder writer produced before the header was written by hand.
/// </summary>
public class DdsWriterTests
{
    private const uint Magic = 0x20534444;
    private const uint Caps = 0x1, Height = 0x2, Width = 0x4, Pitch = 0x8, PixelFormat = 0x1000, MipMapCount = 0x20000, LinearSize = 0x80000;
    private const uint CapsTexture = 0x1000, CapsComplex = 0x8, CapsMipMap = 0x400000;
    private const uint Caps2CubeMapAllFaces = 0x200 | 0xFC00;

    private static async Task<byte[]> Recompress(DdsTarget target, bool cubeSource = false)
    {
        var source = new MemoryStream();
        if (cubeSource) TestTextures.WriteBc1CubeMap(source);
        else TestTextures.WriteBc1(source);
        var result = new MemoryStream();
        await DdsRecompressor.Managed().Recompress(new MemoryStream(source.ToArray()), target, result, 1, CancellationToken.None);
        return result.ToArray();
    }

    private static uint U32(byte[] dds, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(offset));

    private static string FourCc(byte[] dds) => System.Text.Encoding.ASCII.GetString(dds, 84, 4);

    private static int ExpectedLevelBytes(DxgiFormat format, int width, int height)
    {
        var blockBytes = format switch
        {
            DxgiFormat.BC1_UNORM or DxgiFormat.BC4_UNORM => 8,
            DxgiFormat.BC2_UNORM or DxgiFormat.BC3_UNORM or DxgiFormat.BC5_UNORM or DxgiFormat.BC7_UNORM => 16,
            _ => 0,
        };
        return blockBytes == 0
            ? width * height * 4
            : (width + 3) / 4 * ((height + 3) / 4) * blockBytes;
    }

    private static int ChainBytes(DxgiFormat format, int width, int height, int levels)
    {
        var total = 0;
        for (var level = 0; level < levels; level++)
        {
            total += ExpectedLevelBytes(format, Math.Max(1, width >> level), Math.Max(1, height >> level));
        }
        return total;
    }

    public static TheoryData<DxgiFormat, string> BlockFormats => new()
    {
        { DxgiFormat.BC1_UNORM, "DXT1" },
        { DxgiFormat.BC2_UNORM, "DXT3" },
        { DxgiFormat.BC3_UNORM, "DXT5" },
        { DxgiFormat.BC4_UNORM, "BC4U" },
        { DxgiFormat.BC5_UNORM, "ATI2" },
    };

    [Theory]
    [MemberData(nameof(BlockFormats))]
    public async Task Header_LegacyBlockFormat_CarriesItsFourCcAndSizes(DxgiFormat format, string fourCc)
    {
        var dds = await Recompress(new DdsTarget(32, 32, 3, format));

        Assert.Equal(Magic, U32(dds, 0));
        Assert.Equal(124u, U32(dds, 4));
        Assert.Equal(Caps | Height | Width | PixelFormat | MipMapCount | LinearSize, U32(dds, 8));
        Assert.Equal(32u, U32(dds, 12));
        Assert.Equal(32u, U32(dds, 16));
        Assert.Equal((uint)ExpectedLevelBytes(format, 32, 32), U32(dds, 20));
        Assert.Equal(0u, U32(dds, 24));
        Assert.Equal(3u, U32(dds, 28));
        Assert.Equal(32u, U32(dds, 76));
        Assert.Equal(0x4u, U32(dds, 80));
        Assert.Equal(fourCc, FourCc(dds));
        Assert.Equal(0u, U32(dds, 88));
        Assert.Equal(CapsTexture | CapsComplex | CapsMipMap, U32(dds, 108));
        Assert.Equal(0u, U32(dds, 112));
        Assert.Equal(128 + ChainBytes(format, 32, 32, 3), dds.Length);
    }

    [Fact]
    public async Task Header_Bc7_HasADx10ExtensionForOneTexture()
    {
        var dds = await Recompress(new DdsTarget(32, 32, 3, DxgiFormat.BC7_UNORM));

        Assert.Equal("DX10", FourCc(dds));
        Assert.Equal(0x4u, U32(dds, 80));
        Assert.Equal(98u, U32(dds, 128));
        Assert.Equal(3u, U32(dds, 132));
        Assert.Equal(0u, U32(dds, 136));
        Assert.Equal(1u, U32(dds, 140));
        Assert.Equal(0u, U32(dds, 144));
        Assert.Equal((uint)ExpectedLevelBytes(DxgiFormat.BC7_UNORM, 32, 32), U32(dds, 20));
        Assert.Equal(148 + ChainBytes(DxgiFormat.BC7_UNORM, 32, 32, 3), dds.Length);
    }

    [Fact]
    public async Task Header_SingleLevel_HasNoMipFlagsOrCaps()
    {
        var dds = await Recompress(new DdsTarget(32, 32, 1, DxgiFormat.BC1_UNORM));

        Assert.Equal(Caps | Height | Width | PixelFormat | LinearSize, U32(dds, 8));
        Assert.Equal(1u, U32(dds, 28));
        Assert.Equal(CapsTexture, U32(dds, 108));
        Assert.Equal(128 + ChainBytes(DxgiFormat.BC1_UNORM, 32, 32, 1), dds.Length);
    }

    [Theory]
    [InlineData(DxgiFormat.BC1_UNORM)]
    [InlineData(DxgiFormat.BC3_UNORM)]
    public async Task Header_CubeMap_SetsAllSixFaceBits(DxgiFormat format)
    {
        var dds = await Recompress(new DdsTarget(32, 32, 3, format), cubeSource: true);

        Assert.Equal(Caps2CubeMapAllFaces, U32(dds, 112));
        Assert.Equal(CapsTexture | CapsComplex | CapsMipMap, U32(dds, 108));
        Assert.Equal(128 + 6 * ChainBytes(format, 32, 32, 3), dds.Length);
    }

    [Fact]
    public async Task Header_Bc7CubeMap_NamesTextureCubeInTheDx10Extension()
    {
        var dds = await Recompress(new DdsTarget(32, 32, 3, DxgiFormat.BC7_UNORM), cubeSource: true);

        Assert.Equal(Caps2CubeMapAllFaces, U32(dds, 112));
        Assert.Equal(98u, U32(dds, 128));
        Assert.Equal(3u, U32(dds, 132));
        Assert.Equal(0x4u, U32(dds, 136));
        Assert.Equal(1u, U32(dds, 140));
        Assert.Equal(148 + 6 * ChainBytes(DxgiFormat.BC7_UNORM, 32, 32, 3), dds.Length);
    }

    [Theory]
    [InlineData(DxgiFormat.BC1_UNORM, 6, 10, 4)]
    [InlineData(DxgiFormat.BC3_UNORM, 6, 10, 4)]
    [InlineData(DxgiFormat.BC7_UNORM, 6, 10, 4)]
    [InlineData(DxgiFormat.BC1_UNORM, 32, 32, 6)]
    [InlineData(DxgiFormat.BC5_UNORM, 32, 32, 6)]
    [InlineData(DxgiFormat.BC7_UNORM, 32, 32, 6)]
    public async Task Header_FullChain_CountsEveryLevelDownToOneByOne(DxgiFormat format, int width, int height, int levels)
    {
        var dds = await Recompress(new DdsTarget(width, height, 0, format));

        Assert.Equal((uint)height, U32(dds, 12));
        Assert.Equal((uint)width, U32(dds, 16));
        Assert.Equal((uint)levels, U32(dds, 28));
        var headerBytes = format == DxgiFormat.BC7_UNORM ? 148 : 128;
        Assert.Equal(headerBytes + ChainBytes(format, width, height, levels), dds.Length);
    }

    [Theory]
    [InlineData(DxgiFormat.R8G8B8A8_UNORM, 0x000000FFu, 0x0000FF00u, 0x00FF0000u, 0xFF000000u)]
    [InlineData(DxgiFormat.B8G8R8A8_UNORM, 0x00FF0000u, 0x0000FF00u, 0x000000FFu, 0xFF000000u)]
    public async Task Header_Uncompressed32Bit_UsesBitMasksAndAPitch(DxgiFormat format, uint r, uint g, uint b, uint a)
    {
        var dds = await Recompress(new DdsTarget(32, 32, 3, format));

        Assert.Equal(Caps | Height | Width | Pitch | PixelFormat | MipMapCount, U32(dds, 8));
        Assert.Equal(128u, U32(dds, 20));
        Assert.Equal(3u, U32(dds, 28));
        Assert.Equal(0x41u, U32(dds, 80));
        Assert.Equal(0u, U32(dds, 84));
        Assert.Equal(32u, U32(dds, 88));
        Assert.Equal(r, U32(dds, 92));
        Assert.Equal(g, U32(dds, 96));
        Assert.Equal(b, U32(dds, 100));
        Assert.Equal(a, U32(dds, 104));
        Assert.Equal(128 + ChainBytes(format, 32, 32, 3), dds.Length);
    }
}
