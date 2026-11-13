using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.Bethesda.Loot.Database;
using SilverModManager.Games.Bethesda.Loot.Masterlists;
using SilverModManager.Games.Bethesda.Loot.Sorting;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Games.CreationEngine.Loot;
using SilverModManager.Games.CreationEngine.SortOrder;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Games.CreationEngine.Tests.Loot;

/// <summary>
/// Sorts the plugins of a real installed game, records and archives included, and compares the order
/// with the one libloot gave for the same files. Not part of the suite: it only runs when all the
/// variables are set, and it only reads the game folder (see tools/loot-oracle/README.md,
/// "Comparing with an installed game").
/// </summary>
public class InstalledLoadOrderComparisonTests(ITestOutputHelper output)
{
    private const string GameVariable = "LOOT_ORACLE_INSTALLED_GAME";
    private const string StrippedVariable = "LOOT_ORACLE_STRIP_DIR";
    private const string ExpectedVariable = "LOOT_ORACLE_INSTALLED_EXPECTED";

    [Fact]
    public async Task ShouldSortTheInstalledPluginsTheWayLibLootDid()
    {
        var gameFolder = Environment.GetEnvironmentVariable(GameVariable);
        var strippedDirectory = Environment.GetEnvironmentVariable(StrippedVariable);
        var expectedFile = Environment.GetEnvironmentVariable(ExpectedVariable);
        Assert.SkipWhen(
            string.IsNullOrEmpty(gameFolder) || string.IsNullOrEmpty(strippedDirectory) || string.IsNullOrEmpty(expectedFile),
            $"{GameVariable}, {StrippedVariable} and {ExpectedVariable} are not all set");

        using var document = JsonDocument.Parse(File.ReadAllText(expectedFile!));
        var expected = document.RootElement;
        expected.TryGetProperty("order", out var expectedOrder).Should().BeTrue("libloot has to have sorted the plugins: {0}",
            expected.TryGetProperty("error", out var error) ? error.GetString() : "");
        var names = expected.GetProperty("input").EnumerateArray().Select(e => e.GetString()!).ToList();
        var game = Enum.Parse<LootGame>(expected.GetProperty("game_type").GetString()!);

        var data = Path.Combine(gameFolder!, "Data");
        var identities = new Dictionary<FileIdentity, string>();
        var streams = new FileStreams(identities);
        var creationEngineGame = CreationEngineGame(game, streams);
        var format = creationEngineGame.HeaderFormat;
        var earlyLoaders = creationEngineGame.HardcodedPlugins;
        FileIdentity Identify(string path)
        {
            var identity = FileIdentity.XxHash3(Encoding.UTF8.GetBytes(path).xxHash3());
            identities[identity] = path;
            return identity;
        }

        var loadout = names.Select(name => Read(creationEngineGame, name, Path.Combine(data, name), Identify)).ToList();
        var archives = Directory.EnumerateFiles(data, "*" + AssociatedArchives.ArchiveExtension(game))
            .Select(path => (Name: Path.GetFileName(path), Identity: Identify(path)))
            .ToList();

        var reader = new LootOverlapReader(new PluginOverlapCache(streams, format, NullLogger.Instance), format, NullLogger.Instance);
        var stopwatch = Stopwatch.StartNew();
        var overlap = await reader.Read(game, loadout, archives, CancellationToken.None);
        var readMs = stopwatch.ElapsedMilliseconds;
        overlap.Complete.Should().BeTrue("every plugin's records must be readable for the comparison to mean anything");

        var plugins = loadout
            .Select(d => new LootSortPlugin(d, overlap.Plugins[d.Name].Records, overlap.Plugins[d.Name].Assets))
            .ToList();
        var database = new LootDatabase();
        database.LoadMasterlistWithPrelude(
            File.ReadAllText(Path.Combine(strippedDirectory!, $"{game.MasterlistRepository()}.masterlist.yaml")),
            File.ReadAllText(Path.Combine(strippedDirectory!, "prelude.yaml")));

        var timings = new List<long>();
        SortResult? result = null;
        for (var run = 0; run < 3; run++)
        {
            stopwatch.Restart();
            result = PluginSorter.Sort(plugins, database, earlyLoaders, overlapInputsRead: true);
            timings.Add(stopwatch.ElapsedMilliseconds);
        }

        output.WriteLine(
            $"{names.Count} plugins, {archives.Count} archives. C#: read records and archives {readMs} ms, sort {string.Join(", ", timings.Select(t => $"{t} ms"))} (first is cold)" +
            $"; libloot: load_plugins {expected.GetProperty("load_plugins_ms").GetInt64()} ms, sort_plugins {expected.GetProperty("sort_plugins_ms").GetInt64()} ms");
        output.WriteLine($"any overlap edge added: {(result!.OverlapEdgesAdded ? "yes" : "no")}; plugins with records: {plugins.Count(p => p.OverrideRecordCount > 0)}; with assets: {plugins.Count(p => p.AssetCount > 0)}");

        // Shows whether the overlap rules decided anything here: the same plugins without records or archives.
        var withoutOverlap = PluginSorter.Sort(loadout.Select(d => new LootSortPlugin(d)).ToList(), database, earlyLoaders).Order;
        var edgeCounts = PluginSorter.Sort(plugins, database, earlyLoaders, recordEdges: true, overlapInputsRead: true).Edges
            .GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.Count());
        output.WriteLine($"positions that change without overlap data: {withoutOverlap.Zip(result.Order).Count(pair => pair.First != pair.Second)}; " +
            $"record overlap edges {edgeCounts.GetValueOrDefault(EdgeType.RecordOverlap)}, asset overlap edges {edgeCounts.GetValueOrDefault(EdgeType.AssetOverlap)}");

        var libloot = expectedOrder.EnumerateArray().Select(e => e.GetString()!).ToList();
        if (!result.Order.SequenceEqual(libloot))
        {
            var edges = PluginSorter.Sort(plugins, database, earlyLoaders, recordEdges: true, overlapInputsRead: true).Edges;
            Explain(libloot, result.Order, edges).Should().BeNull();
        }

        result.UnlinkedNeighbours.Should().BeEmpty();
    }

    [Fact]
    public void ExplainShouldNameTheFirstDifferingIndexAndTheEdgesOfBothPlugins()
    {
        SortEdge[] edges = [new("B.esp", "A.esp", EdgeType.RecordOverlap), new("Base.esm", "A.esp", EdgeType.Master), new("C.esp", "D.esp", EdgeType.TieBreak)];

        var message = Explain(["Base.esm", "B.esp", "A.esp"], ["Base.esm", "A.esp", "B.esp"], edges);

        message.Should().Contain("index 1").And.Contain("expected B.esp").And.Contain("got A.esp");
        message.Should().Contain("B.esp -> A.esp").And.Contain("Base.esm -> A.esp").And.NotContain("C.esp -> D.esp");
    }

    [Fact]
    public void ExplainShouldReturnNullForEqualOrders() => Explain(["A.esp", "B.esp"], ["A.esp", "B.esp"], []).Should().BeNull();

    /// <summary>
    /// The app's own game, so the early loaders, header format and master rules are the ones a real sort uses. Only
    /// the hardcoded early loaders are taken: a game with a Creation Club list is refused rather than sorted without it.
    /// </summary>
    private static ICreationEngineGame CreationEngineGame(LootGame game, IStreamSourceDispatcher streams)
    {
        var provider = new ServiceCollection().AddSingleton(streams).BuildServiceProvider();
        ICreationEngineGame found = game switch
        {
            LootGame.SkyrimVR => new CreationEngine.SkyrimVR.SkyrimVR(provider),
            _ => throw new NotSupportedException($"The comparison does not know how to build {game}"),
        };
        if (found.CreationClubListFile is not null)
            throw new NotSupportedException($"{game} has a Creation Club list, which the comparison does not read");
        return found;
    }

    private static PluginLoadoutData Read(ICreationEngineGame game, string name, string path, Func<string, FileIdentity> identify)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = PluginHeaderReader.Read(stream, game.HeaderFormat, name);
        // MasterFlagIgnored needs an installation; no game the comparison builds overrides it.
        return new PluginLoadoutData(name, identify(path), true, "Installed", null, PluginFlags.IsMaster(header, game.MasterByExtension, false), header.Masters)
        {
            IsBlueprint = game.IsBlueprint(header),
        };
    }

    /// <summary>Null when the orders are equal; otherwise where they first differ and the edges on both plugins.</summary>
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
            foreach (var edge in touching.Take(40)) text.AppendLine($"  {edge.From} -> {edge.To} ({edge.Type.ToDisplayString()})");
        }

        return text.ToString();
    }

    private sealed class FileStreams(Dictionary<FileIdentity, string> paths) : IStreamSourceDispatcher
    {
        public ValueTask<Stream?> OpenAsync(FileIdentity identity, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Stream?>(paths.TryGetValue(identity, out var path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null);

        public bool Exists(FileIdentity identity) => paths.ContainsKey(identity);
    }
}
