namespace SilverModManager.Wabbajack.Transforms.Tests;

public class MipChainTests
{
    private static RgbaLevel Noise(int width, int height, int seed)
    {
        var random = new Random(seed);
        var pixels = new byte[width * height * 4];
        random.NextBytes(pixels);
        return new RgbaLevel(width, height, pixels);
    }

    private static double Decode(int value)
    {
        var c = value / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static int Encode(double linear)
    {
        var c = linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
        return (int)Math.Clamp(Math.Round(c * 255), 0, 255);
    }

    /// <summary>The box filter written out the long way: one destination pixel at a time, from the source ranges.</summary>
    private static byte[] DirectBox(RgbaLevel source, int width, int height, bool linear)
    {
        var result = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var x0 = x * source.Width / width;
            var x1 = (x + 1) * source.Width / width;
            var y0 = y * source.Height / height;
            var y1 = (y + 1) * source.Height / height;
            var count = (x1 - x0) * (y1 - y0);
            for (var channel = 0; channel < 4; channel++)
            {
                double sum = 0;
                for (var sy = y0; sy < y1; sy++)
                for (var sx = x0; sx < x1; sx++)
                {
                    var value = source.Pixels[(sy * source.Width + sx) * 4 + channel];
                    sum += linear && channel < 3 ? Decode(value) : value;
                }
                var mean = sum / count;
                result[(y * width + x) * 4 + channel] = (byte)(linear && channel < 3 ? Encode(mean) : (int)Math.Floor(mean + 0.5));
            }
        }
        return result;
    }

    [Theory]
    [InlineData(8, 2, new[] { 8, 4, 2, 1 }, new[] { 2, 1, 1, 1 })]
    [InlineData(6, 10, new[] { 6, 3, 1, 1 }, new[] { 10, 5, 2, 1 })]
    [InlineData(1, 1, new[] { 1 }, new[] { 1 })]
    public void Build_FollowsFloorHalvingDownToOneByOne(int width, int height, int[] widths, int[] heights)
    {
        var chain = MipChain.Build(Noise(width, height, 1), linear: false).ToList();

        Assert.Equal(widths, chain.Select(l => l.Width));
        Assert.Equal(heights, chain.Select(l => l.Height));
        Assert.All(chain, l => Assert.Equal(l.Width * l.Height * 4, l.Pixels.Length));
    }

    [Fact]
    public void Build_StopsAtTheRequestedLevelCount()
    {
        Assert.Equal(3, MipChain.Build(Noise(64, 64, 1), linear: false, levelCount: 3).Count());
        Assert.Equal(7, MipChain.Build(Noise(64, 64, 1), linear: false).Count());
        Assert.Equal(7, MipChain.Build(Noise(64, 64, 1), linear: false, levelCount: 99).Count());
    }

    [Fact]
    public void Build_YieldsTheTopLevelFirstUntouched()
    {
        var top = Noise(4, 4, 2);
        Assert.Same(top.Pixels, MipChain.Build(top, linear: true).First().Pixels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_KeepsColourUnderZeroAlphaOnEveryLevel(bool linear)
    {
        var pixels = new byte[8 * 8 * 4];
        for (var i = 0; i < 64; i++)
        {
            pixels[i * 4] = 200;
            pixels[i * 4 + 1] = 100;
            pixels[i * 4 + 2] = 50;
        }

        var chain = MipChain.Build(new RgbaLevel(8, 8, pixels), linear).ToList();

        Assert.Equal(4, chain.Count);
        foreach (var level in chain)
            for (var i = 0; i < level.Width * level.Height; i++)
            {
                Assert.Equal(0, level.Pixels[i * 4 + 3]);
                Assert.Equal(200, level.Pixels[i * 4]);
                Assert.Equal(100, level.Pixels[i * 4 + 1]);
                Assert.Equal(50, level.Pixels[i * 4 + 2]);
            }
    }

    [Theory]
    [InlineData(false, 8, 8)]
    [InlineData(true, 8, 8)]
    [InlineData(false, 6, 10)]
    [InlineData(true, 6, 10)]
    [InlineData(false, 7, 5)]
    [InlineData(true, 7, 5)]
    public void Build_EachLevelIsTheBoxFilterOfThePreviousOne(bool linear, int width, int height)
    {
        var chain = MipChain.Build(Noise(width, height, 7), linear).ToList();

        for (var i = 1; i < chain.Count; i++)
        {
            var expected = DirectBox(chain[i - 1], chain[i].Width, chain[i].Height, linear);
            Assert.Equal(expected, chain[i].Pixels);
        }
    }

    [Fact]
    public void Build_TwoPixelSrgbSourceAveragesToTheLinearLightMidpoint()
    {
        byte[] pixels = [0, 0, 0, 255, 255, 255, 255, 255];

        var gamma = MipChain.Build(new RgbaLevel(2, 1, pixels), linear: false).Last();
        var linear = MipChain.Build(new RgbaLevel(2, 1, pixels), linear: true).Last();

        Assert.Equal([128, 128, 128, 255], gamma.Pixels);
        Assert.Equal([188, 188, 188, 255], linear.Pixels);
    }

    [Fact]
    public void Build_AveragesAlphaStraightEvenInLinearMode()
    {
        byte[] pixels = [10, 20, 30, 0, 10, 20, 30, 255];

        var level = MipChain.Build(new RgbaLevel(2, 1, pixels), linear: true).Last();

        Assert.Equal([10, 20, 30, 128], level.Pixels);
    }

    [Fact]
    public void Pad_ReplicatesTheLastColumnAndRow()
    {
        var level = Noise(3, 5, 3);

        var padded = MipChain.Pad(level);

        Assert.Equal((4, 8), (padded.Width, padded.Height));
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 4; x++)
        {
            var from = (Math.Min(y, 4) * 3 + Math.Min(x, 2)) * 4;
            Assert.Equal(level.Pixels.AsSpan(from, 4).ToArray(), padded.Pixels.AsSpan((y * 4 + x) * 4, 4).ToArray());
        }
    }

    [Fact]
    public void Pad_LeavesAMultipleOfFourAlone()
    {
        var level = Noise(8, 4, 4);
        Assert.Same(level.Pixels, MipChain.Pad(level).Pixels);
    }
}
