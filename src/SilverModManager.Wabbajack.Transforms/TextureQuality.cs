using BCnEncoder.Decoder;

namespace SilverModManager.Wabbajack.Transforms;

/// <summary>
/// How well encoded blocks reproduce the level they came from. Shared by the encoder tests and the benchmark harness so
/// both name the same number when they say "dB".
/// </summary>
internal static class TextureQuality
{
    /// <summary>What <see cref="Psnr"/> returns when the decoded blocks equal the reference, instead of infinity.</summary>
    public const double Identical = 99;

    /// <summary>
    /// PSNR in dB of <paramref name="blocks"/> decoded against <paramref name="reference"/> (RGBA32 straight alpha,
    /// <paramref name="width"/> x <paramref name="height"/>). Only the channels the format carries count: BC1 is opaque,
    /// BC4 holds red, BC5 red and green.
    /// </summary>
    public static double Psnr(ReadOnlySpan<byte> reference, byte[] blocks, int width, int height, BlockFormat format)
    {
        if (reference.Length != width * height * 4)
            throw new ArgumentException($"A {width}x{height} reference holds {width * height * 4} bytes of RGBA, not {reference.Length}", nameof(reference));

        var decoded = new BcDecoder().DecodeRaw(blocks, width, height, ManagedTextureEncoder.ToCompressionFormat(format));
        var channels = Channels(format);
        double squared = 0;
        for (var i = 0; i < width * height; i++)
        {
            var pixel = decoded[i];
            for (var c = 0; c < channels; c++)
            {
                var actual = c switch { 0 => pixel.r, 1 => pixel.g, 2 => pixel.b, _ => pixel.a };
                var difference = reference[i * 4 + c] - actual;
                squared += difference * difference;
            }
        }
        var mse = squared / ((double)width * height * channels);
        return mse == 0 ? Identical : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    private static int Channels(BlockFormat format) => format switch
    {
        BlockFormat.Bc1 => 3,
        BlockFormat.Bc4 => 1,
        BlockFormat.Bc5 => 2,
        _ => 4,
    };
}
