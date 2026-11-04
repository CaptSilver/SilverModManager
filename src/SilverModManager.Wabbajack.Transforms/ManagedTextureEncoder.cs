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
        EncodeArguments.Check(format, rgba.Length, paddedWidth, paddedHeight, dst.Length, parallelism);
        cancellationToken.ThrowIfCancellationRequested();

        var encoder = new BcEncoder
        {
            OutputOptions = { Quality = CompressionQuality.Balanced, GenerateMipMaps = false, Format = ToCompressionFormat(format) },
        };
        encoder.Options.IsParallel = parallelism > 1;
        encoder.Options.TaskCount = parallelism;
        encoder.EncodeToRawBytes(rgba.ToArray(), paddedWidth, paddedHeight, PixelFormat.Rgba32, 0, out _, out _).CopyTo(dst);
    }

    internal static CompressionFormat ToCompressionFormat(BlockFormat format) => format switch
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
