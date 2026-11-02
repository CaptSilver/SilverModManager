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
        var rgb24 = new LegacyLayout(0x40, "", 24, 0xFF0000, 0xFF00, 0xFF, 0);

        var ex = Assert.Throws<UnsupportedTextureFormatException>(() => DdsHeader.Read(Legacy(rgb24, 2, 2, new byte[12])));

        Assert.Equal("cannot read the source texture: fourcc=none, flags=0x40, bpp=24, masks=0xFF0000/0xFF00/0xFF/0x0, dxgi=UNKNOWN (0)", ex.Message);
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
}
