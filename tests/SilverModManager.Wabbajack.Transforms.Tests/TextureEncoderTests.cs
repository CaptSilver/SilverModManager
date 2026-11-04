using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Sdk.Native;
using SilverModManager.Wabbajack.Transforms;

namespace SilverModManager.Wabbajack.Transforms.Tests;

/// <summary>
/// Behaviour every <see cref="ITextureEncoder"/> shares, run over the managed encoder and, when the bundled library
/// loads, the native one. Every native case names its encoder, so a fallback to managed cannot pass for native.
/// </summary>
[Collection(NativeTexenc.Collection)]
public class TextureEncoderTests
{
    private static readonly Lazy<ITextureEncoder?> Native = new(() =>
    {
        var chain = new TextureEncoderSelector(new BundledNativeLibraryLoader(typeof(NativeTextureEncoder).Assembly), NullLogger<TextureEncoderSelector>.Instance).Encoders;
        return chain[0] is NativeTextureEncoder ? chain[0] : null;
    });

    private static readonly BlockFormat[] NativeFormats = [BlockFormat.Bc1, BlockFormat.Bc3, BlockFormat.Bc7];

    private static ITextureEncoder Encoder(string name) => name switch
    {
        "managed" => new ManagedTextureEncoder(),
        _ => Native.Value ?? throw new InvalidOperationException("the native encoder did not load"),
    };

    private static IEnumerable<string> EncoderNames => Native.Value is null ? ["managed"] : ["managed", "native"];

    public static TheoryData<string> Encoders => new(EncoderNames);

    public static TheoryData<string, BlockFormat> EncoderFormats => new(
        EncoderNames.SelectMany(name => Enum.GetValues<BlockFormat>().Where(f => name == "managed" || NativeFormats.Contains(f)).Select(f => (name, f))));

    private static CompressionFormat Compression(BlockFormat format) => format switch
    {
        BlockFormat.Bc1 => CompressionFormat.Bc1,
        BlockFormat.Bc1PunchThrough => CompressionFormat.Bc1WithAlpha,
        BlockFormat.Bc2 => CompressionFormat.Bc2,
        BlockFormat.Bc3 => CompressionFormat.Bc3,
        BlockFormat.Bc4 => CompressionFormat.Bc4,
        BlockFormat.Bc5 => CompressionFormat.Bc5,
        _ => CompressionFormat.Bc7,
    };

    private static byte[] Level(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 37 + i / 5);
        return pixels;
    }

    [Fact]
    public void TheNativeEncoderLoadsWhenItsLibraryIsBesideTheTests()
    {
        var present = BundledNativeLibrary.CandidatePaths(TextureEncoderSelector.LibraryFileName, typeof(NativeTextureEncoder).Assembly.Location, AppContext.BaseDirectory).Any(File.Exists);
        Assert.Equal(present, Native.Value is not null);
    }

    [Theory]
    [MemberData(nameof(Encoders))]
    public void NamesItself(string which)
        => Assert.Equal(which == "native" ? "libsmmtexenc" : "BCnEncoder.Net", Encoder(which).Name);

    [Theory]
    [MemberData(nameof(Encoders))]
    public void TakesTheFormatsItClaims(string which)
    {
        var encoder = Encoder(which);
        foreach (var format in Enum.GetValues<BlockFormat>())
            Assert.Equal(which == "managed" || NativeFormats.Contains(format), encoder.CanEncode(format));
    }

    [Theory]
    [MemberData(nameof(EncoderFormats))]
    public void FillsDstWithTheBlocksOfALevel(string which, BlockFormat format)
    {
        var dst = new byte[format.BlockBytes(8, 12)];
        Array.Fill(dst, (byte)0xAA);

        Encoder(which).Encode(Level(8, 12), 8, 12, format, dst, 1, CancellationToken.None);

        Assert.NotEqual(Enumerable.Repeat((byte)0xAA, dst.Length).ToArray(), dst);
    }

    [Theory]
    [MemberData(nameof(EncoderFormats))]
    public void ThreadsDoNotChangeTheBlocks(string which, BlockFormat format)
    {
        var encoder = Encoder(which);
        var one = new byte[format.BlockBytes(48, 52)];
        var many = new byte[one.Length];

        encoder.Encode(Level(48, 52), 48, 52, format, one, 1, CancellationToken.None);
        encoder.Encode(Level(48, 52), 48, 52, format, many, 5, CancellationToken.None);

        Assert.Equal(one, many);
    }

    [Theory]
    [MemberData(nameof(Encoders))]
    public void ARgbaBufferOfTheWrongLengthThrows(string which)
    {
        var dst = new byte[BlockFormat.Bc7.BlockBytes(8, 8)];

        Assert.Throws<ArgumentException>(() => Encoder(which).Encode(new byte[8 * 8 * 4 - 1], 8, 8, BlockFormat.Bc7, dst, 1, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => Encoder(which).Encode(new byte[8 * 8 * 4 + 4], 8, 8, BlockFormat.Bc7, dst, 1, CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(Encoders))]
    public void ADstOfTheWrongLengthThrows(string which)
    {
        var rgba = Level(8, 8);

        Assert.Throws<ArgumentException>(() => Encoder(which).Encode(rgba, 8, 8, BlockFormat.Bc7, new byte[BlockFormat.Bc7.BlockBytes(8, 8) - 1], 1, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => Encoder(which).Encode(rgba, 8, 8, BlockFormat.Bc1, new byte[BlockFormat.Bc7.BlockBytes(8, 8)], 1, CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(Encoders))]
    public void ALevelThatIsNotWholeBlocksThrows(string which)
    {
        Assert.Throws<ArgumentException>(() => Encoder(which).Encode(new byte[6 * 8 * 4], 6, 8, BlockFormat.Bc1, new byte[16], 1, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => Encoder(which).Encode(new byte[8 * 5 * 4], 8, 5, BlockFormat.Bc1, new byte[16], 1, CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(Encoders))]
    public void AnEncodeCancelledBeforeItStartsThrows(string which)
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            Encoder(which).Encode(Level(8, 8), 8, 8, BlockFormat.Bc7, new byte[BlockFormat.Bc7.BlockBytes(8, 8)], 1, cts.Token));
    }

    [Theory]
    [MemberData(nameof(Encoders))]
    public void ParallelismBelowOneThrows(string which)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Encoder(which).Encode(Level(8, 8), 8, 8, BlockFormat.Bc1, new byte[32], 0, CancellationToken.None));

    [Fact]
    public void TheNativeEncoderRefusesWhatItDeclines()
    {
        if (Native.Value is not { } native) return;

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            native.Encode(Level(8, 8), 8, 8, BlockFormat.Bc2, new byte[BlockFormat.Bc2.BlockBytes(8, 8)], 1, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            native.Encode(Level(8, 8), 8, 8, BlockFormat.Bc1PunchThrough, new byte[BlockFormat.Bc1PunchThrough.BlockBytes(8, 8)], 1, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            native.Encode(Level(8, 8), 8, 8, BlockFormat.Bc4, new byte[BlockFormat.Bc4.BlockBytes(8, 8)], 1, CancellationToken.None));
    }

    // Quality ---------------------------------------------------------------------------------------------------------

    public static TheoryData<string, BlockFormat, string> QualityCases => new(
        EncoderNames.Where(n => n == "native")
            .SelectMany(n => NativeFormats.SelectMany(f => new[] { "gradient", "noise", "checkerboard", "partial alpha" }.Select(t => (n, f, t)))));

    private static byte[] Texture(string name, int size) => name switch
    {
        "gradient" => TestTextures.GradientRgba(size),
        "noise" => TestTextures.NoiseRgba(size, 1234),
        "checkerboard" => TestTextures.CheckerboardRgba(size),
        _ => TestTextures.PartialAlphaRgba(size),
    };

    /// <summary>Channels the format carries: BC1 is opaque.</summary>
    private static int Channels(BlockFormat format) => format == BlockFormat.Bc1 ? 3 : 4;

    private static double Psnr(ITextureEncoder encoder, byte[] rgba, int size, BlockFormat format)
    {
        var dst = new byte[format.BlockBytes(size, size)];
        encoder.Encode(rgba, size, size, format, dst, 1, CancellationToken.None);
        var decoded = new BcDecoder().DecodeRaw(dst, size, size, Compression(format));
        var channels = Channels(format);
        double squared = 0;
        for (var i = 0; i < size * size; i++)
        {
            var actual = new[] { decoded[i].r, decoded[i].g, decoded[i].b, decoded[i].a };
            for (var c = 0; c < channels; c++)
            {
                var difference = rgba[i * 4 + c] - actual[c];
                squared += difference * difference;
            }
        }
        var mse = squared / (size * size * channels);
        return mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    [Theory(SkipTestWithoutData = true)]
    [MemberData(nameof(QualityCases))]
    public void NativeBlocksDecodeAsWellAsManagedOnes(string which, BlockFormat format, string texture)
    {
        Assert.Equal("native", which);
        const int size = 64;
        var rgba = Texture(texture, size);

        var native = Psnr(Encoder("native"), rgba, size, format);
        var managed = Psnr(Encoder("managed"), rgba, size, format);

        Assert.True(native >= managed - 2, $"{format} {texture}: native {native:F2} dB against managed {managed:F2} dB");
        if (texture is "gradient" or "partial alpha")
            Assert.True(native >= 30, $"{format} {texture}: native {native:F2} dB");
    }
}
