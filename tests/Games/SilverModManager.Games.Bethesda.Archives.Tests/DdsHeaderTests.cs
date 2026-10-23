using System.Buffers.Binary;
using System.Text;

namespace SilverModManager.Games.Bethesda.Archives.Tests;

// An extracted texture is hashed against what a modlist recorded, so these pin every header byte rather
// than reading back a few fields.
public class DdsHeaderTests
{
    private const uint Dxt1 = 0x31545844, Dx10 = 0x30315844, Bc5U = 0x55354342;

    private static byte[] Expected(uint flags, uint height, uint width, uint pitch, uint mips, uint[] pixelFormat, uint caps, uint[]? dx10 = null)
    {
        var words = new List<uint> { 124, flags, height, width, pitch, 1, mips };
        words.AddRange(new uint[11]);
        words.AddRange(pixelFormat);
        words.AddRange([caps, 0, 0, 0, 0]);
        if (dx10 is not null) words.AddRange(dx10);

        var bytes = new byte[4 + words.Count * 4];
        Encoding.ASCII.GetBytes("DDS ").CopyTo(bytes, 0);
        for (var i = 0; i < words.Count; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4 + i * 4), words[i]);
        return bytes;
    }

    private static uint[] FourCc(uint code) => [32, 0x4, code, 0, 0, 0, 0, 0];

    [Fact]
    public void BlockCompressedMipChainShouldCarryTheComplexAndMipmapCaps()
    {
        DdsHeader.Build(64, 32, 2, 71, isCubeMap: false).Should().Equal(
            Expected(0xA1007, 32, 64, 1024, 2, FourCc(Dxt1), 0x401008));
    }

    [Fact]
    public void SingleMipTextureShouldCarryOnlyTheTextureCap()
    {
        DdsHeader.Build(4, 4, 1, 71, isCubeMap: false).Should().Equal(
            Expected(0xA1007, 4, 4, 8, 1, FourCc(Dxt1), 0x1000));
    }

    [Fact]
    public void Bc5ShouldUseTheBc5UFourCc()
    {
        DdsHeader.Build(8, 8, 1, 83, isCubeMap: false).Should().Equal(
            Expected(0xA1007, 8, 8, 64, 1, FourCc(Bc5U), 0x1000));
    }

    [Fact]
    public void UncompressedFormatShouldRecordTheRowPitch()
    {
        DdsHeader.Build(16, 2, 1, 28, isCubeMap: false).Should().Equal(
            Expected(0x2100F, 2, 16, 64, 1, [32, 0x41, 0, 32, 0xff, 0xff00, 0xff0000, 0xff000000], 0x1000));
    }

    [Fact]
    public void Bc7ShouldGetADx10ExtensionHeader()
    {
        DdsHeader.Build(16, 16, 1, 98, isCubeMap: false).Should().Equal(
            Expected(0xA1007, 16, 16, 256, 1, FourCc(Dx10), 0x1000, [98, 3, 0, 1, 0]));
    }

    // The old D3DFMT float codes still count as having no legacy pixel format, so these get the DX10 header.
    [Fact]
    public void FloatFormatShouldGetADx10ExtensionHeader()
    {
        DdsHeader.Build(4, 4, 1, 10, isCubeMap: false).Should().Equal(
            Expected(0x2100F, 4, 4, 32, 1, FourCc(Dx10), 0x1000, [10, 3, 0, 1, 0]));
    }

    [Fact]
    public void CubeMapShouldOnlyBeMarkedInTheDx10Header()
    {
        DdsHeader.Build(8, 8, 1, 71, isCubeMap: true).Should().Equal(
            Expected(0xA1007, 8, 8, 32, 1, FourCc(Dxt1), 0x1000));
        DdsHeader.Build(8, 8, 1, 98, isCubeMap: true).Should().Equal(
            Expected(0xA1007, 8, 8, 64, 1, FourCc(Dx10), 0x1000, [98, 3, 0x4, 1, 0]));
    }

    [Fact]
    public void FormatWithoutAKnownSizeShouldStillGetAHeaderWithZeroPitch()
    {
        DdsHeader.Build(8, 8, 1, 200, isCubeMap: false).Should().Equal(
            Expected(0x2100F, 8, 8, 0, 1, FourCc(Dx10), 0x1000, [200, 3, 0, 1, 0]));
    }

    // Row pitch of an 8 pixel wide texture: the video formats have their own rules and the formats only
    // DirectXTex sizes in its default branch still get a pitch.
    [Theory]
    [InlineData(103u, 8u)] // NV12
    [InlineData(106u, 8u)] // 420_OPAQUE
    [InlineData(104u, 16u)] // P010
    [InlineData(105u, 16u)] // P016
    [InlineData(108u, 32u)] // Y210
    [InlineData(109u, 32u)] // Y216
    [InlineData(110u, 8u)] // NV11
    [InlineData(102u, 64u)] // Y416
    [InlineData(100u, 32u)] // AYUV
    [InlineData(111u, 8u)] // AI44
    [InlineData(114u, 16u)] // A8P8
    public void VideoAndPaletteFormatsShouldGetTheirOwnRowPitch(uint format, uint rowPitch)
    {
        DdsHeader.Build(8, 8, 1, format, isCubeMap: false).Should().Equal(
            Expected(0x2100F, 8, 8, rowPitch, 1, FourCc(Dx10), 0x1000, [format, 3, 0, 1, 0]));
    }
}
