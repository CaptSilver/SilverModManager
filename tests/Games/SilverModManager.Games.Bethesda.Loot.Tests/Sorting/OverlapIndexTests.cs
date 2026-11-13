using SilverModManager.Games.Bethesda.Loot.Sorting;

namespace SilverModManager.Games.Bethesda.Loot.Tests.Sorting;

// The index only decides which pairs AddOverlapEdges looks at; the edges, and the order they were added in, must be
// the ones it adds by looking at every pair.
public class OverlapIndexTests
{
    private sealed class KeyedPlugin(string name, ulong[] records, ulong[] assets, int overrides) : ISortingPlugin, IOverlapKeys
    {
        public string Name { get; } = name;
        public bool IsMaster => false;
        public bool IsBlueprintPlugin => false;
        public IReadOnlyList<string> Masters => [];
        public int OverrideRecordCount { get; } = overrides;
        public int AssetCount => assets.Length;
        public IReadOnlyList<ulong> RecordKeys => records;
        public IReadOnlyList<ulong> AssetKeys => assets;
        public bool DoRecordsOverlap(ISortingPlugin other) => other is KeyedPlugin o && records.Intersect(o.RecordKeys).Any();
        public bool DoAssetsOverlap(ISortingPlugin other) => other is KeyedPlugin o && assets.Intersect(o.AssetKeys).Any();
    }

    private static PluginsGraph<KeyedPlugin> Graph(int count, int seed, int keyRange)
    {
        var random = new Random(seed);
        var graph = new PluginsGraph<KeyedPlugin>();
        for (var i = 0; i < count; i++)
        {
            ulong[] Pick(int max) => [.. Enumerable.Range(0, random.Next(0, max)).Select(_ => (ulong)random.Next(keyRange)).Distinct()];
            var records = Pick(12);
            var plugin = new KeyedPlugin($"P{i:D3}.esp", records, Pick(12), random.Next(0, records.Length + 1));
            graph.AddNode(new PluginSortingData<KeyedPlugin>(plugin, null, null, i));
        }

        // A few edges from earlier rules, so the pairs they join are skipped and some paths already exist.
        for (var i = 0; i < count / 4; i++)
        {
            var from = random.Next(count);
            var to = random.Next(count);
            if (from < to) graph.AddEdge(from, to, EdgeType.Master);
        }

        return graph;
    }

    private static List<(int From, int To, EdgeType Type)> EdgesOf(PluginsGraph<KeyedPlugin> graph) =>
        [.. Enumerable.Range(0, graph.NodeCount).SelectMany(n => graph.Inner.OutEdgesOldestFirst(n)).Select(e => (e.Source, e.Target, e.Weight))];

    [Theory]
    [InlineData(1, 300, 400)]
    [InlineData(2, 300, 60)]
    [InlineData(3, 120, 25)]
    [InlineData(4, 500, 5000)]
    public void AddsTheEdgesThatLookingAtEveryPairAdds(int seed, int count, int keyRange)
    {
        var pairwise = Graph(count, seed, keyRange);
        var indexed = Graph(count, seed, keyRange);

        var pairwiseAdded = pairwise.AddOverlapEdges(useIndex: false);
        var indexedAdded = indexed.AddOverlapEdges(useIndex: true);

        pairwiseAdded.Should().BeTrue("the fixture has to overlap for this to prove anything");
        indexedAdded.Should().Be(pairwiseAdded);
        EdgesOf(indexed).Should().Equal(EdgesOf(pairwise));
    }

    [Fact]
    public void FallsBackToEveryPairWhenAPluginOffersNoKeys()
    {
        var fixture = SortingFixture.WithPlugins(SortingFixture.PluginA, SortingFixture.PluginB);
        fixture.Plugin(SortingFixture.PluginA).OverrideRecordCount = 2;
        fixture.Plugin(SortingFixture.PluginA).AddOverlappingRecords(SortingFixture.PluginB);
        fixture.Plugin(SortingFixture.PluginB).OverrideRecordCount = 1;
        var graph = new PluginsGraph<TestPlugin>();
        graph.AddNode(fixture.SortingData(SortingFixture.PluginA));
        graph.AddNode(fixture.SortingData(SortingFixture.PluginB));

        graph.AddOverlapEdges().Should().BeTrue();
    }
}
