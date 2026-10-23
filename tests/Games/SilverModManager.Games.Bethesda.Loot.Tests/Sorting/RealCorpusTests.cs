using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NexusMods.Games.Bethesda.Loot.Sorting;
using NexusMods.Games.Bethesda.Loot.Tests.Corpus;

namespace NexusMods.Games.Bethesda.Loot.Tests.Sorting;

// Sorts load orders taken from real installs and compares with what libloot answered for the same
// header-only stubs. The cases are written by tools/loot-oracle/generate_real.py and hold plugin names,
// flags and masters only. Unlike the synthetic corpus these are big and have the shape real lists
// have: Creation Club plugins, patches flagged as masters, thousands of unrelated mods.
public class RealCorpusTests(ITestOutputHelper output)
{
    private static readonly string CorpusDirectory = Path.Combine(AppContext.BaseDirectory, "Corpus");

    /// <summary>The folder holding the real cases, with a trailing separator.</summary>
    internal static string Directory { get; } = Path.Combine(CorpusDirectory, "real") + Path.DirectorySeparatorChar;

    private const int SlowestAcceptableMilliseconds = 60_000;

    public static TheoryData<string> CaseFiles()
    {
        var data = new TheoryData<string>();
        if (!System.IO.Directory.Exists(Directory)) return data;

        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json").Order(StringComparer.Ordinal))
            data.Add(Path.GetFileName(file));
        return data;
    }

    [Fact]
    public void CorpusShouldHoldAWabbajackListWithOverAThousandPluginsAndAnOblivionRemasteredInstall()
    {
        System.IO.Directory.EnumerateFiles(Directory, "*.json").Select(Path.GetFileName)
            .Should().Contain(["tempus_maledictum.json", "oblivion_remastered_instance.json"]);

        var list = CorpusCase.Load(Path.Combine(Directory, "tempus_maledictum.json"));
        list.Plugins.Count.Should().BeGreaterThan(1000);
        list.GameType.Should().Be("SkyrimSE");

        var source = Source(Path.Combine(Directory, "tempus_maledictum.json"));
        source.GetProperty("kind").GetString().Should().Be("wabbajack");
        source.GetProperty("machine_name").GetString().Should().Be("tempus_maledictum");
        source.GetProperty("version").GetString().Should().NotBeNullOrEmpty();
        source.GetProperty("wabbajack_xxhash64").GetString().Should().NotBeNullOrEmpty();

        var instance = CorpusCase.Load(Path.Combine(Directory, "oblivion_remastered_instance.json"));
        instance.GameType.Should().Be("OblivionRemastered");
        instance.Plugins.Should().Contain(p => p.Name == "Oblivion.esm");
    }

    [Theory]
    [MemberData(nameof(CaseFiles))]
    public void ShouldSortTheWayLibLootDid(string file)
    {
        var path = Path.Combine(Directory, file);
        var corpusCase = CorpusCase.Load(path);
        corpusCase.Expected.Order.Should().NotBeNull("a real case is only kept when libloot could sort it");

        var game = CorpusGame.For(corpusCase.GameType);
        var plugins = corpusCase.Plugins.Select(p => game.ToSortingPlugin(p, corpusCase)).ToList();
        var database = corpusCase.BuildDatabase();

        // The first sort pays for JIT and cold caches; the others show what a re-sort costs.
        var timings = new List<double>();
        SortResult? result = null;
        for (var run = 0; run < 3; run++)
        {
            var sw = Stopwatch.StartNew();
            result = PluginSorter.Sort<ISortingPlugin>(plugins, database, game.EarlyLoaders);
            sw.Stop();
            timings.Add(sw.Elapsed.TotalMilliseconds);
        }

        var oracleMs = OracleMilliseconds(path);
        output.WriteLine(
            $"{corpusCase.Plugins.Count} plugins: C# sort {string.Join(", ", timings.Select(t => $"{t:F0} ms"))} (first is cold)" +
            $"; libloot sort_plugins {(oracleMs is { } ms ? $"{ms} ms when the case was generated" : "not recorded")}");

        // Recording edges costs time, so only a mismatch pays for a sort that keeps them.
        var expected = corpusCase.Expected.Order!;
        if (!result!.Order.SequenceEqual(expected))
        {
            var edges = PluginSorter.Sort<ISortingPlugin>(plugins, database, game.EarlyLoaders, recordEdges: true).Edges;
            Explain(expected, result.Order, edges).Should().BeNull();
        }
        result.UnlinkedNeighbours.Should().BeEmpty();
        timings.Max().Should().BeLessThan(SlowestAcceptableMilliseconds);
    }

    [Fact]
    public void ExplainShouldNameTheFirstDifferingIndexAndTheEdgesOfBothPlugins()
    {
        SortEdge[] edges =
        [
            new("Base.esm", "A.esp", EdgeType.Master),
            new("B.esp", "A.esp", EdgeType.MasterlistLoadAfter),
            new("Base.esm", "B.esp", EdgeType.Master),
            new("B.esp", "C.esp", EdgeType.TieBreak),
            new("C.esp", "D.esp", EdgeType.TieBreak),
        ];

        var message = Explain(["Base.esm", "B.esp", "A.esp", "C.esp"], ["Base.esm", "A.esp", "B.esp", "C.esp"], edges);

        message.Should().NotBeNull();
        message.Should().Contain("index 1").And.Contain("expected B.esp").And.Contain("got A.esp");
        message.Should().Contain("B.esp -> A.esp (Masterlist Load After)");
        message.Should().Contain("Base.esm -> A.esp (Master)");
        message.Should().Contain("B.esp -> C.esp (Tie Break)").And.NotContain("C.esp -> D.esp");
    }

    [Fact]
    public void ExplainShouldReturnNullForEqualOrders()
    {
        Explain(["A.esp", "B.esp"], ["A.esp", "B.esp"], []).Should().BeNull();
    }

    [Fact]
    public void ExplainShouldReportAMissingPluginWhenTheLengthsDiffer()
    {
        Explain(["A.esp", "B.esp"], ["A.esp"], []).Should().Contain("index 1").And.Contain("expected B.esp");
    }

    /// <summary>
    /// Null when the orders are equal; otherwise the first index they differ at, and the edges that
    /// involve the two plugins that disagree, which is what placed each of them.
    /// </summary>
    internal static string? Explain(IReadOnlyList<string> expected, IReadOnlyList<string> actual, IReadOnlyList<SortEdge> edges)
    {
        var shared = Math.Min(expected.Count, actual.Count);
        var index = 0;
        while (index < shared && expected[index] == actual[index]) index++;
        if (index == expected.Count && index == actual.Count) return null;

        var want = index < expected.Count ? expected[index] : "(nothing)";
        var got = index < actual.Count ? actual[index] : "(nothing)";
        var text = new StringBuilder();
        text.AppendLine($"orders first differ at index {index}: expected {want}, got {got} ({expected.Count} expected, {actual.Count} actual)");
        foreach (var name in new[] { want, got }.Distinct())
        {
            var touching = edges.Where(e => e.From == name || e.To == name).ToList();
            text.AppendLine($"edges on {name} ({touching.Count}):");
            foreach (var edge in touching.Take(40))
                text.AppendLine($"  {edge.From} -> {edge.To} ({edge.Type.ToDisplayString()})");
            if (touching.Count > 40) text.AppendLine($"  ... {touching.Count - 40} more");
        }

        return text.ToString();
    }

    private static JsonElement Source(string path)
    {
        using var document = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        return document.RootElement.GetProperty("source").Clone();
    }

    private static long? OracleMilliseconds(string path)
    {
        using var document = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        return document.RootElement.TryGetProperty("oracle_sort_ms", out var ms) ? ms.GetInt64() : null;
    }
}
