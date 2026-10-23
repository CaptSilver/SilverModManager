using SilverModManager.Games.Bethesda.Loot.Sorting;

namespace SilverModManager.Games.Bethesda.Loot.Tests.Sorting;

// Follows the helpers in libloot's sorting/search.rs. Edge order matters in each: petgraph hands
// out the most recently added edge first, and libloot reverses that for its depth-first search.
public class SearchTests
{
    private static DirectedGraph<string, EdgeType> Build(string[] nodes, params (string From, string To, EdgeType Type)[] edges)
    {
        var graph = new DirectedGraph<string, EdgeType>();
        foreach (var node in nodes) graph.AddNode(node);
        foreach (var (from, to, type) in edges) graph.AddEdge(Array.IndexOf(nodes, from), Array.IndexOf(nodes, to), type);
        return graph;
    }

    private sealed class RecordingBfsVisitor : IBidirBfsVisitor
    {
        public List<string> Events { get; } = [];
        public void VisitForwardBfsEdge(int source, int target) => Events.Add($"F {source}>{target}");
        public void VisitReverseBfsEdge(int source, int target) => Events.Add($"R {source}>{target}");
        public void VisitIntersectionNode(int node) => Events.Add($"I {node}");
    }

    private sealed class RecordingDfsVisitor : IDfsVisitor<EdgeType>
    {
        public List<string> Events { get; } = [];
        public void VisitTreeEdge(GraphEdge<EdgeType> edge) => Events.Add($"T {edge.Source}>{edge.Target}");
        public void VisitForwardOrCrossEdge(GraphEdge<EdgeType> edge) => Events.Add($"X {edge.Source}>{edge.Target}");
        public void VisitBackEdge(GraphEdge<EdgeType> edge) => Events.Add($"B {edge.Source}>{edge.Target}");
        public void DiscoverNode(int node) => Events.Add($"D {node}");
        public void FinishNode(int node) => Events.Add($"E {node}");
    }

    [Fact]
    public void FindCycleShouldReturnNullForAnAcyclicGraph()
    {
        var graph = Build(["a", "b", "c"], ("a", "b", EdgeType.Master), ("b", "c", EdgeType.Master), ("a", "c", EdgeType.Master));

        Search.FindCycle(graph, n => n).Should().BeNull();
    }

    [Fact]
    public void FindCycleShouldReturnTheCycleWithEachVerticesOutEdgeType()
    {
        var graph = Build(["a", "b", "c"],
            ("a", "b", EdgeType.MasterlistLoadAfter), ("b", "c", EdgeType.UserLoadAfter), ("c", "a", EdgeType.MasterlistGroup));

        Search.FindCycle(graph, n => n).Should().Equal(
            new Vertex("a", EdgeType.MasterlistLoadAfter),
            new Vertex("b", EdgeType.UserLoadAfter),
            new Vertex("c", EdgeType.MasterlistGroup));
    }

    [Fact]
    public void FindCycleShouldDropVerticesBeforeTheCycle()
    {
        var graph = Build(["x", "a", "b"], ("x", "a", EdgeType.Master), ("a", "b", EdgeType.Hardcoded), ("b", "a", EdgeType.TieBreak));

        Search.FindCycle(graph, n => n).Should().Equal(
            new Vertex("a", EdgeType.Hardcoded),
            new Vertex("b", EdgeType.TieBreak));
    }

    [Fact]
    public void FindCycleShouldFollowOutEdgesOldestFirst()
    {
        // From a the oldest edge goes to b, so the cycle is a-b even though a-c was added later.
        var graph = Build(["a", "b", "c"],
            ("a", "b", EdgeType.Master), ("a", "c", EdgeType.UserGroup), ("b", "a", EdgeType.TieBreak), ("c", "a", EdgeType.TieBreak));

        Search.FindCycle(graph, n => n).Should().Equal(
            new Vertex("a", EdgeType.Master),
            new Vertex("b", EdgeType.TieBreak));
    }

    [Fact]
    public void BidirectionalBfsShouldReturnFalseIfThereIsNoPath()
    {
        var graph = Build(["a", "b"]);
        var visitor = new RecordingBfsVisitor();

        Search.BidirectionalBfs(graph, 0, 1, visitor).Should().BeFalse();
        visitor.Events.Should().BeEmpty();
    }

    [Fact]
    public void BidirectionalBfsShouldMeetOnTheNewestEdgeWhenTwoPathsTie()
    {
        var graph = Build(["a", "b", "c", "d"],
            ("a", "b", EdgeType.Master), ("a", "c", EdgeType.Master), ("b", "d", EdgeType.Master), ("c", "d", EdgeType.Master));
        var visitor = new RecordingBfsVisitor();

        Search.BidirectionalBfs(graph, 0, 3, visitor).Should().BeTrue();

        // Out-edges of a and in-edges of d are both visited newest first, so c wins over b.
        visitor.Events.Should().Equal("F 0>2", "F 0>1", "R 2>3", "R 1>3", "I 2");
    }

    [Fact]
    public void BidirectionalBfsShouldReportTheTargetAsTheIntersectionWhenItIsReachedForward()
    {
        var graph = Build(["a", "b"], ("a", "b", EdgeType.Master));
        var visitor = new RecordingBfsVisitor();

        Search.BidirectionalBfs(graph, 0, 1, visitor).Should().BeTrue();

        visitor.Events.Should().Equal("F 0>1", "I 1");
    }

    [Fact]
    public void DepthFirstSearchShouldVisitEdgesOldestFirstAndClassifyThem()
    {
        var graph = Build(["a", "b", "c"],
            ("a", "b", EdgeType.Master), ("a", "c", EdgeType.Master), ("b", "c", EdgeType.Master), ("c", "a", EdgeType.Master));
        var visitor = new RecordingDfsVisitor();

        Search.DepthFirstSearch(graph, new Dictionary<int, Colour>(), 0, visitor);

        visitor.Events.Should().Equal("D 0", "T 0>1", "D 1", "T 1>2", "D 2", "B 2>0", "E 2", "E 1", "X 0>2", "E 0");
    }

    [Fact]
    public void EdgeIterationOrderMatchesPetgraphPerTraversal()
    {
        var graph = Build(["a", "b", "c", "d"],
            ("a", "b", EdgeType.Master), ("a", "c", EdgeType.Master), ("a", "d", EdgeType.Master),
            ("b", "d", EdgeType.Master), ("c", "d", EdgeType.Master));

        // The raw views: petgraph prepends to its adjacency lists.
        graph.OutEdgesNewestFirst(0).Select(e => e.Target).Should().Equal(3, 2, 1);
        graph.OutEdgesOldestFirst(0).Select(e => e.Target).Should().Equal(1, 2, 3);
        graph.InEdgesNewestFirst(3).Select(e => e.Source).Should().Equal(2, 1, 0);

        // Breadth-first search consumes the newest-first views.
        var bfs = new RecordingBfsVisitor();
        Search.BidirectionalBfs(graph, 0, 3, bfs);
        bfs.Events.Take(3).Should().Equal("F 0>3", "F 0>2", "F 0>1");

        // Depth-first search and cycle finding consume the oldest-first view.
        var dfs = new RecordingDfsVisitor();
        Search.DepthFirstSearch(graph, new Dictionary<int, Colour>(), 0, dfs);
        dfs.Events.Where(e => e.StartsWith('T')).Should().Equal("T 0>1", "T 1>3", "T 0>2");

        // Topological sort follows newest-first neighbours: a, c, b, d rather than a, b, c, d.
        Algorithms.TopologicalSort(graph).Select(i => graph[i]).Should().Equal("a", "c", "b", "d");
    }

    [Fact]
    public void FindEdgeShouldReturnTheNewestEdgeBetweenTwoNodesAndUpdateEdgeShouldReplaceIt()
    {
        var graph = Build(["a", "b"], ("a", "b", EdgeType.Master));

        graph.FindEdge(0, 1)!.Value.Weight.Should().Be(EdgeType.Master);
        graph.FindEdge(1, 0).Should().BeNull();

        graph.UpdateEdge(0, 1, EdgeType.UserGroup);
        graph.UpdateEdge(1, 0, EdgeType.TieBreak);

        graph.OutEdgesOldestFirst(0).Select(e => e.Weight).Should().Equal(EdgeType.UserGroup);
        graph.OutEdgesOldestFirst(1).Select(e => e.Weight).Should().Equal(EdgeType.TieBreak);
        graph.InEdgesNewestFirst(1).Select(e => e.Weight).Should().Equal(EdgeType.UserGroup);
    }
}
