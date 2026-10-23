using SilverModManager.Games.Bethesda.Loot.Sorting;

namespace SilverModManager.Games.Bethesda.Loot.Tests.Sorting;

// Hand-computed from petgraph 0.8.2's toposort and bellman_ford.
public class AlgorithmsTests
{
    private static DirectedGraph<string, float> Build(string[] nodes, params (string From, string To, float Weight)[] edges)
    {
        var graph = new DirectedGraph<string, float>();
        foreach (var node in nodes) graph.AddNode(node);
        foreach (var (from, to, weight) in edges) graph.AddEdge(Array.IndexOf(nodes, from), Array.IndexOf(nodes, to), weight);
        return graph;
    }

    [Fact]
    public void TopologicalSortShouldReturnPetgraphsOrderWhenSeveralAreValid()
    {
        // a, b, c and a, c, b are both valid. petgraph pushes a's neighbours newest first and pops
        // the last one pushed, so it finishes b, then c, then a, and the reversal gives a, c, b.
        var graph = Build(["a", "b", "c"], ("a", "b", 0), ("a", "c", 0));

        Algorithms.TopologicalSort(graph).Select(i => graph[i]).Should().Equal("a", "c", "b");
    }

    [Fact]
    public void TopologicalSortShouldStartNewTreesInNodeIndexOrder()
    {
        // a and b both lead to c, and the finish stack is [c, a, b] before it is reversed.
        var graph = Build(["a", "b", "c"], ("a", "c", 0), ("b", "c", 0));

        Algorithms.TopologicalSort(graph).Select(i => graph[i]).Should().Equal("b", "a", "c");
    }

    [Fact]
    public void TopologicalSortShouldThrowOnACycle()
    {
        var graph = Build(["a", "b", "c"], ("a", "b", 0), ("b", "c", 0), ("c", "b", 0));

        var act = () => Algorithms.TopologicalSort(graph);

        act.Should().Throw<GraphCycleException>();
    }

    [Fact]
    public void TopologicalSortShouldThrowOnASelfLoop()
    {
        var graph = Build(["a"], ("a", "a", 0));

        var act = () => Algorithms.TopologicalSort(graph);

        act.Should().Throw<GraphCycleException>().Which.Node.Should().Be(0);
    }

    [Fact]
    public void BellmanFordShouldKeepTheFirstRelaxedPredecessorOnATie()
    {
        // Both s-a-t and s-b-t cost 2. a is processed before b, so t's predecessor stays a.
        var graph = Build(["s", "a", "b", "t"], ("s", "a", 1), ("s", "b", 1), ("a", "t", 1), ("b", "t", 1));

        var paths = Algorithms.BellmanFord(graph, 0, w => w);

        paths.Distances.Should().Equal(0f, 1f, 1f, 2f);
        paths.Predecessors[3].Should().Be(1);
    }

    [Fact]
    public void BellmanFordTieFollowsNodeIndexOrderNotEdgeOrder()
    {
        // Same shape with b's node added before a's: b relaxes t first.
        var graph = Build(["s", "b", "a", "t"], ("s", "a", 1), ("s", "b", 1), ("a", "t", 1), ("b", "t", 1));

        var paths = Algorithms.BellmanFord(graph, 0, w => w);

        paths.Predecessors[3].Should().Be(1);
        graph[1].Should().Be("b");
    }

    [Fact]
    public void BellmanFordShouldLeaveUnreachableNodesWithoutAPredecessor()
    {
        var graph = Build(["s", "a", "u"], ("s", "a", 1));

        var paths = Algorithms.BellmanFord(graph, 0, w => w);

        paths.Distances[2].Should().Be(float.PositiveInfinity);
        paths.Predecessors.Should().Equal(null, 0, null);
    }

    [Fact]
    public void BellmanFordShouldHandleNegativeWeights()
    {
        var graph = Build(["s", "a", "t"], ("s", "t", 1), ("s", "a", -1_000_000f), ("a", "t", -1_000_000f));

        var paths = Algorithms.BellmanFord(graph, 0, w => w);

        paths.Predecessors[2].Should().Be(1);
    }

    [Fact]
    public void BellmanFordShouldThrowOnANegativeCycle()
    {
        var graph = Build(["s", "a", "b"], ("s", "a", 1), ("a", "b", -2), ("b", "a", 1));

        var act = () => Algorithms.BellmanFord(graph, 0, w => w);

        act.Should().Throw<NegativeCycleException>();
    }
}
