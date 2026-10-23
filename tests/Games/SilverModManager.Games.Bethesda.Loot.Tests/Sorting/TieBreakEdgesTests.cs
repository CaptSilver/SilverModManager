using NexusMods.Games.Bethesda.Loot.Sorting;
using static NexusMods.Games.Bethesda.Loot.Tests.Sorting.SortingFixture;

namespace NexusMods.Games.Bethesda.Loot.Tests.Sorting;

// Ports the add_tie_break_edges tests in libloot's sorting/plugins.rs, plus tests of the path search
// the tie-break edges are placed by.
public class TieBreakEdgesTests
{
    private const string PluginF = "F.esp";
    private const string PluginG = "G.esp";
    private const string PluginH = "H.esp";
    private const string PluginI = "I.esp";
    private const string PluginJ = "J.esp";

    private static PluginsGraph<TestPlugin> GraphOf(SortingFixture fixture, params string[] names)
    {
        var graph = new PluginsGraph<TestPlugin>();
        foreach (var name in names) graph.AddNode(fixture.SortingData(name));
        return graph;
    }

    private static string[] TieBreakAndSort(PluginsGraph<TestPlugin> graph)
    {
        graph.AddTieBreakEdges();

        var sorted = graph.TopologicalSort();
        graph.CheckPathIsHamiltonian(sorted).Should().BeNull();
        return sorted.Select(n => graph[n].Name).ToArray();
    }

    [Fact]
    public void ShouldNotErrorOnAGraphWithOneNode()
    {
        var graph = GraphOf(WithPlugins(PluginA, PluginB), PluginA);

        var act = graph.AddTieBreakEdges;

        act.Should().NotThrow();
    }

    [Fact]
    public void ShouldResultInASortOrderEqualToVertexCreationOrderIfThereAreNoOtherEdges()
    {
        var names = new[] { PluginA, PluginB, PluginC, PluginD, PluginE };

        var sorted = TieBreakAndSort(GraphOf(WithPlugins(names), names));

        sorted.Should().Equal(names);
    }

    [Fact]
    public void ShouldPinPathsThatPreventTheVertexCreationOrderFromBeingUsed()
    {
        var names = new[] { PluginA, PluginB, PluginC, PluginD, PluginE, PluginF, PluginG, PluginH, PluginI, PluginJ };
        var graph = GraphOf(WithPlugins(names), names);
        int N(string name) => Array.IndexOf(names, name);

        // A path g -> h -> i -> f, and also g -> d and i -> e.
        graph.AddEdge(N(PluginG), N(PluginH), EdgeType.RecordOverlap);
        graph.AddEdge(N(PluginH), N(PluginI), EdgeType.RecordOverlap);
        graph.AddEdge(N(PluginI), N(PluginF), EdgeType.RecordOverlap);
        graph.AddEdge(N(PluginG), N(PluginD), EdgeType.RecordOverlap);
        graph.AddEdge(N(PluginI), N(PluginE), EdgeType.RecordOverlap);

        var sorted = TieBreakAndSort(graph);

        sorted.Should().Equal(PluginA, PluginB, PluginC, PluginG, PluginD, PluginH, PluginI, PluginE, PluginF, PluginJ);
    }

    [Fact]
    public void ShouldPrefixPathToNewLoadOrderIfTheFirstPairOfNodesCannotBeUsedInCreationOrder()
    {
        var names = new[] { PluginA, PluginB, PluginC, PluginD, PluginE, PluginF, PluginG, PluginH, PluginI, PluginJ };
        var graph = GraphOf(WithPlugins(names), names);
        int N(string name) => Array.IndexOf(names, name);

        // A path b -> c -> d -> a.
        graph.AddEdge(N(PluginB), N(PluginC), EdgeType.RecordOverlap);
        graph.AddEdge(N(PluginC), N(PluginD), EdgeType.RecordOverlap);
        graph.AddEdge(N(PluginD), N(PluginA), EdgeType.RecordOverlap);

        var sorted = TieBreakAndSort(graph);

        sorted.Should().Equal(PluginB, PluginC, PluginD, PluginA, PluginE, PluginF, PluginG, PluginH, PluginI, PluginJ);
    }

    [Fact]
    public void FindPathShouldTakeThePathThroughTheNewestEdgeWhenTwoAreEquallyShort()
    {
        // S -> X -> T and S -> Y -> T. Petgraph yields the most recently added edge first, so the
        // forward search queues Y before X, the reverse search does the same, and the two meet at Y.
        var names = new[] { "S.esp", "X.esp", "Y.esp", "T.esp" };
        var graph = GraphOf(WithPlugins(names), names);

        graph.AddEdge(0, 1, EdgeType.Master);
        graph.AddEdge(0, 2, EdgeType.Master);
        graph.AddEdge(1, 3, EdgeType.Master);
        graph.AddEdge(2, 3, EdgeType.Master);

        graph.FindPath(0, 3).Should().Equal(0, 2, 3);
    }

    [Fact]
    public void FindPathShouldReturnNullIfThereIsNoPath()
    {
        var names = new[] { PluginA, PluginB };
        var graph = GraphOf(WithPlugins(names), names);

        graph.FindPath(0, 1).Should().BeNull();
    }

    [Fact]
    public void ShouldPinTheNodesOfTheTiedPathPetgraphsOrderYields()
    {
        // Load order A, B, C, D. Both B -> C -> A and B -> D -> A are paths from B to A. Petgraph's
        // newest-first order picks the one through D (the later edge), so the pinned order starts
        // B, D, A. C is then pinned after D, because C has no path to D but does have one to A:
        // the result is B, D, C, A. Taking the path through C would give B, C, D, A.
        var names = new[] { PluginA, PluginB, PluginC, PluginD };
        var graph = GraphOf(WithPlugins(names), names);

        graph.AddEdge(1, 2, EdgeType.RecordOverlap);
        graph.AddEdge(1, 3, EdgeType.RecordOverlap);
        graph.AddEdge(2, 0, EdgeType.RecordOverlap);
        graph.AddEdge(3, 0, EdgeType.RecordOverlap);

        var sorted = TieBreakAndSort(graph);

        sorted.Should().Equal(PluginB, PluginD, PluginC, PluginA);
    }
}
