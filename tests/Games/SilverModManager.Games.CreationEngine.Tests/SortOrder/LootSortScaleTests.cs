using System.Diagnostics;
using FluentAssertions;
using SilverModManager.Games.Bethesda.Archives;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.Bethesda.Loot.Metadata;
using SilverModManager.Games.Bethesda.Loot.Sorting;
using SilverModManager.Games.CreationEngine.Loot;
using SilverModManager.Games.CreationEngine.SortOrder;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Games.CreationEngine.Tests.SortOrder;

/// <summary>
/// How the sort behaves on a load order of a few thousand plugins that override and load a lot of the same things. The time bound is loose
/// on purpose: it catches a return to searching for a path between every pair, not a slower machine. It is a
/// benchmark, left out of the default run because wall-clock time on a busy machine proves nothing.
/// </summary>
[Trait("Benchmark", "True")]
public class LootSortScaleTests(ITestOutputHelper output)
{
    private const int PluginCount = 3000;

    /// <summary>
    /// Plugins that mostly touch a popular few records and archive files and a long tail of rare ones, the way
    /// a big load order's patches and overhauls do. Seeded, so every run sorts the same thing.
    /// </summary>
    internal static List<PluginSortingData<LootSortPlugin>> LoadOrder(int count, int seed)
    {
        var random = new Random(seed);
        var plugins = new List<PluginSortingData<LootSortPlugin>>(count);
        for (var i = 0; i < count; i++)
        {
            var name = $"Plugin{i:D4}.esp";
            var formIds = Enumerable.Range(0, random.Next(20, 400)).Select(_ => PickIndex(random, 100_000)).ToArray();
            var files = Enumerable.Range(0, random.Next(0, 300)).Select(_ => $"meshes/m{PickIndex(random, 50_000)}.nif");

            var data = new PluginLoadoutData(name, default(FileIdentity), true, "Mod", null, false, ["Skyrim.esm"]);
            var plugin = new LootSortPlugin(data, PluginRecordIds.Resolve(name, data.Masters, formIds), ArchiveAssetSet.FromPaths(files));
            plugins.Add(new PluginSortingData<LootSortPlugin>(plugin, null, null, i));
        }

        return plugins;
    }

    // Squaring a uniform draw crowds the picks towards low numbers.
    private static uint PickIndex(Random random, int range) => (uint)(Math.Pow(random.NextDouble(), 2) * range) + 0x800;

    [Fact]
    public void SortsThreeThousandOverlappingPluginsInSecondsNotMinutes()
    {
        var plugins = LoadOrder(PluginCount, seed: 7);
        var groups = GroupsGraph.Build([new Group("default")], []);

        var clock = Stopwatch.StartNew();
        var result = PluginSorter.Sort(plugins, groups, []);
        clock.Stop();

        output.WriteLine($"Sorting {PluginCount} plugins took {clock.Elapsed.TotalSeconds:F2}s, overlap edges added: {result.OverlapEdgesAdded}");
        result.Order.Should().HaveCount(PluginCount);
        result.OverlapEdgesAdded.Should().BeTrue();
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(25));
    }
}
