namespace SilverModManager.Games.Bethesda.Loot.Sorting;

// Follows libloot's sorting/search.rs.

public interface IDfsVisitor<TEdge>
{
    void VisitTreeEdge(GraphEdge<TEdge> edge);
    void VisitForwardOrCrossEdge(GraphEdge<TEdge> edge);
    void VisitBackEdge(GraphEdge<TEdge> edge);
    void DiscoverNode(int node);
    void FinishNode(int node);
}

public interface IBidirBfsVisitor
{
    void VisitForwardBfsEdge(int source, int target);
    void VisitReverseBfsEdge(int source, int target);
    void VisitIntersectionNode(int node);
}

/// <summary>
/// The visited marks and queues of <see cref="Search.BidirectionalBfs"/>, kept between searches. A mark is the
/// number of the search that set it, so starting a search clears nothing.
/// </summary>
public sealed class BfsScratch(int nodeCount)
{
    private int _run;

    internal int[] Forward { get; private set; } = new int[nodeCount];
    internal int[] Reverse { get; private set; } = new int[nodeCount];
    internal int[] ForwardQueue { get; private set; } = new int[nodeCount];
    internal int[] ReverseQueue { get; private set; } = new int[nodeCount];

    internal int Begin(int nodes)
    {
        if (Forward.Length < nodes)
        {
            Forward = new int[nodes];
            Reverse = new int[nodes];
            ForwardQueue = new int[nodes];
            ReverseQueue = new int[nodes];
            _run = 0;
        }

        // Marks start at zero, so the first search is number one; on wrap-around the marks are cleared.
        if (++_run == int.MaxValue)
        {
            Array.Clear(Forward);
            Array.Clear(Reverse);
            _run = 1;
        }

        return _run;
    }
}

/// <summary>Depth-first search state of a node. Nodes missing from the colour map are white.</summary>
public enum Colour
{
    White,
    Grey,
    Black,
}

public static class Search
{
    /// <summary>
    /// Searches forwards from <paramref name="from"/> and backwards from <paramref name="to"/> a
    /// level at a time until the two meet. Out- and in-edges are taken newest first, as petgraph's
    /// <c>neighbors</c> yields them, which decides the path reported when several are equally short.
    /// </summary>
    /// <param name="scratch">Working memory to reuse across searches of the same graph; a search of a big graph is
    /// otherwise mostly allocating.</param>
    public static bool BidirectionalBfs<TNode, TEdge>(
        DirectedGraph<TNode, TEdge> graph, int from, int to, IBidirBfsVisitor visitor, BfsScratch? scratch = null)
    {
        scratch ??= new BfsScratch(graph.NodeCount);
        var run = scratch.Begin(graph.NodeCount);
        var forward = scratch.Forward;
        var reverse = scratch.Reverse;
        var forwardQueue = scratch.ForwardQueue;
        var reverseQueue = scratch.ReverseQueue;
        int forwardHead = 0, forwardTail = 0, reverseHead = 0, reverseTail = 0;

        forwardQueue[forwardTail++] = from;
        reverseQueue[reverseTail++] = to;
        forward[from] = run;
        reverse[to] = run;

        while (forwardHead < forwardTail && reverseHead < reverseTail)
        {
            var forwardCurrent = forwardQueue[forwardHead++];
            var reverseCurrent = reverseQueue[reverseHead++];

            if (forwardCurrent == to || reverse[forwardCurrent] == run)
            {
                visitor.VisitIntersectionNode(forwardCurrent);
                return true;
            }

            for (int i = 0, count = graph.OutDegree(forwardCurrent); i < count; i++)
            {
                var target = graph.OutTargetNewestFirst(forwardCurrent, i);
                if (forward[target] == run) continue;

                forward[target] = run;
                visitor.VisitForwardBfsEdge(forwardCurrent, target);
                forwardQueue[forwardTail++] = target;
            }

            if (reverseCurrent == from || forward[reverseCurrent] == run)
            {
                visitor.VisitIntersectionNode(reverseCurrent);
                return true;
            }

            for (int i = 0, count = graph.InDegree(reverseCurrent); i < count; i++)
            {
                var source = graph.InSourceNewestFirst(reverseCurrent, i);
                if (reverse[source] == run) continue;

                reverse[source] = run;
                visitor.VisitReverseBfsEdge(source, reverseCurrent);
                reverseQueue[reverseTail++] = source;
            }
        }

        return false;
    }

    /// <summary>
    /// Finds the first cycle a depth-first search meets, trying nodes in index order. The trail
    /// holds the vertices on the cycle with the type of the edge leaving each.
    /// </summary>
    public static IReadOnlyList<Vertex>? FindCycle<TNode>(DirectedGraph<TNode, EdgeType> graph, Func<TNode, string> nodeToString)
    {
        var detector = new CycleDetector<TNode>(graph, nodeToString);
        var colours = new Dictionary<int, Colour>();

        for (var node = 0; node < graph.NodeCount; node++)
        {
            DepthFirstSearch(graph, colours, node, detector);
            if (detector.FoundCycle) return detector.Trail;
        }

        return null;
    }

    // Written out instead of using a generic search because the visitors need the edge being
    // followed, and two nodes can have several edges between them with different weights.
    // Out-edges are taken oldest first: libloot reverses petgraph's order for backwards compatibility.
    public static void DepthFirstSearch<TNode, TEdge>(
        DirectedGraph<TNode, TEdge> graph, Dictionary<int, Colour> colours, int start, IDfsVisitor<TEdge> visitor)
    {
        var stack = new Stack<(int Node, IEnumerator<GraphEdge<TEdge>> Edges)>();
        stack.Push((start, graph.OutEdgesOldestFirst(start).GetEnumerator()));
        colours[start] = Colour.Grey;
        visitor.DiscoverNode(start);

        while (stack.Count > 0)
        {
            var (current, edges) = stack.Peek();
            if (edges.MoveNext())
            {
                var edge = edges.Current;
                switch (colours.GetValueOrDefault(edge.Target, Colour.White))
                {
                    case Colour.White:
                        visitor.VisitTreeEdge(edge);
                        colours[edge.Target] = Colour.Grey;
                        visitor.DiscoverNode(edge.Target);
                        stack.Push((edge.Target, graph.OutEdgesOldestFirst(edge.Target).GetEnumerator()));
                        break;
                    case Colour.Grey:
                        visitor.VisitBackEdge(edge);
                        break;
                    default:
                        visitor.VisitForwardOrCrossEdge(edge);
                        break;
                }
            }
            else
            {
                colours[current] = Colour.Black;
                visitor.FinishNode(current);
                edges.Dispose();
                stack.Pop();
            }
        }
    }

    private sealed class CycleDetector<TNode>(DirectedGraph<TNode, EdgeType> graph, Func<TNode, string> nodeToString)
        : IDfsVisitor<EdgeType>
    {
        private readonly List<Vertex> _trail = [];

        public bool FoundCycle { get; private set; }

        public IReadOnlyList<Vertex> Trail => _trail;

        public void VisitTreeEdge(GraphEdge<EdgeType> edge)
        {
            if (FoundCycle) return;
            _trail.Add(new Vertex(nodeToString(graph[edge.Source]), edge.Weight));
        }

        public void VisitForwardOrCrossEdge(GraphEdge<EdgeType> edge) { }

        public void VisitBackEdge(GraphEdge<EdgeType> edge)
        {
            if (FoundCycle) return;
            VisitTreeEdge(edge);

            var targetName = nodeToString(graph[edge.Target]);
            var position = _trail.FindIndex(v => v.Name == targetName);
            if (position < 0)
            {
                throw new InvalidOperationException("The target of a back edge cannot be found in the current visitor trail.");
            }

            _trail.RemoveRange(0, position);
            FoundCycle = true;
        }

        public void DiscoverNode(int node) { }

        public void FinishNode(int node)
        {
            if (!FoundCycle && _trail.Count > 0) _trail.RemoveAt(_trail.Count - 1);
        }
    }
}
