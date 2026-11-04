using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SilverModManager.Sdk.Native;
using SilverModManager.Wabbajack.Transforms;

namespace SilverModManager.Wabbajack.Transforms.Tests;

/// <summary>Selection and fallback, through a loader that stands in for the real library.</summary>
[Collection(NativeTexenc.Collection)]
public sealed unsafe class TextureEncoderSelectorTests
{
    private static int s_inits;
    private static int s_initsBeforeFirstEncode = -1;
    private static readonly List<(uint Width, uint Rows, nint Src, nint Dst, uint Level)> s_calls = [];
    private static readonly Lock s_lock = new();

    [UnmanagedCallersOnly]
    private static void FakeInit() => Interlocked.Increment(ref s_inits);

    [UnmanagedCallersOnly]
    private static int FakeEncode(byte* rgba, uint width, uint rows, byte* dst, uint level)
    {
        lock (s_lock)
        {
            if (s_calls.Count == 0) s_initsBeforeFirstEncode = s_inits;
            s_calls.Add((width, rows, (nint)rgba, (nint)dst, level));
        }
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int FailingEncode(byte* rgba, uint width, uint rows, byte* dst, uint level) => 7;

    private sealed class FakeLoader(IEnumerable<string>? missing = null, bool found = true, bool failing = false) : INativeLibraryLoader
    {
        private readonly HashSet<string> _missing = [.. missing ?? []];
        public int Loads { get; private set; }

        public bool TryLoad(string fileName, out nint handle, out IReadOnlyList<string> pathsTried)
        {
            Loads++;
            handle = found ? 1 : 0;
            pathsTried = ["/app/runtimes/linux-arm64/native/" + fileName, fileName];
            return found;
        }

        public bool TryGetExport(nint handle, string name, out nint address)
        {
            address = 0;
            if (_missing.Contains(name)) return false;
            address = name == "smm_texenc_init"
                ? (nint)(delegate* unmanaged<void>)&FakeInit
                : failing ? (nint)(delegate* unmanaged<byte*, uint, uint, byte*, uint, int>)&FailingEncode
                : (nint)(delegate* unmanaged<byte*, uint, uint, byte*, uint, int>)&FakeEncode;
            return true;
        }
    }

    private static void Reset()
    {
        s_inits = 0;
        s_initsBeforeFirstEncode = -1;
        lock (s_lock) s_calls.Clear();
    }

    private static TextureEncoderSelector Select(INativeLibraryLoader loader, out ListLogger<TextureEncoderSelector> logger)
    {
        logger = new ListLogger<TextureEncoderSelector>();
        return new TextureEncoderSelector(loader, logger);
    }

    [Fact]
    public void ALoaderThatFindsNothing_SelectsManagedAndLogsThePathsTriedOnce()
    {
        var selector = Select(new FakeLoader(found: false), out var logger);

        Assert.Equal(["BCnEncoder.Net"], selector.Encoders.Select(e => e.Name));
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("/app/runtimes/linux-arm64/native/libsmmtexenc.so", entry.Message);
        Assert.Contains("BCnEncoder.Net", entry.Message);
    }

    [Fact]
    public void ALibraryThatLacksOneExport_SelectsManagedWithAWarningNamingIt()
    {
        var selector = Select(new FakeLoader(missing: ["smm_texenc_encode_bc5"]), out var logger);

        Assert.Equal(["BCnEncoder.Net"], selector.Encoders.Select(e => e.Name));
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("smm_texenc_encode_bc5", entry.Message);
    }

    [Fact]
    public void AStaleLibraryIsNeverInitialised()
    {
        Reset();

        Select(new FakeLoader(missing: ["smm_texenc_init"]), out _);
        Select(new FakeLoader(missing: ["smm_texenc_encode_bc7"]), out _);

        Assert.Equal(0, s_inits);
    }

    [Fact]
    public void ALibraryWithEveryExport_PutsNativeBeforeManaged()
    {
        var selector = Select(new FakeLoader(), out var logger);

        Assert.Equal(["libsmmtexenc", "BCnEncoder.Net"], selector.Encoders.Select(e => e.Name));
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("libsmmtexenc", entry.Message);
    }

    [Fact]
    public void TheLibraryIsLoadedAndInitialisedOnceHoweverOftenTheChainIsRead()
    {
        Reset();
        var loader = new FakeLoader();
        var selector = Select(loader, out var logger);

        _ = selector.Encoders;
        _ = selector.Encoders;

        Assert.Equal(1, loader.Loads);
        Assert.Equal(1, s_inits);
        Assert.Single(logger.Entries);
    }

    [Fact]
    public void InitHasRunBeforeTheFirstEncode()
    {
        Reset();
        var native = Select(new FakeLoader(), out _).Encoders[0];

        native.Encode(new byte[8 * 8 * 4], 8, 8, BlockFormat.Bc7, new byte[BlockFormat.Bc7.BlockBytes(8, 8)], 1, CancellationToken.None);

        Assert.Equal(1, s_initsBeforeFirstEncode);
    }

    [Fact]
    public void NativeDeclinesBc2PunchThroughBc4AndBc5_SoTheChainLandsOnManagedForThem()
    {
        var chain = Select(new FakeLoader(), out _).Encoders;

        Assert.All(new[] { BlockFormat.Bc2, BlockFormat.Bc1PunchThrough, BlockFormat.Bc4, BlockFormat.Bc5 }, f =>
        {
            Assert.False(chain[0].CanEncode(f), f.ToString());
            Assert.Equal("BCnEncoder.Net", chain.First(e => e.CanEncode(f)).Name);
        });
        Assert.All(new[] { BlockFormat.Bc1, BlockFormat.Bc3, BlockFormat.Bc7 }, f =>
            Assert.Equal("libsmmtexenc", chain.First(e => e.CanEncode(f)).Name));
    }

    [Fact]
    public void EachFormatReachesItsOwnShimFunctionWithItsOwnLevel()
    {
        Reset();
        var levels = new NativeEncoderLevels(Bc1: 3, Bc3: 5, Bc7: 2);
        Assert.True(NativeTextureEncoder.TryBind(new FakeLoader(), 1, levels, out var encoder, out _));

        foreach (var format in new[] { BlockFormat.Bc1, BlockFormat.Bc3, BlockFormat.Bc7 })
            encoder.Encode(new byte[4 * 4 * 4], 4, 4, format, new byte[format.BlockBytes(4, 4)], 1, CancellationToken.None);

        Assert.Equal([3u, 5u, 2u], s_calls.Select(c => c.Level));
    }

    [Theory]
    [InlineData(8, 8, 8, new[] { 4, 4 })]
    [InlineData(8, 4, 4, new[] { 4 })]
    [InlineData(8, 20, 3, new[] { 8, 8, 4 })]
    [InlineData(8, 20, 1, new[] { 20 })]
    [InlineData(8, 64, 4, new[] { 16, 16, 16, 16 })]
    public void ALevelIsSplitIntoBandsOfWholeBlockRowsNeverUnderFourRows(int width, int height, int parallelism, int[] bandRows)
    {
        Reset();
        Assert.True(NativeTextureEncoder.TryBind(new FakeLoader(), 1, NativeEncoderLevels.Default, out var encoder, out _));
        var rgba = new byte[width * height * 4];
        var dst = new byte[BlockFormat.Bc1.BlockBytes(width, height)];

        encoder.Encode(rgba, width, height, BlockFormat.Bc1, dst, parallelism, CancellationToken.None);

        var calls = s_calls.OrderBy(c => c.Dst).ToArray();
        Assert.Equal(bandRows, calls.Select(c => (int)c.Rows));
        Assert.All(calls, c => Assert.Equal((uint)width, c.Width));
        // Each band reads its own rows and writes its own blocks.
        var rowsBefore = 0;
        foreach (var call in calls)
        {
            Assert.Equal(calls[0].Src + rowsBefore * width * 4, call.Src);
            Assert.Equal(calls[0].Dst + rowsBefore / 4 * (width / 4) * 8, call.Dst);
            rowsBefore += (int)call.Rows;
        }
    }

    [Fact]
    public void ANonZeroReturnFromTheShimFailsTheEncode()
    {
        Assert.True(NativeTextureEncoder.TryBind(new FakeLoader(failing: true), 1, NativeEncoderLevels.Default, out var encoder, out _));

        var act = () => encoder.Encode(new byte[4 * 4 * 4], 4, 4, BlockFormat.Bc7, new byte[16], 1, CancellationToken.None);

        var thrown = Assert.Throws<InvalidOperationException>(act);
        Assert.Contains("Bc7", thrown.Message);
        Assert.Contains("7", thrown.Message);
    }

    [Fact]
    public void AnEncodeCancelledBeforeItStartsCallsNothing()
    {
        Reset();
        Assert.True(NativeTextureEncoder.TryBind(new FakeLoader(), 1, NativeEncoderLevels.Default, out var encoder, out _));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            encoder.Encode(new byte[16 * 16 * 4], 16, 16, BlockFormat.Bc1, new byte[BlockFormat.Bc1.BlockBytes(16, 16)], 4, cts.Token));
        Assert.Empty(s_calls);
    }

    [Fact]
    public void WithTheRealLibraryBesideTheTests_TheDefaultSelectionIsNative()
    {
        var logger = new ListLogger<TextureEncoderSelector>();
        var selector = new TextureEncoderSelector(new BundledNativeLibraryLoader(typeof(NativeTextureEncoder).Assembly), logger);

        var expected = BundledNativeLibrary.CandidatePaths(TextureEncoderSelector.LibraryFileName, typeof(NativeTextureEncoder).Assembly.Location, AppContext.BaseDirectory).Any(File.Exists)
            ? "libsmmtexenc"
            : "BCnEncoder.Net";
        Assert.Equal(expected, selector.Encoders[0].Name);
        Assert.Equal("BCnEncoder.Net", selector.Encoders[^1].Name);
    }

    [Fact]
    public void TheRealLoaderOnAForeignRuntimeReportsWhereItLooked()
    {
        var loader = new BundledNativeLibraryLoader(typeof(NativeTextureEncoder).Assembly, ["linux-riscv64"]);

        // Not asserted to fail: the library path fallback may still find a copy the process already holds.
        loader.TryLoad("libsmm-no-such-texenc.so", out _, out var tried);

        Assert.Contains(tried, path => path.EndsWith("runtimes/linux-riscv64/native/libsmm-no-such-texenc.so"));
        Assert.DoesNotContain(tried, path => path.Contains("linux-x64"));
    }
}
