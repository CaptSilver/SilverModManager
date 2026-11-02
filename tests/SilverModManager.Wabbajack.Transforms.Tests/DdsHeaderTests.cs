using BCnEncoder.Shared;
using SilverModManager.Wabbajack.Transforms;
using static SilverModManager.Wabbajack.Transforms.TestTextures;

namespace SilverModManager.Wabbajack.Transforms.Tests;

public class DdsHeaderTests
{
    private static byte[] Legacy(LegacyLayout layout, int width, int height, byte[] data, int mips = 1, bool cube = false)
    {
        var stream = new MemoryStream();
        WriteLegacy(stream, layout, width, height, data, mips, cube);
        return stream.ToArray();
    }

    private static byte[] Dx10(DxgiFormat format, int width, int height, byte[] data, int mips = 1, bool cube = false)
    {
        var stream = new MemoryStream();
        WriteDx10(stream, format, width, height, data, mips, cube);
        return stream.ToArray();
    }

    public static TheoryData<string, CompressionFormat, DxgiFormat> LegacyBlockFormats => new()
    {
        { "DXT1", CompressionFormat.Bc1, DxgiFormat.BC1_UNORM },
        { "DXT3", CompressionFormat.Bc2, DxgiFormat.BC2_UNORM },
        { "DXT5", CompressionFormat.Bc3, DxgiFormat.BC3_UNORM },
        { "ATI1", CompressionFormat.Bc4, DxgiFormat.BC4_UNORM },
        { "ATI2", CompressionFormat.Bc5, DxgiFormat.BC5_UNORM },
    };

    private static LegacyLayout LayoutOf(string name) => name switch
    {
        "DXT1" => LegacyLayout.Dxt1,
        "DXT3" => LegacyLayout.Dxt3,
        "DXT5" => LegacyLayout.Dxt5,
        "ATI1" => LegacyLayout.Ati1,
        "ATI2" => LegacyLayout.Ati2,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(LegacyBlockFormats))]
    public void ALegacyFourCcHeaderGivesItsSizeFormatAndDataOffset(string fourCc, CompressionFormat encode, DxgiFormat expected)
    {
        var header = DdsHeader.Read(Legacy(LayoutOf(fourCc), 16, 8, EncodedGradient(encode, 16, 1)[..(4 * 2 * (expected is DxgiFormat.BC1_UNORM or DxgiFormat.BC4_UNORM ? 8 : 16))]));

        Assert.Equal(16, header.Width);
        Assert.Equal(8, header.Height);
        Assert.Equal(1, header.MipLevels);
        Assert.Equal(1, header.FaceCount);
        Assert.Equal(expected, header.Format);
        Assert.Equal(128, header.DataOffset);
    }

    [Fact]
    public void ADx10HeaderMovesTheDataOffsetPastTheExtension()
    {
        var header = DdsHeader.Read(Dx10(DxgiFormat.BC7_UNORM, 8, 8, new byte[64]));

        Assert.Equal(DxgiFormat.BC7_UNORM, header.Format);
        Assert.Equal(148, header.DataOffset);
    }

    [Fact]
    public void ATwoMipSourceCountsBothLevelsInTheFaceSize()
    {
        // 16x16 BC1: 4x4 blocks of 8 bytes = 128, then 2x2 blocks = 32.
        var header = DdsHeader.Read(Legacy(LegacyLayout.Dxt1, 16, 16, new byte[160], mips: 2));

        Assert.Equal(2, header.MipLevels);
        Assert.Equal(128, header.LevelSize(0));
        Assert.Equal(32, header.LevelSize(1));
        Assert.Equal(160, header.FaceSize);
    }

    [Fact]
    public void ACubeMapHasSixFacesAtTheFaceSizeStride()
    {
        var header = DdsHeader.Read(Legacy(LegacyLayout.Dxt5, 8, 8, new byte[6 * 64], cube: true));

        Assert.Equal(6, header.FaceCount);
        Assert.Equal(128, header.TopLevelOffset(0));
        Assert.Equal(128 + 64, header.TopLevelOffset(1));
        Assert.Equal(128 + 5 * 64, header.TopLevelOffset(5));
    }

    [Fact]
    public void ADx10CubeMapHasSixFaces()
    {
        var header = DdsHeader.Read(Dx10(DxgiFormat.BC7_UNORM, 8, 8, new byte[6 * 64], cube: true));

        Assert.Equal(6, header.FaceCount);
        Assert.Equal(148 + 64, header.TopLevelOffset(1));
    }

    [Fact]
    public void BlockPitchRoundsUpToWholeBlocksAndNeverDropsBelowOne()
    {
        var header = DdsHeader.Read(Legacy(LegacyLayout.Dxt1, 6, 10, new byte[48], mips: 4));

        Assert.Equal(16, header.RowPitch(0)); // two blocks across
        Assert.Equal(48, header.LevelSize(0)); // three block rows
        Assert.Equal(8, header.RowPitch(3)); // 1x1 is still one block
        Assert.Equal(8, header.LevelSize(3));
    }

    [Fact]
    public void UncompressedPitchFollowsTheBitCount()
    {
        var header = DdsHeader.Read(Legacy(LegacyLayout.A8B8G8R8, 5, 2, new byte[40]));

        Assert.Equal(DxgiFormat.R8G8B8A8_UNORM, header.Format);
        Assert.Equal(20, header.RowPitch(0));
        Assert.Equal(40, header.LevelSize(0));
    }

    [Fact]
    public void AnA8R8G8B8MaskSetIsBgra()
        => Assert.Equal(DxgiFormat.B8G8R8A8_UNORM, DdsHeader.Read(Legacy(LegacyLayout.A8R8G8B8, 2, 2, new byte[16])).Format);

    [Fact]
    public void ABc1HeaderWithAlphaPixelsIsMarkedAsSuch()
    {
        var withAlpha = LegacyLayout.Dxt1 with { PixelFlags = 0x5 };

        Assert.True(DdsHeader.Read(Legacy(withAlpha, 4, 4, new byte[8])).AlphaPixels);
        Assert.False(DdsHeader.Read(Legacy(LegacyLayout.Dxt1, 4, 4, new byte[8])).AlphaPixels);
    }

    [Fact]
    public void AnUnsupportedDx10FormatPrintsItsNameAndNumber()
    {
        var ex = Assert.Throws<UnsupportedTextureFormatException>(() => DdsHeader.Read(Dx10(DxgiFormat.BC6H_UF16, 4, 4, new byte[16])));

        Assert.Equal("cannot read the source texture: fourcc=DX10, flags=0x4, bpp=0, masks=0x0/0x0/0x0/0x0, dxgi=BC6H_UF16 (95)", ex.Message);
    }

    [Fact]
    public void ADxgiValueTheEnumLacksPrintsAsTheBareNumber()
    {
        var ex = Assert.Throws<UnsupportedTextureFormatException>(() => DdsHeader.Read(Dx10((DxgiFormat)133, 4, 4, new byte[16])));

        Assert.EndsWith("dxgi=133", ex.Message);
    }

    [Fact]
    public void AnUnmatchedBitmaskPrintsItsMasks()
    {
        var scrambled = new LegacyLayout(0x40, "", 32, 0xFF00FF, 0xFF00, 0xFF0000, 0);

        var ex = Assert.Throws<UnsupportedTextureFormatException>(() => DdsHeader.Read(Legacy(scrambled, 2, 2, new byte[16])));

        Assert.Equal("cannot read the source texture: fourcc=none, flags=0x40, bpp=32, masks=0xFF00FF/0xFF00/0xFF0000/0x0, dxgi=UNKNOWN (0)", ex.Message);
    }

    [Fact]
    public void AnUnknownFourCcIsNamed()
    {
        var ex = Assert.Throws<UnsupportedTextureFormatException>(() => DdsHeader.Read(Legacy(LegacyLayout.Dxt1 with { FourCc = "BC6H" }, 4, 4, new byte[16])));

        Assert.Contains("fourcc=BC6H", ex.Message);
    }

    [Fact]
    public void ACubeMapWithMissingFacesIsUnsupported()
    {
        var bytes = Legacy(LegacyLayout.Dxt1, 4, 4, new byte[48], cube: true);
        bytes[113] = 0x02 | 0x04; // caps2 high byte: cube map bit stays, faces trimmed to a subset
        bytes[114] = 0;

        Assert.Throws<UnsupportedTextureFormatException>(() => DdsHeader.Read(bytes));
    }

    [Fact]
    public void BytesThatAreNotADdsAreNotAHeader()
        => Assert.Throws<InvalidDataException>(() => DdsHeader.Read(new byte[200]));

    [Fact]
    public void PixelDataShorterThanTheHeaderSaysIsNotAHeader()
        => Assert.Throws<InvalidDataException>(() => DdsHeader.Read(Legacy(LegacyLayout.Dxt1, 16, 16, new byte[64])));

    [Theory]
    [InlineData(DxgiFormat.BC1_UNORM_SRGB, DxgiFormat.BC1_UNORM)]
    [InlineData(DxgiFormat.BC2_UNORM_SRGB, DxgiFormat.BC2_UNORM)]
    [InlineData(DxgiFormat.BC3_UNORM_SRGB, DxgiFormat.BC3_UNORM)]
    [InlineData(DxgiFormat.BC7_UNORM_SRGB, DxgiFormat.BC7_UNORM)]
    [InlineData(DxgiFormat.R8G8B8A8_UNORM_SRGB, DxgiFormat.R8G8B8A8_UNORM)]
    [InlineData(DxgiFormat.B8G8R8A8_UNORM_SRGB, DxgiFormat.B8G8R8A8_UNORM)]
    [InlineData(DxgiFormat.B8G8R8X8_UNORM_SRGB, DxgiFormat.B8G8R8X8_UNORM)]
    public void AnSrgbDx10FormatReadsAsItsUnormTwinAndSaysSo(DxgiFormat srgb, DxgiFormat unorm)
    {
        var header = DdsHeader.Read(Dx10(srgb, 4, 4, new byte[64]));

        Assert.Equal(unorm, header.Format);
        Assert.True(header.IsSrgb);
        Assert.False(DdsHeader.Read(Dx10(unorm, 4, 4, new byte[64])).IsSrgb);
    }

    [Theory]
    [InlineData(24, 0xFF0000u, 0xFF00u, 0xFFu, 0u, 0x40u, 3)]
    [InlineData(16, 0xF800u, 0x7E0u, 0x1Fu, 0u, 0x40u, 2)]
    public void ALowerMipOfAnUncompressedSourceStartsAfterTheRealBytesOfTheLevelAbove(int bits, uint r, uint g, uint b, uint a, uint flags, int bytesPerPixel)
    {
        var layout = new LegacyLayout(flags, "", (uint)bits, r, g, b, a);
        var level0 = Enumerable.Range(0, 4 * bytesPerPixel).Select(i => (byte)(10 + i)).ToArray();
        var level1 = Enumerable.Range(0, bytesPerPixel).Select(i => (byte)(200 + i)).ToArray();
        var dds = Legacy(layout, 2, 2, [.. level0, .. level1], mips: 2);

        var header = DdsHeader.Read(dds);

        Assert.Equal(128 + 4 * bytesPerPixel, header.LevelOffset(0, 1));
        Assert.Equal(bytesPerPixel, header.LevelSize(1));
        var expected = header.Reader!.Unpack(level1, 1, 1);
        var actual = header.Reader.Unpack(dds.AsSpan((int)header.LevelOffset(0, 1), (int)header.LevelSize(1)), 1, 1);
        Assert.Equal(expected, actual);
        Assert.NotEqual(header.Reader.Unpack(level0.AsSpan(0, bytesPerPixel), 1, 1), actual);
    }

    public static TheoryData<string, DxgiFormat> FourCcRows => new()
    {
        { "DXT1", DxgiFormat.BC1_UNORM }, { "DXT2", DxgiFormat.BC2_UNORM }, { "DXT3", DxgiFormat.BC2_UNORM },
        { "DXT4", DxgiFormat.BC3_UNORM }, { "DXT5", DxgiFormat.BC3_UNORM },
        { "A2D5", DxgiFormat.BC3_UNORM }, { "xGBR", DxgiFormat.BC3_UNORM }, { "RxBG", DxgiFormat.BC3_UNORM },
        { "RBxG", DxgiFormat.BC3_UNORM }, { "xRBG", DxgiFormat.BC3_UNORM }, { "RGxB", DxgiFormat.BC3_UNORM },
        { "xGxR", DxgiFormat.BC3_UNORM }, { "GXRB", DxgiFormat.BC3_UNORM }, { "GRXB", DxgiFormat.BC3_UNORM },
        { "RXGB", DxgiFormat.BC3_UNORM }, { "BRGX", DxgiFormat.BC3_UNORM },
        { "ATI1", DxgiFormat.BC4_UNORM }, { "BC4U", DxgiFormat.BC4_UNORM }, { "BC4S", DxgiFormat.BC4_SNORM },
        { "ATI2", DxgiFormat.BC5_UNORM }, { "BC5U", DxgiFormat.BC5_UNORM }, { "A2XY", DxgiFormat.BC5_UNORM },
        { "BC5S", DxgiFormat.BC5_SNORM }, { "BC7L", DxgiFormat.BC7_UNORM }, { "BC7\0", DxgiFormat.BC7_UNORM },
    };

    [Theory]
    [MemberData(nameof(FourCcRows))]
    public void EveryBlockFourCcMapsToItsDxgiFormat(string fourCc, DxgiFormat expected)
        => Assert.Equal(expected, DdsHeader.Read(Legacy(LegacyLayout.Dxt1 with { FourCc = fourCc }, 4, 4, new byte[16])).Format);

    [Fact]
    public void ABc6hFourCcIsUnsupportedAndNamesItsDxgiFormat()
    {
        var ex = Assert.Throws<UnsupportedTextureFormatException>(() => DdsHeader.Read(Legacy(LegacyLayout.Dxt1 with { FourCc = "BC6H" }, 4, 4, new byte[16])));

        Assert.Contains("BC6H_UF16 (95)", ex.Message);
    }

    [Theory]
    [InlineData(111, "D3DFMT_R16F")]
    [InlineData(116, "D3DFMT_A32B32G32R32F")]
    [InlineData(60, "D3DFMT_V8U8")]
    [InlineData(110, "D3DFMT_Q16W16V16U16")]
    public void ANumericD3dFormatThatIsNotDecodedIsNamed(int d3dFormat, string name)
    {
        var bytes = Legacy(LegacyLayout.Dxt1, 4, 4, new byte[16]);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(84), (uint)d3dFormat);

        var ex = Assert.Throws<UnsupportedTextureFormatException>(() => DdsHeader.Read(bytes));

        Assert.Contains($"fourcc={d3dFormat} ({name})", ex.Message);
    }

    [Theory]
    [InlineData("YUY2", "D3DFMT_YUY2")]
    [InlineData("UYVY", "D3DFMT_UYVY")]
    [InlineData("RGBG", "D3DFMT_R8G8_B8G8")]
    [InlineData("GRGB", "D3DFMT_G8R8_G8B8")]
    public void ATextD3dFormatThatIsNotDecodedIsNamed(string fourCc, string name)
    {
        var ex = Assert.Throws<UnsupportedTextureFormatException>(() => DdsHeader.Read(Legacy(LegacyLayout.Dxt1 with { FourCc = fourCc }, 4, 4, new byte[16])));

        Assert.Contains($"fourcc={fourCc} ({name})", ex.Message);
    }

    [Fact]
    public void Bc4AndBc5SnormDx10FormatsAreReadAsSigned()
    {
        Assert.Equal(DxgiFormat.BC4_SNORM, DdsHeader.Read(Dx10(DxgiFormat.BC4_SNORM, 4, 4, new byte[8])).Format);
        Assert.Equal(DxgiFormat.BC5_SNORM, DdsHeader.Read(Dx10(DxgiFormat.BC5_SNORM, 4, 4, new byte[16])).Format);
        Assert.Equal(16, DdsHeader.Read(Dx10(DxgiFormat.BC5_SNORM, 4, 4, new byte[16])).LevelSize(0));
    }

    private static byte[] TwoColourPalette(byte alpha = 255)
    {
        var palette = new byte[1024];
        palette[0] = 255; palette[3] = alpha;      // entry 0: red
        palette[6] = 255; palette[7] = alpha;      // entry 1: blue
        return palette;
    }

    [Fact]
    public void AP8SourceReadsItsPaletteAndStartsPixelsAfterIt()
    {
        var layout = new LegacyLayout(0x20, "", 8, 0, 0, 0, 0);
        var dds = Legacy(layout, 2, 2, [.. TwoColourPalette(), 0, 1, 1, 0]);

        var header = DdsHeader.Read(dds);
        var pixels = header.Reader!.Unpack(dds.AsSpan((int)header.TopLevelOffset(0), (int)header.LevelSize(0)), 2, 2);

        Assert.Equal(128 + 1024, header.DataOffset);
        Assert.Equal(DxgiFormat.R8G8B8A8_UNORM, header.Format);
        var red = new ColorRgba32(255, 0, 0, 255);
        var blue = new ColorRgba32(0, 0, 255, 255);
        Assert.Equal([red, blue, blue, red], pixels);
    }

    [Fact]
    public void AnA8P8SourceTakesItsAlphaFromTheHighByteOfEachPixel()
    {
        var layout = new LegacyLayout(0x21, "", 16, 0, 0, 0, 0xFF00);
        var dds = Legacy(layout, 2, 1, [.. TwoColourPalette(alpha: 0), 0, 128, 1, 64]);

        var header = DdsHeader.Read(dds);
        var pixels = header.Reader!.Unpack(dds.AsSpan((int)header.TopLevelOffset(0), (int)header.LevelSize(0)), 2, 1);

        Assert.Equal([new ColorRgba32(255, 0, 0, 128), new ColorRgba32(0, 0, 255, 64)], pixels);
    }

    [Fact]
    public void APaletteCutShortIsNotAHeader()
        => Assert.Throws<InvalidDataException>(() => DdsHeader.Read(Legacy(new LegacyLayout(0x20, "", 8, 0, 0, 0, 0), 2, 2, new byte[100])));
}
