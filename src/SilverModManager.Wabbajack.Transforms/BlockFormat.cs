namespace SilverModManager.Wabbajack.Transforms;

/// <summary>
/// What one <see cref="ITextureEncoder.Encode"/> call produces. <see cref="Bc1PunchThrough"/> is BC1 with 1-bit alpha; the
/// recompressor picks it from the pixels. sRGB is not here: the same blocks serve BC*_UNORM and BC*_UNORM_SRGB, only the
/// DDS header differs.
/// </summary>
public enum BlockFormat { Bc1, Bc1PunchThrough, Bc2, Bc3, Bc4, Bc5, Bc7 }

public static class BlockFormats
{
    /// <summary>Bytes of one 4x4 block.</summary>
    public static int BlockBytes(this BlockFormat format) => format is BlockFormat.Bc1 or BlockFormat.Bc1PunchThrough or BlockFormat.Bc4 ? 8 : 16;

    /// <summary>Bytes of a level of <paramref name="paddedWidth"/> x <paramref name="paddedHeight"/> pixels, both multiples of 4.</summary>
    public static int BlockBytes(this BlockFormat format, int paddedWidth, int paddedHeight)
        => paddedWidth / 4 * (paddedHeight / 4) * format.BlockBytes();
}
