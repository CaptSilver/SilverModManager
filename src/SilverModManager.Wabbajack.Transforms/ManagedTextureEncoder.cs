using BCnEncoder.Encoder;
using BCnEncoder.Shared;

namespace SilverModManager.Wabbajack.Transforms;

/// <summary>Encodes with BCnEncoder.Net. Takes every <see cref="BlockFormat"/>.</summary>
public sealed class ManagedTextureEncoder : ITextureEncoder
{
    public string Name => "BCnEncoder.Net";

    public bool CanEncode(BlockFormat format) => Enum.IsDefined(format);

    public void Encode(ReadOnlySpan<byte> rgba, int paddedWidth, int paddedHeight, BlockFormat format, Span<byte> dst, int parallelism,
        CancellationToken cancellationToken)
    {
        if (!CanEncode(format)) throw new ArgumentOutOfRangeException(nameof(format), format, null);
        ArgumentOutOfRangeException.ThrowIfLessThan(parallelism, 1);
        if (paddedWidth < 4 || paddedHeight < 4 || paddedWidth % 4 != 0 || paddedHeight % 4 != 0)
            throw new ArgumentException($"A level to encode is padded to whole blocks, not {paddedWidth}x{paddedHeight}");
        if (rgba.Length != paddedWidth * paddedHeight * 4)
            throw new ArgumentException($"A {paddedWidth}x{paddedHeight} level holds {paddedWidth * paddedHeight * 4} bytes of RGBA, not {rgba.Length}", nameof(rgba));
        var expected = format.BlockBytes(paddedWidth, paddedHeight);
        if (dst.Length != expected)
            throw new ArgumentException($"A {paddedWidth}x{paddedHeight} {format} level takes {expected} bytes, not {dst.Length}", nameof(dst));
        cancellationToken.ThrowIfCancellationRequested();

        var encoder = new BcEncoder
        {
            OutputOptions = { Quality = CompressionQuality.Balanced, GenerateMipMaps = false, Format = ToCompressionFormat(format) },
        };
        encoder.Options.IsParallel = parallelism > 1;
        encoder.Options.TaskCount = parallelism;
        encoder.EncodeToRawBytes(rgba.ToArray(), paddedWidth, paddedHeight, PixelFormat.Rgba32, 0, out _, out _).CopyTo(dst);
    }

    private static CompressionFormat ToCompressionFormat(BlockFormat format) => format switch
    {
        BlockFormat.Bc1 => CompressionFormat.Bc1,
        BlockFormat.Bc1PunchThrough => CompressionFormat.Bc1WithAlpha,
        BlockFormat.Bc2 => CompressionFormat.Bc2,
        BlockFormat.Bc3 => CompressionFormat.Bc3,
        BlockFormat.Bc4 => CompressionFormat.Bc4,
        BlockFormat.Bc5 => CompressionFormat.Bc5,
        BlockFormat.Bc7 => CompressionFormat.Bc7,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };
}
