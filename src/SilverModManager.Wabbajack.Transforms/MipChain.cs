namespace SilverModManager.Wabbajack.Transforms;

/// <summary>One mip level as tightly packed straight-alpha RGBA bytes.</summary>
internal readonly record struct RgbaLevel(int Width, int Height, byte[] Pixels);

/// <summary>
/// Builds a mip chain with a box filter in C#. Skia is not used because its resampler may premultiply while it
/// filters, which destroys the colour under zero alpha; normal maps keep specular in alpha, so every level would
/// suffer. Alpha is averaged as stored in both modes.
/// </summary>
internal static class MipChain
{
    /// <summary>The number of levels in a chain that runs down to 1x1.</summary>
    public static int FullLength(int width, int height) => 32 - int.LeadingZeroCount(Math.Max(width, height));

    /// <summary>
    /// Yields <paramref name="level"/> itself, then each smaller level built from the one before it, one at a time so a
    /// caller that drops a level once it is encoded never holds the whole chain. Level n+1 is
    /// <c>max(1, w/2) x max(1, h/2)</c> and each of its pixels averages the source pixels it covers, so an odd size
    /// such as 3 -> 1 averages all three columns. <paramref name="levelCount"/> of 0 runs down to 1x1.
    /// </summary>
    /// <param name="linear">Average the colour channels in linear light, treating the bytes as sRGB.</param>
    public static IEnumerable<RgbaLevel> Build(RgbaLevel level, bool linear, int levelCount = 0)
    {
        var count = levelCount > 0 ? Math.Min(levelCount, FullLength(level.Width, level.Height)) : FullLength(level.Width, level.Height);
        yield return level;
        for (var i = 1; i < count; i++)
        {
            level = Halve(level, linear);
            yield return level;
        }
    }

    /// <summary>
    /// Grows a level to the next multiple of 4 in each direction by repeating its last column and row, which is
    /// what a block encoder needs. Run after filtering so the filter never averages replicated pixels.
    /// </summary>
    public static RgbaLevel Pad(RgbaLevel level)
    {
        var width = (level.Width + 3) & ~3;
        var height = (level.Height + 3) & ~3;
        if (width == level.Width && height == level.Height) return level;

        var padded = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var from = Math.Min(y, level.Height - 1) * level.Width * 4;
            level.Pixels.AsSpan(from, level.Width * 4).CopyTo(padded.AsSpan(y * width * 4));
            var last = level.Pixels.AsSpan(from + (level.Width - 1) * 4, 4);
            for (var x = level.Width; x < width; x++) last.CopyTo(padded.AsSpan((y * width + x) * 4));
        }
        return new RgbaLevel(width, height, padded);
    }

    private static RgbaLevel Halve(RgbaLevel source, bool linear)
    {
        var width = Math.Max(1, source.Width / 2);
        var height = Math.Max(1, source.Height / 2);
        var pixels = new byte[width * height * 4];
        var table = SrgbTransfer.LinearValues;
        Span<double> sums = stackalloc double[4];

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            int x0 = x * source.Width / width, x1 = (x + 1) * source.Width / width;
            int y0 = y * source.Height / height, y1 = (y + 1) * source.Height / height;
            sums.Clear();
            for (var sy = y0; sy < y1; sy++)
            for (var sx = x0; sx < x1; sx++)
            {
                var at = (sy * source.Width + sx) * 4;
                for (var c = 0; c < 3; c++) sums[c] += linear ? table[source.Pixels[at + c]] : source.Pixels[at + c];
                sums[3] += source.Pixels[at + 3];
            }

            var count = (x1 - x0) * (y1 - y0);
            var to = (y * width + x) * 4;
            for (var c = 0; c < 3; c++)
                pixels[to + c] = linear ? SrgbTransfer.EncodeLinear(sums[c] / count) : Round(sums[c], count);
            pixels[to + 3] = Round(sums[3], count);
        }
        return new RgbaLevel(width, height, pixels);
    }

    // Sums of whole bytes, so the quotient's only fraction is the exact .5 that rounds up.
    private static byte Round(double sum, int count) => (byte)(((long)sum * 2 + count) / (2L * count));
}
