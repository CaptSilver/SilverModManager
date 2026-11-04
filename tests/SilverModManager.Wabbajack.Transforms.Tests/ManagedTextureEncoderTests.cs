using SilverModManager.Wabbajack.Transforms;

namespace SilverModManager.Wabbajack.Transforms.Tests;

public class ManagedTextureEncoderTests
{
    private static readonly ManagedTextureEncoder Encoder = new();

    private static byte[] Level(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 37 + i / 5);
        return pixels;
    }

    public static TheoryData<BlockFormat> Formats => new(Enum.GetValues<BlockFormat>());

    [Fact]
    public void NamesItselfAndTakesEveryBlockFormat()
    {
        Assert.Equal("BCnEncoder.Net", Encoder.Name);
        Assert.All(Enum.GetValues<BlockFormat>(), f => Assert.True(Encoder.CanEncode(f), f.ToString()));
    }

    [Theory]
    [InlineData(BlockFormat.Bc1, 8)]
    [InlineData(BlockFormat.Bc1PunchThrough, 8)]
    [InlineData(BlockFormat.Bc4, 8)]
    [InlineData(BlockFormat.Bc2, 16)]
    [InlineData(BlockFormat.Bc3, 16)]
    [InlineData(BlockFormat.Bc5, 16)]
    [InlineData(BlockFormat.Bc7, 16)]
    public void BlockBytesAreTheBytesOfOneBlockTimesTheBlockCount(BlockFormat format, int perBlock)
        => Assert.Equal(perBlock * 2 * 3, format.BlockBytes(8, 12));

    [Theory]
    [MemberData(nameof(Formats))]
    public void FillsDstWithTheBlocksOfALevel(BlockFormat format)
    {
        var dst = new byte[format.BlockBytes(8, 12)];
        Array.Fill(dst, (byte)0xAA);

        Encoder.Encode(Level(8, 12), 8, 12, format, dst, 1, CancellationToken.None);

        Assert.NotEqual(new byte[dst.Length], dst);
        Assert.NotEqual(Enumerable.Repeat((byte)0xAA, dst.Length).ToArray(), dst);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void ThreadsDoNotChangeTheBlocks(BlockFormat format)
    {
        var one = new byte[format.BlockBytes(32, 32)];
        var many = new byte[one.Length];

        Encoder.Encode(Level(32, 32), 32, 32, format, one, 1, CancellationToken.None);
        Encoder.Encode(Level(32, 32), 32, 32, format, many, 4, CancellationToken.None);

        Assert.Equal(one, many);
    }

    [Fact]
    public void ARgbaBufferOfTheWrongLengthThrows()
    {
        var dst = new byte[BlockFormat.Bc7.BlockBytes(8, 8)];

        Assert.Throws<ArgumentException>(() => Encoder.Encode(new byte[8 * 8 * 4 - 1], 8, 8, BlockFormat.Bc7, dst, 1, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => Encoder.Encode(new byte[8 * 8 * 4 + 4], 8, 8, BlockFormat.Bc7, dst, 1, CancellationToken.None));
    }

    [Fact]
    public void ADstOfTheWrongLengthThrows()
    {
        var rgba = Level(8, 8);

        Assert.Throws<ArgumentException>(() => Encoder.Encode(rgba, 8, 8, BlockFormat.Bc7, new byte[BlockFormat.Bc7.BlockBytes(8, 8) - 1], 1, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => Encoder.Encode(rgba, 8, 8, BlockFormat.Bc1, new byte[BlockFormat.Bc7.BlockBytes(8, 8)], 1, CancellationToken.None));
    }

    [Theory]
    [InlineData(6, 8)]
    [InlineData(8, 5)]
    [InlineData(0, 8)]
    public void ALevelThatIsNotWholeBlocksThrows(int width, int height)
        => Assert.Throws<ArgumentException>(() => Encoder.Encode(new byte[width * height * 4], width, height, BlockFormat.Bc1, new byte[width / 4 * (height / 4) * 8], 1, CancellationToken.None));

    [Fact]
    public void AnEncodeCancelledBeforeItStartsThrows()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            Encoder.Encode(Level(8, 8), 8, 8, BlockFormat.Bc7, new byte[BlockFormat.Bc7.BlockBytes(8, 8)], 1, cts.Token));
    }

    [Fact]
    public void ParallelismBelowOneThrows()
        => Assert.Throws<ArgumentOutOfRangeException>(() => Encoder.Encode(Level(8, 8), 8, 8, BlockFormat.Bc1, new byte[32], 0, CancellationToken.None));
}
