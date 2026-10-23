using System.Diagnostics;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using DdsFile = BCnEncoder.Shared.ImageFiles.DdsFile;
using NexusMods.Wabbajack.Transforms;

namespace NexusMods.Wabbajack.Transforms.Tests;

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
    public void DxgiFormat_UpstreamNamesParse(string name)
    {
        Assert.Equal(name, Enum.Parse<DxgiFormat>(name).ToString());
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
