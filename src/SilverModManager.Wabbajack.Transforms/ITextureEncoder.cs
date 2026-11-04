namespace SilverModManager.Wabbajack.Transforms;

/// <summary>Encodes one padded mip level of RGBA32 pixels into one block-compressed format.</summary>
public interface ITextureEncoder
{
    /// <summary>Asserted by tests and printed where the encoder is named.</summary>
    string Name { get; }

    bool CanEncode(BlockFormat format);

    /// <summary>
    /// <paramref name="rgba"/> is exactly <c>paddedWidth * paddedHeight * 4</c> bytes of straight-alpha pixels, both
    /// dimensions multiples of 4 (the recompressor pads). <paramref name="dst"/> is exactly
    /// <see cref="BlockFormats.BlockBytes(BlockFormat, int, int)"/> bytes. Both lengths are checked and a wrong one throws.
    /// <paramref name="parallelism"/> (at least 1) is how many threads the level may be spread over; the caller decides it
    /// because a loose-file install already runs one texture per worker.
    /// </summary>
    void Encode(ReadOnlySpan<byte> rgba, int paddedWidth, int paddedHeight, BlockFormat format, Span<byte> dst, int parallelism,
        CancellationToken cancellationToken);
}
