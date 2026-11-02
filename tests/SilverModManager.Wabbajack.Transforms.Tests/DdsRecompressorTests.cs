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
}
