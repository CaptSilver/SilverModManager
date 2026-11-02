using System.Diagnostics;
using NexusMods.Paths;
using SilverModManager.Games.Bethesda.Archives;
using SilverModManager.Wabbajack.Acquire;

namespace SilverModManager.Wabbajack.Tests.Acquire;

/// <summary>
/// Times extracting members of one compressed Bethesda archive with 24 workers at different reader counts, to show
/// what capping an opened archive's readers costs in wall time. Run with <c>--filter-trait Benchmark=True</c>.
/// </summary>
[Collection(MemoryMeasuringCollection.Name)]
public class BsaMemberOpenerBenchmarks(ITestOutputHelper helper)
{
    private static int Env(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;

    private static readonly int Members = Env("SILVER_BSA_MEMBERS", 400);
    private static readonly int MemberKb = Env("SILVER_BSA_MEMBER_KB", 512);

    private static string NameOf(int i) => $"textures/actors/set{i % 40}/part{i / 40}.dds";

    // Compresses to roughly half: each 64 random bytes are followed by a copy of themselves.
    private static byte[] MemberData(int i)
    {
        var bytes = new byte[MemberKb * 1024];
        new Random(i).NextBytes(bytes);
        for (var at = 0; at + 128 <= bytes.Length; at += 128) bytes.AsSpan(at, 64).CopyTo(bytes.AsSpan(at + 64, 64));
        return bytes;
    }

    private static async Task<byte[]> CompressedBsa()
    {
        using var temp = new TemporaryFileManager(NexusMods.Paths.FileSystem.Shared);
        // Flags 0x7: directory names, file names, compressed.
        var builder = BethesdaArchiveBuilder.Create(new Tes5ArchiveState("BSA\0", 104, 0x7, 0), temp);
        for (var i = 0; i < Members; i++)
            await builder.AddFile(new Tes5FileState(i, RelativePath.FromUnsanitizedInput(NameOf(i)), false), new MemoryStream(MemberData(i)), CancellationToken.None);
        var output = new MemoryStream();
        await builder.Build(output, CancellationToken.None);
        await builder.DisposeAsync();
        return output.ToArray();
    }

    [Fact]
    [Trait("Benchmark", "True")]
    public async Task WallTimeToExtractCompressedMembersAtEachReaderCount()
    {
        var bsa = await CompressedBsa();
        var workers = Env("SILVER_BSA_WORKERS", 24);

        foreach (var readers in new[] { 1, 2, 4, 8, 24 })
        {
            var best = TimeSpan.MaxValue;
            long peakWorkingSet = 0;
            for (var run = 0; run < 3; run++)
            {
                await using var archive = await new BsaMemberOpener(readers).OpenArchive(_ => Task.FromResult<Stream>(new MemoryStream(bsa)), CancellationToken.None);
                var clock = Stopwatch.StartNew();
                var gate = new SemaphoreSlim(workers);
                await Task.WhenAll(Enumerable.Range(0, Members).Select(async i =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        await using var member = await archive.Open(RelativePath.FromUnsanitizedInput(NameOf(i)), CancellationToken.None);
                        if (member is null) throw new InvalidOperationException($"{NameOf(i)} is not in the archive");
                    }
                    finally { gate.Release(); }
                }));
                best = TimeSpan.FromTicks(Math.Min(best.Ticks, clock.Elapsed.Ticks));
                peakWorkingSet = Math.Max(peakWorkingSet, Process.GetCurrentProcess().WorkingSet64);
            }

            var line = $"BSAREADERS readers={readers} workers={workers} members={Members} memberKB={MemberKb} archiveMB={bsa.Length / 1048576.0:F0} bestWallMs={best.TotalMilliseconds:F0} workingSetMB={peakWorkingSet / 1048576}";
            helper.WriteLine(line);
            if (Environment.GetEnvironmentVariable("SILVER_BENCH_OUT") is { Length: > 0 } outFile) File.AppendAllText(outFile, line + "\n");
        }
    }
}
