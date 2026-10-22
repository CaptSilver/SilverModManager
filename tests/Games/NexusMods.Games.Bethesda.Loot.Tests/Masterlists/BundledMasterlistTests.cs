using System.Diagnostics;
using System.Text.RegularExpressions;
using NexusMods.Games.Bethesda.Loot.Database;
using NexusMods.Games.Bethesda.Loot.Masterlists;
using NexusMods.Games.Bethesda.Loot.Metadata;
using NexusMods.Games.Bethesda.Loot.Sorting;
using YamlDotNet.RepresentationModel;

namespace NexusMods.Games.Bethesda.Loot.Tests.Masterlists;

/// <summary>
/// Runs every bundled masterlist through the parts of the port that read metadata, so an upstream
/// refresh that uses syntax the port cannot handle fails here and not in a user's sort.
/// </summary>
public class BundledMasterlistTests(ITestOutputHelper output)
{
    private static readonly BundledMasterlistStore Store = new();

    public static IEnumerable<object[]> Repositories =>
        LootGameExtensions.BundledRepositories.Select(r => new object[] { r });

    [Fact]
    public void EveryBundledRepositoryHasAMasterlistAndALockEntry()
    {
        foreach (var repo in LootGameExtensions.BundledRepositories)
        {
            Store.MasterlistText(repo).Should().NotBeNullOrWhiteSpace(repo);
            Store.RetrievedOn(repo).Should().BeAfter(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), repo);
        }
        Store.PreludeText().Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [MemberData(nameof(Repositories))]
    public void MasterlistLoadsWithThePrelude(string repo)
    {
        var sw = Stopwatch.StartNew();
        var document = MetadataDocument.LoadWithPrelude(Store.MasterlistText(repo), Store.PreludeText());
        sw.Stop();

        output.WriteLine($"{repo}: {document.Plugins.Count} plugins, {document.RegexPlugins.Count} regex plugins, loaded in {sw.ElapsedMilliseconds} ms");
        document.Groups.Should().NotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Repositories))]
    public void EveryRegexPluginNameCompilesAndMatchesALiteralEntryWhereOneExists(string repo)
    {
        var text = Store.MasterlistText(repo);
        var literals = new List<string>();
        var regexes = new List<(string Name, int Line)>();
        foreach (var (name, line) in PluginNames(text))
        {
            if (PluginName.IsRegexName(name)) regexes.Add((name, line));
            else literals.Add(name);
        }

        var compileFailures = new List<string>();
        var unmatched = new List<string>();
        foreach (var (name, line) in regexes)
        {
            PluginName compiled;
            try
            {
                compiled = new PluginName(name);
            }
            catch (InvalidPluginNameException e)
            {
                compileFailures.Add($"{repo}/masterlist.yaml:{line}: {name} ({e.Message})");
                continue;
            }

            if (literals.Count > 0 && !literals.Any(compiled.Matches))
                unmatched.Add($"{repo}/masterlist.yaml:{line}: {name}");
        }

        output.WriteLine($"{repo}: {regexes.Count} regex names, {literals.Count} literal names, {unmatched.Count} matching no literal entry");
        foreach (var u in unmatched) output.WriteLine("no literal match: " + u);

        compileFailures.Should().BeEmpty("every regex name must compile under ECMAScript | IgnoreCase:\n" + string.Join("\n", compileFailures));
    }

    [Theory]
    [MemberData(nameof(Repositories))]
    public void EveryConditionStringParses(string repo) => AssertConditionsParse($"{repo}/masterlist.yaml", Store.MasterlistText(repo));

    [Fact]
    public void EveryPreludeConditionStringParses() => AssertConditionsParse("prelude.yaml", Store.PreludeText());

    [Theory]
    [MemberData(nameof(Repositories))]
    public void GroupsGraphBuildsWithoutACycle(string repo)
    {
        var document = MetadataDocument.LoadWithPrelude(Store.MasterlistText(repo), Store.PreludeText());

        var graph = GroupsGraph.Build(document.Groups, []);

        graph.NodeCount.Should().BeGreaterThan(0);
        graph.SortedGroupNodes().Should().HaveCount(graph.NodeCount);
    }

    private static void AssertConditionsParse(string label, string yaml)
    {
        var failures = new List<string>();
        var count = 0;
        foreach (var (condition, line) in Conditions(yaml))
        {
            count++;
            try
            {
                ConditionExpression.Parse(condition);
            }
            catch (ConditionParseException e)
            {
                failures.Add($"{label}:{line}: {condition} ({e.Message})");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} of {count} conditions failed to parse:\n" + string.Join("\n", failures));
    }

    private static IEnumerable<(string Condition, int Line)> Conditions(string yaml)
    {
        foreach (var (key, value, line) in Scalars(yaml))
        {
            if (key == "condition") yield return (value, line);
        }
    }

    // Plugin names are the `name` of each entry under the top-level `plugins` sequence; the same key
    // also names groups, bash tags and locations elsewhere.
    private static IEnumerable<(string Name, int Line)> PluginNames(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root) yield break;
        foreach (var (key, value) in root.Children)
        {
            if (key is not YamlScalarNode { Value: "plugins" } || value is not YamlSequenceNode plugins) continue;
            foreach (var entry in plugins.Children.OfType<YamlMappingNode>())
            {
                if (entry.Children.TryGetValue(new YamlScalarNode("name"), out var name) && name is YamlScalarNode { Value: { } text })
                    yield return (text, (int)name.Start.Line);
            }
        }
    }

    private static IEnumerable<(string Key, string Value, int Line)> Scalars(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var pending = new Stack<YamlNode>(stream.Documents.Select(d => d.RootNode));
        while (pending.Count > 0)
        {
            switch (pending.Pop())
            {
                case YamlMappingNode map:
                    foreach (var (key, value) in map.Children)
                    {
                        if (key is YamlScalarNode { Value: { } k } && value is YamlScalarNode { Value: { } v })
                            yield return (k, v, (int)value.Start.Line);
                        else
                            pending.Push(value);
                    }
                    break;
                case YamlSequenceNode seq:
                    foreach (var child in seq.Children) pending.Push(child);
                    break;
            }
        }
    }
}
