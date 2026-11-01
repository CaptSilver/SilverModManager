using System.Diagnostics;
using SilverModManager.Sdk.Hashes;
using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack.Tests.Install;

/// <summary>
/// Installs a synthetic list with real payloads (archive members, patches, textures, inline files, built archives) and
/// prints peak memory, wall time and the number of database commits. Slow, so the suite filters the Benchmark trait out;
/// run it with <c>-p:XunitArgs="--filter-trait Benchmark=True"</c>. SILVER_BENCH_SCALE multiplies the list size.
/// </summary>
[Trait("Benchmark", "True")]
public class InstallModlistBenchmarkTests(ITestOutputHelper helper) : InstallTestBase<InstallModlistBenchmarkTests>(helper)
{
    private static int Scale => int.TryParse(Environment.GetEnvironmentVariable("SILVER_BENCH_SCALE"), out var scale) ? Math.Max(1, scale) : 1;

    private const int MemberSize = 16 * 1024;

    private static byte[] Payload(int seed, int length)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public async Task InstallingAMixedListReportsPeakMemoryAndWallTime()
    {
        var scale = Scale;
        var plain = 3000 * scale;
        var patches = 300 * scale;
        var textures = 300 * scale;
        var inlines = 500 * scale;
        var archives = 5 * scale;
        var archiveInputs = 100;

        var list = new ListBuilder();

        // Plain members and patch bases live in a few zips.
        const int PerZip = 1000;
        var zips = new List<(ArchiveEntry Entry, List<(string Name, int Seed, ulong Hash)> Files)>();
        for (var z = 0; z * PerZip < plain + patches; z++)
        {
            var files = new Dictionary<string, byte[]>();
            var named = new List<(string, int, ulong)>();
            for (var i = 0; i < PerZip && z * PerZip + i < plain + patches; i++)
            {
                var name = $"files/f{z * PerZip + i}.bin";
                files[name] = Payload(z * PerZip + i, MemberSize);
                named.Add((name, z * PerZip + i, 0));
            }
            var entry = await AddZip($"z{z}.zip", files);
            list.Archives.Add(entry);
            zips.Add((entry, named));
        }

        var n = 0;
        foreach (var (entry, files) in zips)
        {
            foreach (var (name, seed, _) in files)
            {
                var mod = $"M{n % 60}";
                var bytes = Payload(seed, MemberSize);
                if (n < plain)
                {
                    list.Directives.Add(list.Member($"mods/{mod}/data/{name}", entry, name, bytes));
                }
                else
                {
                    var result = Payload(1_000_000 + n, MemberSize);
                    Array.Copy(bytes, result, MemberSize / 2);
                    list.Directives.Add(list.Patched($"mods/{mod}/data/{name}", entry, name, bytes, result));
                }
                n++;
            }
        }

        var dds = await AddZip("tex.zip", Enumerable.Range(0, textures).ToDictionary(i => $"t{i}.dds", i => Bc1Dds(256)));
        list.Archives.Add(dds);
        for (var i = 0; i < textures; i++)
        {
            list.Directives.Add(new TransformedTexture(P($"mods/T{i % 20}/textures/t{i}.dds"), Xx64.From(7), 1,
                new ArchiveHashPath(dds.XxHash64, [P($"t{i}.dds")]), new ImageState(256, 256, 1, "BC7_UNORM")));
        }

        for (var i = 0; i < inlines; i++)
            list.Directives.Add(list.Inline($"mods/I{i % 10}/data/i{i}.txt", Payload(2_000_000 + i, 4096)));

        for (var a = 0; a < archives; a++)
        {
            var paths = new List<string>();
            for (var i = 0; i < archiveInputs; i++)
            {
                var path = $"meshes/a{a}/m{i}.nif";
                paths.Add(path);
                list.Directives.Add(list.Inline($"TEMP_BSA_FILES/pack{a}/{path}", Payload(3_000_000 + a * 1000 + i, 16 * 1024)));
            }
            list.Directives.Add(list.Bsa($"mods/B{a}/pack{a}.bsa", $"pack{a}", paths.ToArray()));
        }

        var loadout = await CreateLoadout();
        var modlist = await AddModlist(list.ToList(), list.Blobs);

        var commits = 0;
        using var revisions = Connection.Revisions.Subscribe(_ => Interlocked.Increment(ref commits));

        GC.Collect();
        var startManaged = GC.GetTotalMemory(true);
        var process = Process.GetCurrentProcess();
        var startWorkingSet = process.WorkingSet64;
        long peakManaged = 0, peakWorkingSet = 0;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(25));
            while (!stop.IsCancellationRequested)
            {
                peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(false));
                process.Refresh();
                peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
                try { await timer.WaitForNextTickAsync(stop.Token); } catch (OperationCanceledException) { }
            }
        });

        var watch = Stopwatch.StartNew();
        var (_, group) = await Install(loadout, modlist, new InstallOptions(false, new HashSet<RelativePath>()));
        watch.Stop();
        await stop.CancelAsync();
        await sampler;

        var directives = list.Directives.Count;
        var line = ($"BENCH directives={directives} wall={watch.Elapsed.TotalSeconds:F1}s commits={commits} " +
                         $"startManagedMB={startManaged / 1048576} peakManagedMB={peakManaged / 1048576} peakWorkingSetMB={peakWorkingSet / 1048576} startWorkingSetMB={startWorkingSet / 1048576} " +
                         $"gcPeakHeapMB={GC.GetGCMemoryInfo().HeapSizeBytes / 1048576} gen2={GC.CollectionCount(2)}");
        helper.WriteLine(line);
        if (Environment.GetEnvironmentVariable("SILVER_BENCH_OUT") is { Length: > 0 } outFile) File.AppendAllText(outFile, line + "\n");
        SkipsOf(group).Should().BeEmpty();
    }
}
