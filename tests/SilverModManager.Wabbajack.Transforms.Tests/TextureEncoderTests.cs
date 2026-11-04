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

    private static byte[] Level(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 37 + i / 5);
        return pixels;
    }

    [Fact]
    public void TheNativeEncoderLoadsWhenItsLibraryIsBesideTheTests()
    {
        Assert.SkipUnless(NativeTexenc.LibraryIsBundledForThisPlatform, "no library is bundled for this platform");
        Assert.NotNull(Native.Value);
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

        Assert.NotEqual(new byte[dst.Length], dst);
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

    public static TheoryData<string, int, int> NotWholeBlocks => new(
        EncoderNames.SelectMany(name => new[] { (6, 8), (8, 5), (0, 8) }.Select(size => (name, size.Item1, size.Item2))));

    [Theory]
    [MemberData(nameof(NotWholeBlocks))]
    public void ALevelThatIsNotWholeBlocksThrows(string which, int width, int height)
        => Assert.Throws<ArgumentException>(() => Encoder(which).Encode(new byte[width * height * 4], width, height, BlockFormat.Bc1, new byte[width / 4 * (height / 4) * 8], 1, CancellationToken.None));

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
        Assert.SkipUnless(Native.Value is not null, "the native encoder did not load on this platform");
        var native = Encoder("native");

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
            .SelectMany(n => NativeFormats.SelectMany(f => TestTextures.Names.Select(t => (n, f, t)))));

    private static double Psnr(ITextureEncoder encoder, byte[] rgba, int size, BlockFormat format)
    {
        var dst = new byte[format.BlockBytes(size, size)];
        encoder.Encode(rgba, size, size, format, dst, 1, CancellationToken.None);
        return TextureQuality.Psnr(rgba, dst, size, size, format);
    }

    [Theory(SkipTestWithoutData = true)]
    [MemberData(nameof(QualityCases))]
    public void NativeBlocksDecodeAsWellAsManagedOnes(string which, BlockFormat format, string texture)
    {
        Assert.Equal("native", which);
        const int size = 64;
        var rgba = TestTextures.Named(texture, size);

        var native = Psnr(Encoder("native"), rgba, size, format);
        var managed = Psnr(Encoder("managed"), rgba, size, format);

        Assert.True(native >= managed - 2, $"{format} {texture}: native {native:F2} dB against managed {managed:F2} dB");
        if (texture is "gradient" or "partial alpha")
            Assert.True(native >= 30, $"{format} {texture}: native {native:F2} dB");
    }

    // Floors -----------------------------------------------------------------------------------------------------------

    private const int FloorSize = 1024;

    /// <summary>
    /// The native encoder's PSNR on each generated texture at <see cref="FloorSize"/> pixels, at the pinned levels, less half a
    /// dB. The BC1 and BC3 checkerboards are exact (about 99 dB), which is why their floor is 98.5; BC7's checkerboard is
    /// not exact, so its floor comes from the measured 57.16 dB. The benchmark harness prints the same figures (benchmarks/SilverModManager.Benchmarks, TextureEncoders).
    /// </summary>
    private static readonly Dictionary<(BlockFormat, string), double> NativeFloors = new()
    {
        [(BlockFormat.Bc1, "gradient")] = 51.4,
        [(BlockFormat.Bc1, "noise")] = 13.2,
        [(BlockFormat.Bc1, "checkerboard")] = 98.5,
        [(BlockFormat.Bc1, "partial alpha")] = 51.4,
        [(BlockFormat.Bc3, "gradient")] = 52.5,
        [(BlockFormat.Bc3, "noise")] = 14.4,
        [(BlockFormat.Bc3, "checkerboard")] = 98.5,
        [(BlockFormat.Bc3, "partial alpha")] = 52.5,
        [(BlockFormat.Bc7, "gradient")] = 56.3,
        [(BlockFormat.Bc7, "noise")] = 15.0,
        [(BlockFormat.Bc7, "checkerboard")] = 56.6,
        [(BlockFormat.Bc7, "partial alpha")] = 56.3,
    };

    public static TheoryData<BlockFormat, string> FloorCases => Native.Value is null
        ? []
        : new(NativeFloors.Keys.Select(k => (k.Item1, k.Item2)));

    [Theory(SkipTestWithoutData = true)]
    [MemberData(nameof(FloorCases))]
    public void NativeBlocksStayAboveTheirMeasuredFloor(BlockFormat format, string texture)
    {
        var psnr = Psnr(Encoder("native"), TestTextures.Named(texture, FloorSize), FloorSize, format);

        Assert.True(psnr >= NativeFloors[(format, texture)], $"{format} {texture}: {psnr:F2} dB against a floor of {NativeFloors[(format, texture)]:F2} dB");
    }
}
