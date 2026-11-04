using SilverModManager.Wabbajack.Transforms;

namespace SilverModManager.Wabbajack.Transforms.Tests;

public class ManagedTextureEncoderTests
{
    private static readonly ManagedTextureEncoder Encoder = new();

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
}
