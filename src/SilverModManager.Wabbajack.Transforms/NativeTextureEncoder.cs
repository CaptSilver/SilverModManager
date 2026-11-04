using System.Runtime.ExceptionServices;
using SilverModManager.Sdk.Native;

namespace SilverModManager.Wabbajack.Transforms;

/// <summary>
/// The quality each native format is encoded at, in the shim's own terms: the rgbcx level (0 to 18) for BC1 and BC3,
/// the bc7e preset (0 ultrafast, 1 veryfast, 2 fast, 3 basic, 4 slow, 5 veryslow, 6 slowest) for BC7. BC4 and BC5 have one quality.
/// </summary>
internal readonly record struct NativeEncoderLevels(uint Bc1, uint Bc3, uint Bc7)
{
    /// <summary>
    /// The fastest level whose decoded PSNR is no lower than BCnEncoder's Balanced on the generated textures (gradient,
    /// noise, checkerboard, partial alpha) at 1024 and 2048 pixels square, one thread. PSNR is what the TextureEncoders
    /// benchmark prints: BC1 over RGB, BC3 and BC7 over RGBA, bc7e not run perceptually. BC1 and BC3 meet that. BC7
    /// meets it within half a dB: no bc7e preset reaches Balanced on the gradient, and a gradient half a dB under is not
    /// visible, while leaving BC7 on the managed encoder keeps it 50 to 80 times slower.
    /// <para>
    /// rgbcx BC1 and BC3: level 10, where rgbcx's header says to start. It beats Balanced on every texture: gradient
    /// 51.93 against 50.21 dB (BC1) and 53.03 against 51.46 dB (BC3) at 1024 pixels, 53.47 against 51.17 and 54.63
    /// against 52.42 at 2048, noise by 1.4 dB, and the checkerboard is exact in both. Levels 11 and 12 score the same
    /// and run slower; 13 and up add 0.7 dB on the gradient for roughly half the speed. A 64 pixel gradient sits 0.1 to
    /// 0.2 dB under Balanced at level 10, which is below the sizes the rule measures.
    /// </para>
    /// <para>
    /// bc7e BC7: preset 4, "slow". On the gradient, Balanced scores 56.94 dB at 1024 pixels and 60.07 at 2048; slow scores
    /// 56.80 and 59.69, 0.14 and 0.38 dB under, and beats Balanced on every other texture (checkerboard 57.16 against
    /// 52.39, noise 15.55 against 14.30, partial alpha 56.84 against 54.93 at 1024). Every other preset is further under
    /// on the gradient (basic 0.6 and 0.7 dB, veryfast 0.8 and 1.3, fast 1.4 and 1.8, ultrafast 4.0 and 3.5; veryslow is
    /// ahead at 1024 but 1.5 behind at 2048, slowest 0.2 and 2.1 behind). Slow is also faster than basic: basic turns on
    /// bc7e's uber search and slow does not.
    /// </para>
    /// <para>
    /// One thread, Mpix/s on the 1024 pixel gradient, native against Balanced: BC1 14.3 against 1.0, BC3 23.2 against 1.0,
    /// BC7 9.1 against 0.11 (basic runs 5.5). Through DdsRecompressor from a BC7 source to a BC7 target with a full mip
    /// chain, native is 37 to 74 times Balanced's Mpix/s, and on the gradient its PSNR is 59.44 dB against 59.91.
    /// </para>
    /// </summary>
    public static NativeEncoderLevels Default { get; } = new(Bc1: 10, Bc3: 10, Bc7: 4);
}

/// <summary>
/// Encodes through libsmmtexenc.so (bc7e for BC7, rgbcx for BC1/BC3/BC4/BC5). The library starts no threads, so a level
/// is split into bands of rows and each band is one call, on the calling thread or on the pool when there is more than one.
/// Build one with <see cref="TryBind"/>, which is also what initialises the library.
/// </summary>
public sealed unsafe class NativeTextureEncoder : ITextureEncoder
{
    internal const string InitExport = "smm_texenc_init";

    // BC4 and BC5 are exported but not bound. rgbcx's fast encode_bc4/encode_bc5 have no quality level and decode
    // below BCnEncoder's Balanced on the generated textures (noise 29.2 to 29.4 against 30.5 to 30.8 dB; a 64 pixel
    // gradient 51.7 against 66.2 dB), so by the quality rule those formats stay on the managed encoder. The library also
    // has encode_bc4_hq/encode_bc5_hq, which the shim does not call yet; binding BC4/BC5 means calling those and
    // measuring again.
    private static readonly (BlockFormat Format, string Export, bool Bound)[] FormatExports =
    [
        (BlockFormat.Bc1, "smm_texenc_encode_bc1", true),
        (BlockFormat.Bc3, "smm_texenc_encode_bc3", true),
        (BlockFormat.Bc4, "smm_texenc_encode_bc4", false),
        (BlockFormat.Bc5, "smm_texenc_encode_bc5", false),
        (BlockFormat.Bc7, "smm_texenc_encode_bc7", true),
    ];

    /// <summary>Every symbol the library must export; a missing one means a stale binary.</summary>
    internal static IEnumerable<string> ExportNames => FormatExports.Select(f => f.Export).Prepend(InitExport);

    private readonly nint[] _encode;
    private readonly NativeEncoderLevels _levels;

    private NativeTextureEncoder(nint[] encode, NativeEncoderLevels levels)
    {
        _encode = encode;
        _levels = levels;
    }

    public string Name => "libsmmtexenc";

    public bool CanEncode(BlockFormat format) => (int)format < _encode.Length && _encode[(int)format] != 0;

    /// <summary>
    /// Looks up every export of the loaded library and, when all are there, calls <c>smm_texenc_init</c>, which runs once per
    /// process however often this is called, and has finished before this returns.
    /// </summary>
    internal static bool TryBind(INativeLibraryLoader loader, nint library, NativeEncoderLevels levels, out NativeTextureEncoder encoder, out IReadOnlyList<string> missing)
    {
        var lost = new List<string>();
        var encode = new nint[Enum.GetValues<BlockFormat>().Length];
        foreach (var (format, export, bound) in FormatExports)
        {
            if (!loader.TryGetExport(library, export, out var address)) lost.Add(export);
            else if (bound) encode[(int)format] = address;
        }
        var hasInit = loader.TryGetExport(library, InitExport, out var init);
        if (!hasInit) lost.Add(InitExport);

        missing = lost;
        encoder = new NativeTextureEncoder(encode, levels);
        if (lost.Count > 0) return false;

        InitialiseOnce(init);
        return true;
    }

    // rgbcx::init fills its tables without a lock and a second run rewrites entries an encode may be reading, so the
    // call is made once per process however many selectors (one per service provider, one per test class) bind the library.
    private static readonly Lock s_initLock = new();
    private static readonly HashSet<nint> s_initialised = [];

    private static void InitialiseOnce(nint init)
    {
        lock (s_initLock)
        {
            if (s_initialised.Add(init)) ((delegate* unmanaged<void>)init)();
        }
    }

    /// <summary>Lets a test that counts <c>smm_texenc_init</c> calls start from a process that has not run it.</summary>
    internal static void ForgetInitialisedLibraries()
    {
        lock (s_initLock) s_initialised.Clear();
    }

    private uint Level(BlockFormat format) => format switch
    {
        BlockFormat.Bc1 => _levels.Bc1,
        BlockFormat.Bc3 => _levels.Bc3,
        BlockFormat.Bc7 => _levels.Bc7,
        _ => 0,
    };

    public void Encode(ReadOnlySpan<byte> rgba, int paddedWidth, int paddedHeight, BlockFormat format, Span<byte> dst, int parallelism,
        CancellationToken cancellationToken)
    {
        if (!CanEncode(format)) throw new ArgumentOutOfRangeException(nameof(format), format, null);
        EncodeArguments.Check(format, rgba.Length, paddedWidth, paddedHeight, dst.Length, parallelism);
        cancellationToken.ThrowIfCancellationRequested();

        var export = _encode[(int)format];
        var level = Level(format);
        var rowBytes = paddedWidth * 4;
        var blockRowBytes = paddedWidth / 4 * format.BlockBytes();
        var rowsPerBand = RowsPerBand(paddedHeight, parallelism);
        var bands = (paddedHeight + rowsPerBand - 1) / rowsPerBand;

        fixed (byte* src = rgba)
        fixed (byte* dest = dst)
        {
            var srcAddress = (nint)src;
            var dstAddress = (nint)dest;

            void EncodeBand(int band)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var firstRow = band * rowsPerBand;
                var rows = Math.Min(rowsPerBand, paddedHeight - firstRow);
                var result = ((delegate* unmanaged<byte*, uint, uint, byte*, uint, int>)export)(
                    (byte*)(srcAddress + (nint)firstRow * rowBytes), (uint)paddedWidth, (uint)rows,
                    (byte*)(dstAddress + (nint)(firstRow / 4) * blockRowBytes), level);
                if (result != 0)
                    throw new InvalidOperationException($"libsmmtexenc refused a {paddedWidth}x{rows} {format} band at level {level} (returned {result})");
            }

            if (bands == 1)
            {
                EncodeBand(0);
                return;
            }

            try
            {
                Parallel.For(0, bands, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken }, EncodeBand);
            }
            catch (AggregateException failed) when (failed.InnerExceptions.Count > 0)
            {
                ExceptionDispatchInfo.Capture(failed.InnerExceptions[0]).Throw();
            }
        }
    }

    /// <summary>Whole block rows, at least one, so a small level is one band however many threads are offered.</summary>
    private static int RowsPerBand(int paddedHeight, int parallelism)
    {
        var perThread = (paddedHeight + parallelism - 1) / parallelism;
        return Math.Max(4, (perThread + 3) / 4 * 4);
    }
}
