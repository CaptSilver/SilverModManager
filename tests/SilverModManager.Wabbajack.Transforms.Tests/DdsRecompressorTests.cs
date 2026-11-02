using System.Diagnostics;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using DdsFile = BCnEncoder.Shared.ImageFiles.DdsFile;
using SilverModManager.Wabbajack.Transforms;

namespace SilverModManager.Wabbajack.Transforms.Tests;

public class DdsRecompressorTests(ITestOutputHelper output)
{
    private static (double R, double G, double B) Mean(ColorRgba32[] pixels)
    {
        double r = 0, g = 0, b = 0;
        foreach (var p in pixels) { r += p.r; g += p.g; b += p.b; }
        return (r / pixels.Length, g / pixels.Length, b / pixels.Length);
    }

    private static async Task<byte[]> Recompress(byte[] dds, DdsTarget target)
    {
        var result = new MemoryStream();
        await DdsRecompressor.Recompress(new MemoryStream(dds), target, result, CancellationToken.None);
        return result.ToArray();
    }

    private static byte[] Bc1Fixture()
    {
        var stream = new MemoryStream();
        TestTextures.WriteBc1(stream);
        return stream.ToArray();
    }

    [Fact]
    public async Task Recompress_ProducesRecordedSizeMipCountAndFormat()
    {
        var source = TestTextures.Gradient(64);
        var result = await Recompress(Bc1Fixture(), new DdsTarget(32, 32, 3, DxgiFormat.BC7_UNORM));

        var dds = DdsFile.Load(new MemoryStream(result));
        var face = dds.Faces.Single();
        Assert.Equal(32u, face.Width);
        Assert.Equal(32u, face.Height);
        Assert.Equal(3, face.MipMaps.Length);
        Assert.Equal(BCnEncoder.Shared.ImageFiles.DxgiFormat.DxgiFormatBc7Unorm, dds.dx10Header.dxgiFormat);

        var decoded = new BcDecoder().DecodeRaw(face.MipMaps[0].Data, 32, 32, CompressionFormat.Bc7);
        var (sr, sg, sb) = Mean(source);
        var (dr, dg, db) = Mean(decoded);
        Assert.True(Math.Abs(sr - dr) < 8.0);
        Assert.True(Math.Abs(sg - dg) < 8.0);
        Assert.True(Math.Abs(sb - db) < 8.0);
    }

    private static byte[] LegacySource(TestTextures.LegacyLayout layout, CompressionFormat encode, int size = 16, int mips = 1, bool cube = false)
    {
        var level = TestTextures.EncodedGradient(encode, size, mips);
        var data = cube ? [.. Enumerable.Repeat(level, 6).SelectMany(x => x)] : level;
        var stream = new MemoryStream();
        TestTextures.WriteLegacy(stream, layout, size, size, data, mips, cube);
        return stream.ToArray();
    }

    private static async Task<ColorRgba32[]> DecodedTopLevel(byte[] dds, DxgiFormat format)
    {
        var header = DdsFile.Load(new MemoryStream(dds));
        var face = header.Faces.First();
        return new BcDecoder().DecodeRaw(face.MipMaps[0].Data, (int)face.Width, (int)face.Height,
            format == DxgiFormat.R8G8B8A8_UNORM ? CompressionFormat.Rgba : CompressionFormat.Bc7);
    }

    public static TheoryData<string, CompressionFormat> LegacyColourSources => new()
    {
        { "DXT1", CompressionFormat.Bc1 },
        { "DXT3", CompressionFormat.Bc2 },
        { "DXT5", CompressionFormat.Bc3 },
    };

    [Theory]
    [MemberData(nameof(LegacyColourSources))]
    public async Task Recompress_LegacyColourBlockSources_KeepTheirColour(string fourCc, CompressionFormat encode)
    {
        var layout = fourCc switch { "DXT1" => TestTextures.LegacyLayout.Dxt1, "DXT3" => TestTextures.LegacyLayout.Dxt3, _ => TestTextures.LegacyLayout.Dxt5 };
        var result = await Recompress(LegacySource(layout, encode), new DdsTarget(16, 16, 1, DxgiFormat.R8G8B8A8_UNORM));

        var (r, g, b) = Mean(await DecodedTopLevel(result, DxgiFormat.R8G8B8A8_UNORM));
        var (sr, sg, sb) = Mean(TestTextures.Gradient(16));
        Assert.True(Math.Abs(r - sr) < 8.0 && Math.Abs(g - sg) < 8.0 && Math.Abs(b - sb) < 8.0, $"{r},{g},{b} vs {sr},{sg},{sb}");
    }

    [Fact]
    public async Task Recompress_Ati1Source_KeepsTheRedChannel()
    {
        var result = await Recompress(LegacySource(TestTextures.LegacyLayout.Ati1, CompressionFormat.Bc4), new DdsTarget(16, 16, 1, DxgiFormat.R8G8B8A8_UNORM));

        var (r, _, _) = Mean(await DecodedTopLevel(result, DxgiFormat.R8G8B8A8_UNORM));
        Assert.True(Math.Abs(r - Mean(TestTextures.Gradient(16)).R) < 8.0);
    }

    [Fact]
    public async Task Recompress_Ati2Source_KeepsRedAndGreen()
    {
        var result = await Recompress(LegacySource(TestTextures.LegacyLayout.Ati2, CompressionFormat.Bc5), new DdsTarget(16, 16, 1, DxgiFormat.R8G8B8A8_UNORM));

        var (r, g, _) = Mean(await DecodedTopLevel(result, DxgiFormat.R8G8B8A8_UNORM));
        var (sr, sg, _) = Mean(TestTextures.Gradient(16));
        Assert.True(Math.Abs(r - sr) < 8.0 && Math.Abs(g - sg) < 8.0);
    }

    public static TheoryData<string, CompressionFormat> OtherFourCcSources => new()
    {
        { "DXT2", CompressionFormat.Bc2 }, { "DXT4", CompressionFormat.Bc3 },
        { "A2D5", CompressionFormat.Bc3 }, { "xGBR", CompressionFormat.Bc3 }, { "RXGB", CompressionFormat.Bc3 },
        { "BRGX", CompressionFormat.Bc3 }, { "BC4U", CompressionFormat.Bc4 }, { "BC5U", CompressionFormat.Bc5 },
        { "A2XY", CompressionFormat.Bc5 },
    };

    [Theory]
    [MemberData(nameof(OtherFourCcSources))]
    public async Task Recompress_OtherFourCcSources_DecodeAsTheirBlockFormat(string fourCc, CompressionFormat encode)
    {
        var layout = TestTextures.LegacyLayout.Dxt1 with { FourCc = fourCc };
        var result = await Recompress(LegacySource(layout, encode), new DdsTarget(16, 16, 1, DxgiFormat.R8G8B8A8_UNORM));

        var (r, _, _) = Mean(await DecodedTopLevel(result, DxgiFormat.R8G8B8A8_UNORM));
        Assert.True(Math.Abs(r - Mean(TestTextures.Gradient(16)).R) < 8.0);
    }

    [Theory]
    [InlineData("BC7L")]
    [InlineData("BC7\0")]
    public async Task Recompress_Bc7FourCcSources_DecodeAsBc7(string fourCc)
    {
        var layout = TestTextures.LegacyLayout.Dxt1 with { FourCc = fourCc };
        var result = await Recompress(LegacySource(layout, CompressionFormat.Bc7), new DdsTarget(16, 16, 1, DxgiFormat.R8G8B8A8_UNORM));

        var (r, g, b) = Mean(await DecodedTopLevel(result, DxgiFormat.R8G8B8A8_UNORM));
        var (sr, sg, sb) = Mean(TestTextures.Gradient(16));
        Assert.True(Math.Abs(r - sr) < 4.0 && Math.Abs(g - sg) < 4.0 && Math.Abs(b - sb) < 4.0);
    }

    [Fact]
    public async Task Recompress_Dxt4Source_KeepsItsStoredAlphaAsIs()
    {
        // A flat BC3 block of colour (200, 100, 50) with alpha 80, stored the way DXT4 stores premultiplied data.
        var block = BlockSolidBc3(80);
        var stream = new MemoryStream();
        TestTextures.WriteLegacy(stream, TestTextures.LegacyLayout.Dxt1 with { FourCc = "DXT4" }, 4, 4, block);

        var result = await Recompress(stream.ToArray(), new DdsTarget(4, 4, 1, DxgiFormat.R8G8B8A8_UNORM));

        var decoded = new BcDecoder().DecodeRaw(DdsFile.Load(new MemoryStream(result)).Faces[0].MipMaps[0].Data, 4, 4, CompressionFormat.Rgba);
        Assert.All(decoded, p => Assert.Equal(80, p.a));
    }

    private static byte[] BlockSolidBc3(byte alpha)
    {
        var pixels = new ColorRgba32[16];
        Array.Fill(pixels, new ColorRgba32(200, 100, 50, alpha));
        var encoder = new BCnEncoder.Encoder.BcEncoder(CompressionFormat.Bc3);
        encoder.OutputOptions.GenerateMipMaps = false;
        return encoder.EncodeToRawBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()), 4, 4, BCnEncoder.Encoder.PixelFormat.Rgba32)[0];
    }

    [Fact]
    public async Task Recompress_P8Source_ExpandsThroughItsPalette()
    {
        var palette = new byte[1024];
        palette[0] = 10; palette[1] = 20; palette[2] = 30; palette[3] = 255;
        palette[4] = 200; palette[5] = 150; palette[6] = 100; palette[7] = 255;
        var stream = new MemoryStream();
        TestTextures.WriteLegacy(stream, new TestTextures.LegacyLayout(0x20, "", 8, 0, 0, 0, 0), 2, 2, [.. palette, 0, 1, 1, 0]);

        var result = await Recompress(stream.ToArray(), new DdsTarget(2, 2, 1, DxgiFormat.R8G8B8A8_UNORM));

        var decoded = new BcDecoder().DecodeRaw(DdsFile.Load(new MemoryStream(result)).Faces[0].MipMaps[0].Data, 2, 2, CompressionFormat.Rgba);
        var first = new ColorRgba32(10, 20, 30, 255);
        var second = new ColorRgba32(200, 150, 100, 255);
        Assert.Equal([first, second, second, first], decoded);
    }

    [Theory]
    [InlineData("BC5S", 2)]
    [InlineData("BC4S", 1)]
    public async Task Recompress_SignedSources_ReEncodeWithoutThrowing(string fourCc, int channels)
    {
        // Endpoints 0/0 and index 0 everywhere: every texel is signed zero, which maps to 128.
        var data = new byte[16 * 8 * channels];
        var stream = new MemoryStream();
        TestTextures.WriteLegacy(stream, TestTextures.LegacyLayout.Dxt1 with { FourCc = fourCc }, 16, 16, data);

        var result = await Recompress(stream.ToArray(), new DdsTarget(16, 16, 1, channels == 2 ? DxgiFormat.BC5_UNORM : DxgiFormat.BC4_UNORM));

        var face = DdsFile.Load(new MemoryStream(result)).Faces[0];
        var decoded = new BcDecoder().DecodeRaw(face.MipMaps[0].Data, 16, 16, channels == 2 ? CompressionFormat.Bc5 : CompressionFormat.Bc4);
        Assert.All(decoded, p => Assert.InRange(p.r, 126, 130));
        if (channels == 2) Assert.All(decoded, p => Assert.InRange(p.g, 126, 130));
    }

    [Fact]
    public async Task Recompress_Bc5Dx10SnormSource_ReEncodesWithoutThrowing()
    {
        var stream = new MemoryStream();
        TestTextures.WriteDx10(stream, DxgiFormat.BC5_SNORM, 8, 8, new byte[4 * 16]);

        var result = await Recompress(stream.ToArray(), new DdsTarget(8, 8, 1, DxgiFormat.BC5_UNORM));

        Assert.NotEmpty(result);
    }

    [Fact]
    public async Task Recompress_Bc7Dx10Source_KeepsItsColour()
    {
        var stream = new MemoryStream();
        TestTextures.WriteDx10(stream, DxgiFormat.BC7_UNORM, 16, 16, TestTextures.EncodedGradient(CompressionFormat.Bc7, 16, 1));

        var result = await Recompress(stream.ToArray(), new DdsTarget(16, 16, 1, DxgiFormat.R8G8B8A8_UNORM));

        var (r, g, b) = Mean(await DecodedTopLevel(result, DxgiFormat.R8G8B8A8_UNORM));
        var (sr, sg, sb) = Mean(TestTextures.Gradient(16));
        Assert.True(Math.Abs(r - sr) < 4.0 && Math.Abs(g - sg) < 4.0 && Math.Abs(b - sb) < 4.0);
    }

    [Fact]
    public async Task Recompress_TwoMipSource_UsesOnlyTheTopLevel()
    {
        var result = await Recompress(LegacySource(TestTextures.LegacyLayout.Dxt1, CompressionFormat.Bc1, mips: 2), new DdsTarget(16, 16, 1, DxgiFormat.R8G8B8A8_UNORM));

        var (r, g, b) = Mean(await DecodedTopLevel(result, DxgiFormat.R8G8B8A8_UNORM));
        var (sr, sg, sb) = Mean(TestTextures.Gradient(16));
        Assert.True(Math.Abs(r - sr) < 8.0 && Math.Abs(g - sg) < 8.0 && Math.Abs(b - sb) < 8.0);
    }

    [Fact]
    public async Task Recompress_LegacyCubeMapSource_ProducesSixFaces()
    {
        var result = await Recompress(LegacySource(TestTextures.LegacyLayout.Dxt1, CompressionFormat.Bc1, size: 16, cube: true), new DdsTarget(8, 8, 1, DxgiFormat.BC1_UNORM));

        var dds = DdsFile.Load(new MemoryStream(result));
        Assert.Equal(6, dds.Faces.Count);
        Assert.True(dds.Faces.All(f => f.Width == 8));
    }

    [Fact]
    public async Task Recompress_Uncompressed32BitLegacySource_IsDecodedAsRgba()
    {
        var pixels = TestTextures.Gradient(8);
        var stream = new MemoryStream();
        TestTextures.WriteLegacy(stream, TestTextures.LegacyLayout.A8B8G8R8, 8, 8, System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()));

        var result = await Recompress(stream.ToArray(), new DdsTarget(8, 8, 1, DxgiFormat.R8G8B8A8_UNORM));

        var decoded = await DecodedTopLevel(result, DxgiFormat.R8G8B8A8_UNORM);
        Assert.Equal(pixels, decoded);
    }

    [Fact]
    public async Task Recompress_ZeroMipLevels_GeneratesFullChain()
    {
        var result = await Recompress(Bc1Fixture(), new DdsTarget(64, 64, 0, DxgiFormat.BC3_UNORM));
        var dds = DdsFile.Load(new MemoryStream(result));
        Assert.Equal(7, dds.Faces.Single().MipMaps.Length);
    }

    [Fact]
    public async Task Recompress_SixFaces_ProducesCubeMap()
    {
        var cube = new MemoryStream();
        TestTextures.WriteBc1CubeMap(cube);

        var result = await Recompress(cube.ToArray(), new DdsTarget(32, 32, 1, DxgiFormat.BC1_UNORM));
        var dds = DdsFile.Load(new MemoryStream(result));
        Assert.Equal(6, dds.Faces.Count);
        Assert.True(dds.Faces.All(f => f.Width == 32));
    }

    [Theory]
    [InlineData("BC1_UNORM")]
    [InlineData("BC2_UNORM")]
    [InlineData("BC3_UNORM")]
    [InlineData("BC4_UNORM")]
    [InlineData("BC5_UNORM")]
    [InlineData("BC7_UNORM")]
    [InlineData("B8G8R8A8_UNORM")]
    [InlineData("R8G8B8A8_UNORM")]
    [InlineData("R8G8B8A8_UNORM_SRGB")]
    [InlineData("R8G8_UNORM")]
    [InlineData("R16_UNORM")]
    [InlineData("R8_UNORM")]
    [InlineData("A8_UNORM")]
    [InlineData("BC1_UNORM_SRGB")]
    [InlineData("BC2_UNORM_SRGB")]
    [InlineData("BC3_UNORM_SRGB")]
    [InlineData("B5G6R5_UNORM")]
    [InlineData("B5G5R5A1_UNORM")]
    [InlineData("B8G8R8X8_UNORM")]
    [InlineData("B8G8R8A8_UNORM_SRGB")]
    [InlineData("B8G8R8X8_UNORM_SRGB")]
    [InlineData("BC7_UNORM_SRGB")]
    [InlineData("B4G4R4A4_UNORM")]
    public void DxgiFormat_UpstreamNamesParse(string name)
    {
        Assert.Equal(name, Enum.Parse<DxgiFormat>(name).ToString());
    }

    [Theory]
    [InlineData("R8G8B8A8_UNORM_SRGB", 29)]
    [InlineData("R8G8_UNORM", 49)]
    [InlineData("R16_UNORM", 56)]
    [InlineData("R8_UNORM", 61)]
    [InlineData("A8_UNORM", 65)]
    [InlineData("BC1_UNORM_SRGB", 72)]
    [InlineData("BC2_UNORM_SRGB", 75)]
    [InlineData("BC3_UNORM_SRGB", 78)]
    [InlineData("B5G6R5_UNORM", 85)]
    [InlineData("B5G5R5A1_UNORM", 86)]
    [InlineData("B8G8R8X8_UNORM", 88)]
    [InlineData("B8G8R8A8_UNORM_SRGB", 91)]
    [InlineData("B8G8R8X8_UNORM_SRGB", 93)]
    [InlineData("BC7_UNORM_SRGB", 99)]
    [InlineData("B4G4R4A4_UNORM", 115)]
    public void DxgiFormat_ValuesMatchUpstream(string name, uint value)
    {
        Assert.Equal(value, (uint)Enum.Parse<DxgiFormat>(name));
    }

    [Fact]
    public async Task Recompress_UnsupportedTargetFormat_ThrowsNamingIt()
    {
        var ex = await Assert.ThrowsAsync<UnsupportedTextureFormatException>(() =>
            Recompress(Bc1Fixture(), new DdsTarget(32, 32, 1, DxgiFormat.BC6H_UF16)));
        Assert.Contains("BC6H_UF16", ex.Message);
    }

    [Fact]
    public async Task Recompress_UnsupportedSourceFormat_ThrowsNamingIt()
    {
        var source = new MemoryStream();
        TestTextures.WriteDds(source, DxgiFormat.BC6H_UF16);
        var ex = await Assert.ThrowsAsync<UnsupportedTextureFormatException>(() =>
            Recompress(source.ToArray(), new DdsTarget(32, 32, 1, DxgiFormat.BC1_UNORM)));
        Assert.Contains("BC6H_UF16", ex.Message);
    }

    [Fact]
    public async Task Recompress_UnsupportedSourceFormat_WritesNothingToTheOutput()
    {
        var source = new MemoryStream();
        TestTextures.WriteDds(source, DxgiFormat.BC6H_UF16);
        var output = new CountingStream();

        await Assert.ThrowsAsync<UnsupportedTextureFormatException>(() =>
            DdsRecompressor.Recompress(new MemoryStream(source.ToArray()), new DdsTarget(32, 32, 1, DxgiFormat.BC1_UNORM), output, CancellationToken.None));

        Assert.Equal(0, output.BytesWritten);
    }

    [Fact]
    public async Task Recompress_UnsupportedTargetFormat_WritesNothingToTheOutput()
    {
        var output = new CountingStream();

        await Assert.ThrowsAsync<UnsupportedTextureFormatException>(() =>
            DdsRecompressor.Recompress(new MemoryStream(Bc1Fixture()), new DdsTarget(32, 32, 1, DxgiFormat.BC6H_UF16), output, CancellationToken.None));

        Assert.Equal(0, output.BytesWritten);
    }

    /// <summary>A write-only sink that counts what reaches it, so a test can tell "wrote nothing" from "wrote and was rewound".</summary>
    private sealed class CountingStream : Stream
    {
        public long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => BytesWritten += count;
        public override void Write(ReadOnlySpan<byte> buffer) => BytesWritten += buffer.Length;
    }

    [Fact]
    public async Task Recompress_CancelledToken_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DdsRecompressor.Recompress(new MemoryStream(Bc1Fixture()), new DdsTarget(32, 32, 1, DxgiFormat.BC1_UNORM),
                new MemoryStream(), cts.Token));
    }

    [Fact]
    public async Task Recompress_Bc7At1024_LogsEncodeTime()
    {
        var big = new MemoryStream();
        TestTextures.WriteBc1(big, 1024);
        var watch = Stopwatch.StartNew();
        await Recompress(big.ToArray(), new DdsTarget(1024, 1024, 0, DxgiFormat.BC7_UNORM));
        output.WriteLine($"BC7 1024x1024 recompress: {watch.ElapsedMilliseconds} ms");
    }

    private static byte[] Dx10Source(DxgiFormat format, int width, int height, byte[] data)
    {
        var stream = new MemoryStream();
        TestTextures.WriteDx10(stream, format, width, height, data);
        return stream.ToArray();
    }

    /// <summary>
    /// Re-encodes <paramref name="dds"/> to R8G8B8A8 at its own size and returns the top level's pixels as r,g,b,a bytes.
    /// An sRGB source goes to the sRGB twin so no transfer runs and the stored values come back unchanged.
    /// </summary>
    private static async Task<byte[]> RgbaOf(byte[] dds, int width, int height)
    {
        var format = DdsHeader.Read(dds).IsSrgb ? DxgiFormat.R8G8B8A8_UNORM_SRGB : DxgiFormat.R8G8B8A8_UNORM;
        var result = await Recompress(dds, new DdsTarget(width, height, 1, format));
        var header = DdsHeader.Read(result);
        return result.AsSpan(header.DataOffset, (int)header.LevelSize(0)).ToArray();
    }

    public static TheoryData<DxgiFormat, byte[], byte[]> Dx10UncompressedSources => new()
    {
        { DxgiFormat.R8_UNORM, [10, 200], [10, 0, 0, 255, 200, 0, 0, 255] },
        { DxgiFormat.R8G8_UNORM, [1, 2, 3, 4], [1, 2, 0, 255, 3, 4, 0, 255] },
        { DxgiFormat.B8G8R8X8_UNORM, [1, 2, 3, 77, 4, 5, 6, 88], [3, 2, 1, 255, 6, 5, 4, 255] },
        { DxgiFormat.R8G8B8A8_UNORM_SRGB, [1, 2, 3, 4, 5, 6, 7, 8], [1, 2, 3, 4, 5, 6, 7, 8] },
        { DxgiFormat.B8G8R8A8_UNORM_SRGB, [1, 2, 3, 4, 5, 6, 7, 8], [3, 2, 1, 4, 7, 6, 5, 8] },
        { DxgiFormat.B8G8R8X8_UNORM_SRGB, [1, 2, 3, 77, 4, 5, 6, 88], [3, 2, 1, 255, 6, 5, 4, 255] },
    };

    [Theory]
    [MemberData(nameof(Dx10UncompressedSources))]
    public async Task Recompress_Dx10UncompressedSource_DecodesToExactPixels(DxgiFormat format, byte[] data, byte[] expected)
    {
        var width = 2;
        var rows = data.Length / (format is DxgiFormat.R8_UNORM ? 1 : format is DxgiFormat.R8G8_UNORM ? 2 : 4) / width;

        Assert.Equal(expected, await RgbaOf(Dx10Source(format, width, rows, data), width, rows));
    }

    private static byte[] FlatSource(DxgiFormat format)
    {
        var pixels = new byte[8 * 8 * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = pixels[i + 1] = pixels[i + 2] = 128; pixels[i + 3] = 200; }
        return Dx10Source(format, 8, 8, pixels);
    }

    public static TheoryData<DxgiFormat, DxgiFormat, int> SrgbMatrix
    {
        get
        {
            var data = new TheoryData<DxgiFormat, DxgiFormat, int>();
            foreach (var (unorm, srgb) in new[]
                     {
                         (DxgiFormat.BC7_UNORM, DxgiFormat.BC7_UNORM_SRGB),
                         (DxgiFormat.R8G8B8A8_UNORM, DxgiFormat.R8G8B8A8_UNORM_SRGB),
                     })
            {
                data.Add(DxgiFormat.R8G8B8A8_UNORM, unorm, 128);
                data.Add(DxgiFormat.R8G8B8A8_UNORM_SRGB, srgb, 128);
                data.Add(DxgiFormat.R8G8B8A8_UNORM, srgb, 188);
                data.Add(DxgiFormat.R8G8B8A8_UNORM_SRGB, unorm, 55);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(SrgbMatrix))]
    public async Task Recompress_SrgbTransfer_RunsOnlyWhenSourceAndTargetDiffer(DxgiFormat source, DxgiFormat target, int expected)
    {
        var result = await Recompress(FlatSource(source), new DdsTarget(8, 8, 1, target));

        var header = DdsHeader.Read(result);
        var data = result.AsSpan(header.DataOffset, (int)header.LevelSize(0)).ToArray();
        var pixels = header.IsBlockCompressed
            ? new BcDecoder().DecodeRaw(data, 8, 8, CompressionFormat.Bc7)
            : header.Reader!.Unpack(data, 8, 8);
        Assert.All(pixels, p =>
        {
            Assert.InRange(p.r, expected - 1, expected + 1);
            Assert.InRange(p.g, expected - 1, expected + 1);
            Assert.InRange(p.b, expected - 1, expected + 1);
            Assert.Equal(200, p.a);
        });
        Assert.Equal(target, header.IsSrgb ? SrgbOf(header.Format) : header.Format);
        // Every _SRGB value is recorded in a DX10 extension; the UNORM RGBA target keeps its legacy header.
        if (header.IsSrgb || target == DxgiFormat.BC7_UNORM)
            Assert.Equal((uint)target, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(128)));
        else
            Assert.NotEqual(0x30315844u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(84)));
    }

    private static DxgiFormat SrgbOf(DxgiFormat unorm) => unorm switch
    {
        DxgiFormat.BC7_UNORM => DxgiFormat.BC7_UNORM_SRGB,
        DxgiFormat.R8G8B8A8_UNORM => DxgiFormat.R8G8B8A8_UNORM_SRGB,
        _ => throw new ArgumentOutOfRangeException(nameof(unorm)),
    };

    [Fact]
    public void SrgbTransfer_TablesRoundTripTheEndsAndTheMiddle()
    {
        Assert.Equal(0, SrgbTransfer.ToSrgb[0]);
        Assert.Equal(255, SrgbTransfer.ToSrgb[255]);
        Assert.Equal(188, SrgbTransfer.ToSrgb[128]);
        Assert.InRange(SrgbTransfer.ToLinear[128], 54, 56);
    }

    [Fact]
    public async Task Recompress_R8Source_PutsTheValueInRedAndNothingElse()
    {
        var rgba = await RgbaOf(Dx10Source(DxgiFormat.R8_UNORM, 1, 1, [77]), 1, 1);

        Assert.Equal(new byte[] { 77, 0, 0, 255 }, rgba);
    }

    [Fact]
    public async Task Recompress_R8G8Source_PutsTheTwoValuesInRedAndGreen()
    {
        var rgba = await RgbaOf(Dx10Source(DxgiFormat.R8G8_UNORM, 1, 1, [77, 99]), 1, 1);

        Assert.Equal(new byte[] { 77, 99, 0, 255 }, rgba);
    }

    public static TheoryData<DxgiFormat, DxgiFormat, CompressionFormat> SrgbBlockSources => new()
    {
        { DxgiFormat.BC1_UNORM_SRGB, DxgiFormat.BC1_UNORM, CompressionFormat.Bc1 },
        { DxgiFormat.BC2_UNORM_SRGB, DxgiFormat.BC2_UNORM, CompressionFormat.Bc2 },
        { DxgiFormat.BC3_UNORM_SRGB, DxgiFormat.BC3_UNORM, CompressionFormat.Bc3 },
        { DxgiFormat.BC7_UNORM_SRGB, DxgiFormat.BC7_UNORM, CompressionFormat.Bc7 },
    };

    [Theory]
    [MemberData(nameof(SrgbBlockSources))]
    public async Task Recompress_SrgbBlockSource_DecodesLikeItsUnormTwin(DxgiFormat srgb, DxgiFormat unorm, CompressionFormat encode)
    {
        var blocks = TestTextures.EncodedGradient(encode, 8, 1);

        var fromSrgb = await RgbaOf(Dx10Source(srgb, 8, 8, blocks), 8, 8);
        var fromUnorm = await RgbaOf(Dx10Source(unorm, 8, 8, blocks), 8, 8);

        Assert.Equal(fromUnorm, fromSrgb);
        var (r, g, _) = Mean(DecodedPixels(fromSrgb));
        var (sr, sg, _) = Mean(TestTextures.Gradient(8));
        Assert.True(Math.Abs(r - sr) < 12.0 && Math.Abs(g - sg) < 12.0, $"{r},{g} vs {sr},{sg}");
    }

    private static ColorRgba32[] DecodedPixels(byte[] rgba)
        => System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ColorRgba32>(rgba.AsSpan()).ToArray();

    private const uint Rgb = 0x40, Lum = 0x20000, Alp = 0x2;

    // name, pixel flags, bits, masks r/g/b/a, the pixel's stored value, expected r/g/b/a
    public static TheoryData<string, uint, uint, uint, uint, uint, uint, uint, byte[]> BitmaskSources => new()
    {
        { "R8G8B8", Rgb, 24, 0xFF0000, 0xFF00, 0xFF, 0, 0x302010, [0x30, 0x20, 0x10, 255] },
        { "R5G6B5", Rgb, 16, 0xF800, 0x7E0, 0x1F, 0, 0x8410, [132, 130, 132, 255] },
        { "R5G6B5 full", Rgb, 16, 0xF800, 0x7E0, 0x1F, 0, 0xFFE0, [255, 255, 0, 255] },
        { "A1R5G5B5", Rgb | 1, 16, 0x7C00, 0x3E0, 0x1F, 0x8000, 0xFC00, [255, 0, 0, 255] },
        { "A1R5G5B5 clear", Rgb | 1, 16, 0x7C00, 0x3E0, 0x1F, 0x8000, 0x7C00, [255, 0, 0, 0] },
        { "X1R5G5B5", Rgb, 16, 0x7C00, 0x3E0, 0x1F, 0, 0x7C00, [255, 0, 0, 255] },
        { "A4R4G4B4", Rgb | 1, 16, 0xF00, 0xF0, 0xF, 0xF000, 0x8F4A, [255, 68, 170, 136] },
        { "X4R4G4B4", Rgb, 16, 0xF00, 0xF0, 0xF, 0, 0x0F4A, [255, 68, 170, 255] },
        { "A4L4", Lum | 1, 8, 0xF, 0, 0, 0xF0, 0xA5, [85, 85, 85, 170] },
        { "L8", Lum, 8, 0xFF, 0, 0, 0, 200, [200, 0, 0, 255] },
        { "A8L8", Lum | 1, 16, 0xFF, 0, 0, 0xFF00, 0x140A, [10, 20, 0, 255] },
        { "L16", Lum, 16, 0xFFFF, 0, 0, 0, 0x1234, [0x12, 0, 0, 255] },
        { "A8", Alp, 8, 0, 0, 0, 0xFF, 77, [0, 0, 0, 77] },
        { "A8R3G3B2", Rgb | 1, 16, 0xE0, 0x1C, 0x3, 0xFF00, 0x80E0, [255, 0, 0, 128] },
        { "R3G3B2", Rgb, 8, 0xE0, 0x1C, 0x3, 0, 0xE3, [255, 0, 255, 255] },
        { "X8R8G8B8", Rgb, 32, 0xFF0000, 0xFF00, 0xFF, 0, 0x04030201, [3, 2, 1, 255] },
        { "A8R8G8B8", Rgb | 1, 32, 0xFF0000, 0xFF00, 0xFF, 0xFF000000, 0x04030201, [3, 2, 1, 4] },
        { "A8B8G8R8", Rgb | 1, 32, 0xFF, 0xFF00, 0xFF0000, 0xFF000000, 0x04030201, [1, 2, 3, 4] },
        { "X8B8G8R8", Rgb, 32, 0xFF, 0xFF00, 0xFF0000, 0, 0x04030201, [1, 2, 3, 255] },
        { "G16R16", Rgb, 32, 0xFFFF, 0xFFFF0000, 0, 0, 0x56781234, [0x12, 0x56, 0, 255] },
        { "A2R10G10B10", Rgb | 1, 32, 0x3FF00000, 0xFFC00, 0x3FF, 0xC0000000, (512u << 20) | (256u << 10) | 1023 | (1u << 30), [128, 64, 255, 85] },
        { "A2B10G10R10", Rgb | 1, 32, 0x3FF, 0xFFC00, 0x3FF00000, 0xC0000000, 512 | (256u << 10) | (1023u << 20) | (1u << 30), [128, 64, 255, 85] },
    };

    [Theory]
    [MemberData(nameof(BitmaskSources))]
    public async Task Recompress_BitmaskSource_DecodesToExactPixels(string name, uint flags, uint bits, uint r, uint g, uint b, uint a, uint value, byte[] expected)
    {
        var bytes = new byte[bits / 8];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(value >> (8 * i));
        var stream = new MemoryStream();
        TestTextures.WriteLegacy(stream, new TestTextures.LegacyLayout(flags, "", bits, r, g, b, a), 1, 1, bytes);

        var actual = await RgbaOf(stream.ToArray(), 1, 1);

        Assert.True(Enumerable.SequenceEqual(expected, actual), $"{name}: {string.Join(',', actual)}");
    }
}
