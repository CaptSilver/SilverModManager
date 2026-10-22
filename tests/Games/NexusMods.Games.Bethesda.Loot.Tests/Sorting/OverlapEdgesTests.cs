using NexusMods.Games.Bethesda.Loot.Sorting;
using static NexusMods.Games.Bethesda.Loot.Tests.Sorting.SortingFixture;

namespace NexusMods.Games.Bethesda.Loot.Tests.Sorting;

// Ports the add_overlap_edges tests in libloot's sorting/plugins.rs, plus the rules those do not
// reach: skipped plugins, existing edges and cycles.
public class OverlapEdgesTests
{
    private static (PluginsGraph<TestPlugin> Graph, int A, int B) TwoPluginGraph(SortingFixture fixture)
    {
        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.SortingData(PluginA));
        var b = graph.AddNode(fixture.SortingData(PluginB));
        return (graph, a, b);
    }

    private static void ShouldHaveNoEdgeBetween(PluginsGraph<TestPlugin> graph, int a, int b)
    {
        graph.Inner.FindEdge(a, b).Should().BeNull();
        graph.Inner.FindEdge(b, a).Should().BeNull();
    }

    private static void ShouldHaveOnlyEdge(PluginsGraph<TestPlugin> graph, int from, int to, EdgeType edgeType)
    {
        graph.Inner.FindEdge(from, to)!.Value.Weight.Should().Be(edgeType);
        graph.Inner.FindEdge(to, from).Should().BeNull();
    }

    [Fact]
    public void ShouldNotAddEdgesBetweenNonOverlappingPlugins()
    {
        var (graph, a, b) = TwoPluginGraph(WithPlugins(PluginA, PluginB));

        graph.AddOverlapEdges().Should().BeFalse();

        ShouldHaveNoEdgeBetween(graph, a, b);
    }

    [Fact]
    public void ShouldNotAddEdgesBetweenOverlappingPluginsWithEqualOverrideCounts()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginA).OverrideRecordCount = 1;
        fixture.Plugin(PluginA).AddOverlappingRecords(PluginB);
        fixture.Plugin(PluginB).OverrideRecordCount = 1;
        var (graph, a, b) = TwoPluginGraph(fixture);

        graph.AddOverlapEdges();

        ShouldHaveNoEdgeBetween(graph, a, b);
    }

    [Fact]
    public void ShouldAddEdgeBetweenOverlappingPluginsWithUnequalOverrideCounts()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginA).OverrideRecordCount = 2;
        fixture.Plugin(PluginA).AddOverlappingRecords(PluginB);
        fixture.Plugin(PluginB).OverrideRecordCount = 1;
        var (graph, a, b) = TwoPluginGraph(fixture);

        graph.AddOverlapEdges().Should().BeTrue();

        ShouldHaveOnlyEdge(graph, a, b, EdgeType.RecordOverlap);
    }

    [Fact]
    public void ShouldNotAddEdgeBetweenNonOverlappingPluginsWithUnequalOverrideCounts()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginA).OverrideRecordCount = 2;
        fixture.Plugin(PluginB).OverrideRecordCount = 1;
        var (graph, a, b) = TwoPluginGraph(fixture);

        graph.AddOverlapEdges();

        ShouldHaveNoEdgeBetween(graph, a, b);
    }

    [Fact]
    public void ShouldNotAddEdgeBetweenPluginsWithAssetOverlapAndEqualAssetCounts()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginA).AssetCount = 1;
        fixture.Plugin(PluginA).AddOverlappingAssets(PluginB);
        fixture.Plugin(PluginB).AssetCount = 1;
        var (graph, a, b) = TwoPluginGraph(fixture);

        graph.AddOverlapEdges();

        ShouldHaveNoEdgeBetween(graph, a, b);
    }

    [Fact]
    public void ShouldNotAddEdgeBetweenPluginsWithNoAssetOverlapAndUnequalAssetCounts()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginA).AssetCount = 2;
        fixture.Plugin(PluginB).AssetCount = 1;
        var (graph, a, b) = TwoPluginGraph(fixture);

        graph.AddOverlapEdges();

        ShouldHaveNoEdgeBetween(graph, a, b);
    }

    [Fact]
    public void ShouldAddEdgeBetweenPluginsWithAssetOverlapAndUnequalAssetCounts()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginA).AssetCount = 2;
        fixture.Plugin(PluginA).AddOverlappingAssets(PluginB);
        fixture.Plugin(PluginB).AssetCount = 1;
        var (graph, a, b) = TwoPluginGraph(fixture);

        graph.AddOverlapEdges();

        ShouldHaveOnlyEdge(graph, a, b, EdgeType.AssetOverlap);
    }

    [Fact]
    public void ShouldAddEdgeBetweenOverlappingPluginsWithAssetOverlapAndEqualOverrideCountAndUnequalAssetCounts()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginA).AssetCount = 2;
        fixture.Plugin(PluginA).AddOverlappingRecords(PluginB);
        fixture.Plugin(PluginA).AddOverlappingAssets(PluginB);
        fixture.Plugin(PluginB).AssetCount = 1;
        var (graph, a, b) = TwoPluginGraph(fixture);

        graph.AddOverlapEdges();

        ShouldHaveOnlyEdge(graph, a, b, EdgeType.AssetOverlap);
    }

    [Fact]
    public void ShouldAddEdgeBetweenPluginsWithAssetOverlapAndUnequalOverrideCountAndUnequalAssetCounts()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginA).OverrideRecordCount = 1;
        fixture.Plugin(PluginA).AssetCount = 2;
        fixture.Plugin(PluginA).AddOverlappingAssets(PluginB);
        fixture.Plugin(PluginB).OverrideRecordCount = 2;
        fixture.Plugin(PluginB).AssetCount = 1;
        var (graph, a, b) = TwoPluginGraph(fixture);

        graph.AddOverlapEdges();

        ShouldHaveOnlyEdge(graph, a, b, EdgeType.AssetOverlap);
    }

    [Fact]
    public void ShouldChooseRecordOverlapOverAssetOverlap()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginA).OverrideRecordCount = 2;
        fixture.Plugin(PluginA).AssetCount = 1;
        fixture.Plugin(PluginA).AddOverlappingRecords(PluginB);
        fixture.Plugin(PluginA).AddOverlappingAssets(PluginB);
        fixture.Plugin(PluginB).OverrideRecordCount = 1;
        fixture.Plugin(PluginB).AssetCount = 2;
        var (graph, a, b) = TwoPluginGraph(fixture);

        graph.AddOverlapEdges();

        ShouldHaveOnlyEdge(graph, a, b, EdgeType.RecordOverlap);
    }

    [Fact]
    public void ShouldLoadThePluginWithFewerOverridesFirstWhenItIsTheLaterVertex()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginA).OverrideRecordCount = 1;
        fixture.Plugin(PluginB).OverrideRecordCount = 2;
        fixture.Plugin(PluginB).AddOverlappingRecords(PluginA);
        fixture.Plugin(PluginA).AddOverlappingRecords(PluginB);
        var (graph, a, b) = TwoPluginGraph(fixture);

        graph.AddOverlapEdges();

        ShouldHaveOnlyEdge(graph, b, a, EdgeType.RecordOverlap);
    }

    [Fact]
    public void ShouldSkipPluginsWithNoOverridesAndNoAssets()
    {
        // B overlaps A but the outer plugin is the one with nothing, and is skipped as the outer plugin.
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginB).OverrideRecordCount = 2;
        fixture.Plugin(PluginB).AddOverlappingRecords(PluginA);
        fixture.Plugin(PluginA).AddOverlappingRecords(PluginB);
        var (graph, a, b) = TwoPluginGraph(fixture);

        graph.AddOverlapEdges().Should().BeFalse();

        ShouldHaveNoEdgeBetween(graph, a, b);
    }

    [Fact]
    public void ShouldNotAddEdgeBetweenPluginsThatAlreadyHaveADirectEdge()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginA).OverrideRecordCount = 2;
        fixture.Plugin(PluginA).AddOverlappingRecords(PluginB);
        fixture.Plugin(PluginB).OverrideRecordCount = 1;
        var (graph, a, b) = TwoPluginGraph(fixture);
        graph.AddEdge(b, a, EdgeType.Master);

        graph.AddOverlapEdges().Should().BeFalse();

        ShouldHaveOnlyEdge(graph, b, a, EdgeType.Master);
    }

    [Fact]
    public void ShouldSkipAnEdgeThatWouldCreateACycle()
    {
        var fixture = WithPlugins(PluginA, PluginB, PluginC);
        fixture.Plugin(PluginA).OverrideRecordCount = 2;
        fixture.Plugin(PluginA).AddOverlappingRecords(PluginC);
        fixture.Plugin(PluginC).OverrideRecordCount = 1;
        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.SortingData(PluginA));
        var b = graph.AddNode(fixture.SortingData(PluginB));
        var c = graph.AddNode(fixture.SortingData(PluginC));

        // C -> B -> A already, so A -> C would close a cycle.
        graph.AddEdge(c, b, EdgeType.Master);
        graph.AddEdge(b, a, EdgeType.Master);

        graph.AddOverlapEdges().Should().BeFalse();

        graph.Inner.FindEdge(a, c).Should().BeNull();
    }
}
