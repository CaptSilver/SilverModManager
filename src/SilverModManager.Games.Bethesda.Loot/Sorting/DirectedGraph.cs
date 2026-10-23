namespace NexusMods.Games.Bethesda.Loot.Sorting;

/// <summary>An edge as seen from the graph: its endpoints and weight.</summary>
public readonly record struct GraphEdge<TEdge>(int Source, int Target, TEdge Weight);

/// <summary>
/// A directed multigraph that reproduces the edge iteration orders of petgraph's <c>Graph</c>,
/// which libloot's sorting results depend on. Nodes are identified by their index in insertion
/// order. Petgraph prepends to its adjacency lists, so its own iterators yield the most recently
/// added edge first; <see cref="OutEdgesOldestFirst"/> is what libloot gets by reversing that.
/// </summary>
public sealed class DirectedGraph<TNode, TEdge>
{
    private sealed class EdgeData(int source, int target, TEdge weight)
    {
        public int Source { get; } = source;
        public int Target { get; } = target;
        public TEdge Weight { get; set; } = weight;
        public GraphEdge<TEdge> ToEdge() => new(Source, Target, Weight);
    }

    private readonly List<TNode> _nodes = [];
    private readonly List<List<EdgeData>> _outEdges = [];
    private readonly List<List<EdgeData>> _inEdges = [];

    public int NodeCount => _nodes.Count;

    public TNode this[int node] => _nodes[node];

    public int AddNode(TNode node)
    {
        _nodes.Add(node);
        _outEdges.Add([]);
        _inEdges.Add([]);
        return _nodes.Count - 1;
    }

    public void AddEdge(int source, int target, TEdge weight)
    {
        CheckNode(source);
        CheckNode(target);
        var edge = new EdgeData(source, target, weight);
        _outEdges[source].Add(edge);
        _inEdges[target].Add(edge);
    }

    /// <summary>Replaces the weight of the edge from <paramref name="source"/> to <paramref name="target"/> if there is one, else adds it.</summary>
    public void UpdateEdge(int source, int target, TEdge weight)
    {
        if (FindEdgeData(source, target) is { } existing) existing.Weight = weight;
        else AddEdge(source, target, weight);
    }

    /// <summary>The most recently added edge from <paramref name="source"/> to <paramref name="target"/>, if any.</summary>
    public GraphEdge<TEdge>? FindEdge(int source, int target) => FindEdgeData(source, target)?.ToEdge();

    /// <summary>Out-edges in petgraph's native order: the most recently added first.</summary>
    public IEnumerable<GraphEdge<TEdge>> OutEdgesNewestFirst(int node)
    {
        var edges = _outEdges[node];
        for (var i = edges.Count - 1; i >= 0; i--) yield return edges[i].ToEdge();
    }

    /// <summary>In-edges in petgraph's native order: the most recently added first.</summary>
    public IEnumerable<GraphEdge<TEdge>> InEdgesNewestFirst(int node)
    {
        var edges = _inEdges[node];
        for (var i = edges.Count - 1; i >= 0; i--) yield return edges[i].ToEdge();
    }

    /// <summary>Out-edges in insertion order, the reverse of petgraph's.</summary>
    public IEnumerable<GraphEdge<TEdge>> OutEdgesOldestFirst(int node)
    {
        var edges = _outEdges[node];
        for (var i = 0; i < edges.Count; i++) yield return edges[i].ToEdge();
    }

    public bool HasInEdges(int node) => _inEdges[node].Count > 0;

    private EdgeData? FindEdgeData(int source, int target)
    {
        CheckNode(source);
        CheckNode(target);
        var edges = _outEdges[source];
        for (var i = edges.Count - 1; i >= 0; i--)
        {
            if (edges[i].Target == target) return edges[i];
        }

        return null;
    }

    private void CheckNode(int node)
    {
        if ((uint)node >= (uint)_nodes.Count) throw new ArgumentOutOfRangeException(nameof(node), node, "No such node.");
    }
}
