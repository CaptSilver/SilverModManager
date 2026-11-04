using BenchmarkDotNet.Attributes;
using SilverModManager.Benchmarks.Interfaces;
using SilverModManager.Wabbajack.Transforms;

namespace SilverModManager.Benchmarks.Benchmarks.Textures;

/// <summary>
/// Case 1: the encoder alone, one thread, on a padded level, for every generated texture. The native library is bound at
/// each level in turn, which is what picks the level pinned in <see cref="NativeEncoderLevels.Default"/>. Filter with
/// BenchmarkDotNet's <c>--filter</c>, e.g. <c>--filter '*native/Bc7*'</c>.
/// </summary>
[BenchmarkInfo("TextureEncoders", "Texture encoders alone, per quality level: Mpix/s and PSNR on generated 1024 and 2048 pixel textures (BC1, BC3, BC5, BC7).")]
[Config(typeof(TextureBenchmarkConfig))]
public class EncoderPerLevel : IBenchmark
{
    private static readonly BlockFormat[] Formats = [BlockFormat.Bc1, BlockFormat.Bc3, BlockFormat.Bc5, BlockFormat.Bc7];
    private static readonly int[] Sizes = [1024, 2048];

    // rgbcx's header says to start at 10; the pinned level sits inside this range. bc7e: every preset, since the quality rule has to show that none of the fast ones does better than the pinned one.
    private static readonly uint[] RgbcxLevels = [8, 10, 11, 12, 13, 14, 15, 18];
    private static readonly uint[] Bc7Levels = [0, 1, 2, 3, 4, 5, 6];

    public sealed record Case(string Encoder, BlockFormat Format, string Texture, int Size, uint Level)
    {
        public override string ToString() => $"{Encoder}/{Format}/{Texture}/{Size}/{(Encoder == "managed" ? "Balanced" : $"level {Level}")}";
    }

    public static IEnumerable<Case> Cases()
    {
        foreach (var size in Sizes)
        foreach (var texture in TestTextures.Names)
        foreach (var format in Formats)
        {
            // A managed BC7 level takes tens of seconds at 1024 pixels and minutes at 2048, and the noise one over six
            // minutes at 1024, so its baseline is not run at 2048. Filter the other cases in when the numbers are wanted.
            if (size == Sizes[0] || texture != "noise") yield return new Case("managed", format, texture, size, 0);
            // BC5 is exported by the library but not bound: its single quality decodes under Balanced, see NativeTextureEncoder.
            if (format == BlockFormat.Bc5) continue;
            foreach (var level in format == BlockFormat.Bc7 ? Bc7Levels : RgbcxLevels)
                yield return new Case("native", format, texture, size, level);
        }
    }

    private ITextureEncoder _encoder = null!;
    private byte[] _rgba = null!;
    private byte[] _dst = null!;
    private string _key = "";

    [ParamsSource(nameof(Cases))]
    public Case Input { get; set; } = null!;

    [GlobalSetup]
    public void Setup()
    {
        _encoder = Input.Encoder == "managed" ? new ManagedTextureEncoder() : NativeEncoders.At(NativeEncoders.With(Input.Format, Input.Level));
        _rgba = TestTextures.Named(Input.Texture, Input.Size);
        _dst = new byte[Input.Format.BlockBytes(Input.Size, Input.Size)];
        _key = Input.ToString();
        TextureResults.Expect(_key, new TextureResults.Entry((double)Input.Size * Input.Size, _rgba, Input.Size, Input.Size, Input.Format));
    }

    [Benchmark]
    public void Encode()
    {
        _encoder.Encode(_rgba, Input.Size, Input.Size, Input.Format, _dst, 1, CancellationToken.None);
        TextureResults.Record(_key, _dst);
    }
}
