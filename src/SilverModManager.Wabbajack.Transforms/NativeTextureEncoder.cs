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
    /// Each the lowest level whose decoded PSNR is no lower than BCnEncoder's Balanced on every generated test texture
    /// (gradient, two noise seeds, checkerboard, partial alpha) at 64 and at 256 pixels square, single thread:
    /// rgbcx level 13 for BC1 and BC3 (10 to 12 sit 0.1 to 0.2 dB under on the gradient: 45.26 against 45.40 dB for BC1,
    /// 46.44 against 46.67 dB for BC3), bc7e "basic" (3) for BC7 (the faster presets fall under on small gradient and
    /// partial-alpha content). At 256 squared, native against managed Balanced: BC1 about 12 ms against 70 to 110 ms,
    /// BC3 about 7 ms against 70 to 80 ms, BC7 about 5 to 13 ms against 0.4 to 20 s.
    /// </summary>
    public static NativeEncoderLevels Default { get; } = new(Bc1: 13, Bc3: 13, Bc7: 3);
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
