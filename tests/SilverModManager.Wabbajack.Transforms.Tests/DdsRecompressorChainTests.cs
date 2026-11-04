using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using SilverModManager.Wabbajack.Transforms;

namespace SilverModManager.Wabbajack.Transforms.Tests;

/// <summary>Which encoder, block format and thread count the recompressor asks for.</summary>
public class DdsRecompressorChainTests
{
    private sealed class RecordingEncoder(string name, params BlockFormat[] takes) : ITextureEncoder
    {
        private readonly ManagedTextureEncoder _inner = new();
        public List<(BlockFormat Format, int Width, int Height, int Parallelism)> Calls { get; } = [];
        public string Name => name;
        public bool CanEncode(BlockFormat format) => takes.Contains(format);

        public void Encode(ReadOnlySpan<byte> rgba, int paddedWidth, int paddedHeight, BlockFormat format, Span<byte> dst, int parallelism, CancellationToken cancellationToken)
        {
            Calls.Add((format, paddedWidth, paddedHeight, parallelism));
            _inner.Encode(rgba, paddedWidth, paddedHeight, format, dst, parallelism, cancellationToken);
        }
    }

    /// <summary>An R8G8B8A8 texture whose left half is clear (alpha 0) and whose right half is opaque, when <paramref name="clearAlpha"/> is the clear alpha.</summary>
    private static byte[] RgbaSource(int size, byte clearAlpha, byte solidAlpha = 255)
    {
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var at = (y * size + x) * 4;
            pixels[at] = 200; pixels[at + 1] = 100; pixels[at + 2] = 50;
            pixels[at + 3] = x < size / 2 ? clearAlpha : solidAlpha;
        }
        var stream = new MemoryStream();
        TestTextures.WriteDx10(stream, DxgiFormat.R8G8B8A8_UNORM, size, size, pixels);
        return stream.ToArray();
    }

    private static async Task<byte[]> Recompress(DdsRecompressor recompressor, byte[] source, DdsTarget target, int parallelism = 1)
    {
        var output = new MemoryStream();
        await recompressor.Recompress(new MemoryStream(source), target, output, parallelism, CancellationToken.None);
        return output.ToArray();
    }

    [Fact]
    public void AnEmptyChainThrows()
        => Assert.Throws<ArgumentException>(() => new DdsRecompressor([]));

    [Fact]
    public async Task TheFirstEncoderThatCanEncodeAFormatIsUsedForIt()
    {
        var first = new RecordingEncoder("first", BlockFormat.Bc7);
        var second = new RecordingEncoder("second", BlockFormat.Bc7, BlockFormat.Bc3);
        var recompressor = new DdsRecompressor([first, second]);

        await Recompress(recompressor, RgbaSource(16, 255), new DdsTarget(16, 16, 1, DxgiFormat.BC7_UNORM));
        await Recompress(recompressor, RgbaSource(16, 255), new DdsTarget(16, 16, 1, DxgiFormat.BC3_UNORM));

        Assert.Equal([BlockFormat.Bc7], first.Calls.Select(c => c.Format));
        Assert.Equal([BlockFormat.Bc3], second.Calls.Select(c => c.Format));
    }

    [Fact]
    public async Task EveryLevelOfEveryFaceIsEncodedPaddedToWholeBlocksOnTheGivenThreads()
    {
        var encoder = new RecordingEncoder("only", BlockFormat.Bc1);

        await Recompress(new DdsRecompressor([encoder]), RgbaSource(16, 255), new DdsTarget(6, 10, 0, DxgiFormat.BC1_UNORM), parallelism: 5);

        // 6x10 -> 3x5 -> 1x2 -> 1x1, each padded up to a multiple of 4.
        Assert.Equal(
            [(BlockFormat.Bc1, 8, 12, 5), (BlockFormat.Bc1, 4, 8, 5), (BlockFormat.Bc1, 4, 4, 5), (BlockFormat.Bc1, 4, 4, 5)],
            encoder.Calls);
    }

    [Fact]
    public async Task ATargetNoEncoderCanWriteThrowsBeforeDecodingAndWritesNothing()
    {
        var recompressor = new DdsRecompressor([new RecordingEncoder("bc7 only", BlockFormat.Bc7)]);
        var output = new MemoryStream();

        var failure = await Record.ExceptionAsync(() => recompressor.Recompress(new MemoryStream(RgbaSource(16, 255)),
            new DdsTarget(16, 16, 1, DxgiFormat.BC3_UNORM), output, 1, CancellationToken.None));

        Assert.Contains("BC3", Assert.IsType<UnsupportedTextureFormatException>(failure).Message);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task ParallelismBelowOneThrows()
    {
        var recompressor = DdsRecompressor.Managed();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => recompressor.Recompress(new MemoryStream(), new DdsTarget(4, 4, 1, DxgiFormat.BC1_UNORM), Stream.Null, 0, CancellationToken.None));
    }

    private static ColorRgba32[] DecodeBc1WithAlpha(byte[] dds, int size)
    {
        var header = DdsHeader.Read(dds);
        return new BcDecoder().DecodeRaw(dds.AsMemory(header.DataOffset, (int)header.LevelSize(0)).ToArray(), size, size, CompressionFormat.Bc1WithAlpha);
    }

    [Theory]
    [InlineData(DxgiFormat.BC1_UNORM)]
    [InlineData(DxgiFormat.BC1_UNORM_SRGB)]
    public async Task ABc1TextureWhoseAlphaIsOnlyZeroOr255KeepsBoth(DxgiFormat format)
    {
        var encoder = new RecordingEncoder("managed", Enum.GetValues<BlockFormat>());

        var result = await Recompress(new DdsRecompressor([encoder]), RgbaSource(16, 0), new DdsTarget(16, 16, 1, format));

        Assert.Equal([BlockFormat.Bc1PunchThrough], encoder.Calls.Select(c => c.Format));
        var pixels = DecodeBc1WithAlpha(result, 16);
        for (var y = 0; y < 16; y++)
        for (var x = 0; x < 16; x++)
            Assert.True(pixels[y * 16 + x].a == (x < 8 ? 0 : 255), $"pixel {x},{y} has alpha {pixels[y * 16 + x].a}");
    }

    [Fact]
    public async Task ABc1TextureWithAlphaBelow128AnywhereIsAPunchThroughTexture()
    {
        var encoder = new RecordingEncoder("managed", Enum.GetValues<BlockFormat>());

        await Recompress(new DdsRecompressor([encoder]), RgbaSource(16, 127), new DdsTarget(16, 16, 2, DxgiFormat.BC1_UNORM));

        Assert.Equal([BlockFormat.Bc1PunchThrough, BlockFormat.Bc1PunchThrough], encoder.Calls.Select(c => c.Format));
    }

    [Fact]
    public async Task ABc1TextureWhoseAlphaIsAtLeast128EverywhereStaysOpaqueBc1()
    {
        var encoder = new RecordingEncoder("managed", Enum.GetValues<BlockFormat>());

        await Recompress(new DdsRecompressor([encoder]), RgbaSource(16, 128), new DdsTarget(16, 16, 1, DxgiFormat.BC1_UNORM));

        Assert.Equal([BlockFormat.Bc1], encoder.Calls.Select(c => c.Format));
    }

    [Theory]
    [InlineData(DxgiFormat.BC3_UNORM, BlockFormat.Bc3)]
    [InlineData(DxgiFormat.BC7_UNORM, BlockFormat.Bc7)]
    public async Task OnlyBc1LooksForCutOutAlpha(DxgiFormat format, BlockFormat expected)
    {
        var encoder = new RecordingEncoder("managed", Enum.GetValues<BlockFormat>());

        await Recompress(new DdsRecompressor([encoder]), RgbaSource(16, 0), new DdsTarget(16, 16, 1, format));

        Assert.Equal([expected], encoder.Calls.Select(c => c.Format));
    }

    [Fact]
    public async Task ACubemapIsOnePunchThroughTextureEvenIfOnlyOneFaceHasCutOuts()
    {
        var clear = RgbaSource(16, 0);
        var opaque = RgbaSource(16, 255);
        // Faces are stored back to back after the header; build a cube from one clear and five opaque faces.
        var faceBytes = 16 * 16 * 4;
        var pixels = new byte[6 * faceBytes];
        for (var face = 0; face < 6; face++)
            (face == 3 ? clear : opaque).AsSpan(DdsHeader.Read(opaque).DataOffset, faceBytes).CopyTo(pixels.AsSpan(face * faceBytes));
        var cube = new MemoryStream();
        TestTextures.WriteDx10(cube, DxgiFormat.R8G8B8A8_UNORM, 16, 16, pixels, 1, cubeMap: true);
        var encoder = new RecordingEncoder("managed", Enum.GetValues<BlockFormat>());

        await Recompress(new DdsRecompressor([encoder]), cube.ToArray(), new DdsTarget(16, 16, 1, DxgiFormat.BC1_UNORM));

        Assert.Equal(Enumerable.Repeat(BlockFormat.Bc1PunchThrough, 6), encoder.Calls.Select(c => c.Format));
    }
}
