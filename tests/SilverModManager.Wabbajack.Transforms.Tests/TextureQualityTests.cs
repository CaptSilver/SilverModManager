using SilverModManager.Wabbajack.Transforms;

namespace SilverModManager.Wabbajack.Transforms.Tests;

public class TextureQualityTests
{
    private static byte[] Solid(int size, byte r, byte g, byte b, byte a)
    {
        var pixels = new byte[size * size * 4];
        for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (r, g, b, a);
        return pixels;
    }

    private static byte[] Encode(BlockFormat format, byte[] rgba, int size)
    {
        var blocks = new byte[format.BlockBytes(size, size)];
        new ManagedTextureEncoder().Encode(rgba, size, size, format, blocks, 1, CancellationToken.None);
        return blocks;
    }

    [Theory]
    [InlineData(BlockFormat.Bc1)]
    [InlineData(BlockFormat.Bc3)]
    [InlineData(BlockFormat.Bc4)]
    [InlineData(BlockFormat.Bc5)]
    [InlineData(BlockFormat.Bc7)]
    public void AnExactRoundTripScoresTheCeiling(BlockFormat format)
    {
        var white = Solid(8, 255, 255, 255, 255);

        Assert.Equal(TextureQuality.Identical, TextureQuality.Psnr(white, Encode(format, white, 8), 8, 8, format));
    }

    [Fact]
    public void AnErrorOfTenInThreeChannelsIsTheTextbookFigure()
    {
        // 255 against 245 in R, G and B is a mean squared error of 100: 10 * log10(255^2 / 100).
        var psnr = TextureQuality.Psnr(Solid(8, 245, 245, 245, 255), Encode(BlockFormat.Bc1, Solid(8, 255, 255, 255, 255), 8), 8, 8, BlockFormat.Bc1);

        Assert.Equal(28.13, psnr, 2);
    }

    [Fact]
    public void BcOneIgnoresAlphaAndBcThreeDoesNot()
    {
        var reference = Solid(8, 255, 255, 255, 245);

        Assert.Equal(TextureQuality.Identical, TextureQuality.Psnr(reference, Encode(BlockFormat.Bc1, Solid(8, 255, 255, 255, 255), 8), 8, 8, BlockFormat.Bc1));
        Assert.True(TextureQuality.Psnr(reference, Encode(BlockFormat.Bc3, Solid(8, 255, 255, 255, 255), 8), 8, 8, BlockFormat.Bc3) < 40);
    }

    [Fact]
    public void BcFourComparesRedOnlyAndBcFiveRedAndGreen()
    {
        var blueOff = Solid(8, 255, 255, 0, 255);
        var greenOff = Solid(8, 255, 0, 255, 255);

        Assert.Equal(TextureQuality.Identical, TextureQuality.Psnr(blueOff, Encode(BlockFormat.Bc4, Solid(8, 255, 255, 255, 255), 8), 8, 8, BlockFormat.Bc4));
        Assert.Equal(TextureQuality.Identical, TextureQuality.Psnr(blueOff, Encode(BlockFormat.Bc5, Solid(8, 255, 255, 255, 255), 8), 8, 8, BlockFormat.Bc5));
        Assert.True(TextureQuality.Psnr(greenOff, Encode(BlockFormat.Bc5, Solid(8, 255, 255, 255, 255), 8), 8, 8, BlockFormat.Bc5) < 10);
        Assert.Equal(TextureQuality.Identical, TextureQuality.Psnr(greenOff, Encode(BlockFormat.Bc4, Solid(8, 255, 255, 255, 255), 8), 8, 8, BlockFormat.Bc4));
    }

    [Fact]
    public void AReferenceOfTheWrongLengthThrows()
        => Assert.Throws<ArgumentException>(() => TextureQuality.Psnr(new byte[8 * 8 * 4 - 4], new byte[BlockFormat.Bc1.BlockBytes(8, 8)], 8, 8, BlockFormat.Bc1));
}
