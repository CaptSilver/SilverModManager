using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using BenchmarkDotNet.Attributes;
using SilverModManager.Benchmarks.Interfaces;
using SilverModManager.Wabbajack.Transforms;

namespace SilverModManager.Benchmarks.Benchmarks.Textures;

/// <summary>
/// Cases 2 and 3: <see cref="DdsRecompressor.Recompress"/> from a BC7 source (decode, resize, mip chain, encode, write)
/// to a full mip chain at the same size, in the two shapes an install produces. Concurrent is the loose-file shape: one
/// texture per worker, each at parallelism 1. Archive is the BSA shape: the inputs one after another, each spread over
/// row bands. The count is 4 because the agent running this must not take more than four cores.
/// </summary>
[BenchmarkInfo("TextureEndToEnd", "DdsRecompressor end to end from a BC7 source, loose-file and archive shapes, native chain against BCnEncoder.Net alone.")]
[Config(typeof(TextureBenchmarkConfig))]
public class EndToEnd : IBenchmark
{
    private const int Inputs = 4;

    public enum Shape { Single, Concurrent, Archive }

    public sealed record Case(string Encoder, BlockFormat Target, string Texture, int Size, Shape Shape)
    {
        public int Textures => Shape == Shape.Single ? 1 : Inputs;
        public override string ToString() => $"{Encoder}/{Target}/{Texture}/{Size}/{Shape}";
    }

    public static IEnumerable<Case> Cases()
    {
        // The gradient is the texture BCnEncoder does best on, the partial-alpha one has real alpha for BC3 and BC7. Noise is
        // left out: the managed BC7 encode of it takes close to seven minutes a texture, and the shapes below run it nine times.
        // BC1 takes the gradient only, because a partial-alpha BC1 target becomes punch-through, which only the managed
        // encoder takes: nothing native to compare.
        (BlockFormat Target, string Texture)[] combinations =
            [(BlockFormat.Bc7, "gradient"), (BlockFormat.Bc7, "partial alpha"), (BlockFormat.Bc3, "gradient"), (BlockFormat.Bc1, "gradient")];
        foreach (var (target, texture) in combinations)
        foreach (var shape in Enum.GetValues<Shape>())
        foreach (var encoder in new[] { "managed", "native" })
            yield return new Case(encoder, target, texture, 1024, shape);
    }

    private DdsRecompressor _recompressor = null!;
    private byte[] _source = null!;
    private DdsTarget _target;
    private string _key = "";
    private byte[]? _lastOutput;

    [ParamsSource(nameof(Cases))]
    public Case Input { get; set; } = null!;

    [GlobalSetup]
    public void Setup()
    {
        var native = NativeEncoders.At(NativeEncoderLevels.Default);
        _recompressor = Input.Encoder == "managed" ? DdsRecompressor.Managed() : new DdsRecompressor([native, new ManagedTextureEncoder()]);

        var size = Input.Size;
        var rgba = TestTextures.Named(Input.Texture, size);
        var blocks = new byte[BlockFormat.Bc7.BlockBytes(size, size)];
        native.Encode(rgba, size, size, BlockFormat.Bc7, blocks, 1, CancellationToken.None);
        using var source = new MemoryStream();
        TestTextures.WriteDx10(source, DxgiFormat.BC7_UNORM, size, size, blocks);
        _source = source.ToArray();

        // The reference for PSNR is what the recompressor sees, the decoded BC7 source, so the figure is the target
        // encoder's loss and not the source's.
        var decoded = new BcDecoder().DecodeRaw(blocks, size, size, CompressionFormat.Bc7);
        var reference = new byte[size * size * 4];
        for (var i = 0; i < decoded.Length; i++) (reference[i * 4], reference[i * 4 + 1], reference[i * 4 + 2], reference[i * 4 + 3]) = (decoded[i].r, decoded[i].g, decoded[i].b, decoded[i].a);

        _target = new DdsTarget(size, size, 0, Input.Target switch
        {
            BlockFormat.Bc1 => DxgiFormat.BC1_UNORM,
            BlockFormat.Bc3 => DxgiFormat.BC3_UNORM,
            _ => DxgiFormat.BC7_UNORM,
        });
        _key = Input.ToString();
        TextureResults.Expect(_key, new TextureResults.Entry((double)size * size * Input.Textures, reference, size, size, Input.Target));
    }

    [Benchmark]
    public async Task Recompress()
    {
        switch (Input.Shape)
        {
            case Shape.Single:
                _lastOutput = await One(1);
                break;
            case Shape.Concurrent:
                _lastOutput = (await Task.WhenAll(Enumerable.Range(0, Inputs).Select(_ => One(1))))[0];
                break;
            default:
                for (var i = 0; i < Inputs; i++) _lastOutput = await One(Inputs);
                break;
        }

        TextureResults.Record(_key, TopLevel(_lastOutput!));
    }

    private async Task<byte[]> One(int parallelism)
    {
        using var output = new MemoryStream();
        await _recompressor.Recompress(new MemoryStream(_source), _target, output, parallelism, CancellationToken.None);
        return output.ToArray();
    }

    private static byte[] TopLevel(byte[] dds)
    {
        var header = DdsHeader.Read(dds);
        return dds.AsSpan(header.DataOffset, (int)header.LevelSize(0)).ToArray();
    }
}
