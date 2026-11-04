namespace SilverModManager.Wabbajack.Transforms;

/// <summary>The argument checks every <see cref="ITextureEncoder"/> makes before it touches a pixel.</summary>
internal static class EncodeArguments
{
    public static void Check(BlockFormat format, int rgbaLength, int paddedWidth, int paddedHeight, int dstLength, int parallelism)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(parallelism, 1);
        if (paddedWidth < 4 || paddedHeight < 4 || paddedWidth % 4 != 0 || paddedHeight % 4 != 0)
            throw new ArgumentException($"A level to encode is padded to whole blocks, not {paddedWidth}x{paddedHeight}");
        if (rgbaLength != paddedWidth * paddedHeight * 4)
            throw new ArgumentException($"A {paddedWidth}x{paddedHeight} level holds {paddedWidth * paddedHeight * 4} bytes of RGBA, not {rgbaLength}", "rgba");
        var expected = format.BlockBytes(paddedWidth, paddedHeight);
        if (dstLength != expected)
            throw new ArgumentException($"A {paddedWidth}x{paddedHeight} {format} level takes {expected} bytes, not {dstLength}", "dst");
    }
}
