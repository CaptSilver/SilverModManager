using NexusMods.Games.Bethesda.Loot.Metadata;

namespace NexusMods.Games.Bethesda.Loot.Sorting;

// Follows libloot's sorting/groups.rs.

/// <summary>
/// The graph of group "load after" relationships. Nodes are group names, in the order masterlist
/// groups by name, then user-only groups by name; an edge goes from the group that loads first to
/// the group that loads after it.
/// </summary>
public sealed class GroupsGraph
{
    // User edges are made so cheap that a path through them always beats one without.
    private const float UserEdgeWeight = -1_000_000f;
    private const float OtherEdgeWeight = 1f;

    private readonly DirectedGraph<string, EdgeType> _graph;

    private GroupsGraph(DirectedGraph<string, EdgeType> graph) => _graph = graph;

    public int NodeCount => _graph.NodeCount;

    public string NodeName(int node) => _graph[node];

    /// <summary>Builds the graph from masterlist groups, then user groups, which may extend or add to them.</summary>
    /// <exception cref="BuildGroupsGraphException">A group loads after one that is not defined (masterlist groups cannot refer to user-only groups), or the relationships form a cycle.</exception>
    public static GroupsGraph Build(IEnumerable<Group> masterlistGroups, IEnumerable<Group> userGroups)
    {
        var graph = new DirectedGraph<string, EdgeType>();
        var nodes = new Dictionary<string, int>(StringComparer.Ordinal);

        try
        {
            AddGroups(graph, nodes, SortedByName(masterlistGroups), EdgeType.MasterlistLoadAfter);
            AddGroups(graph, nodes, SortedByName(userGroups), EdgeType.UserLoadAfter);
        }
        catch (UndefinedGroupException e)
        {
            throw new BuildGroupsGraphException(e);
        }

        if (Search.FindCycle(graph, name => name) is { } cycle)
        {
            throw new BuildGroupsGraphException(new CyclicInteractionException(cycle));
        }

        return new GroupsGraph(graph);
    }

    /// <summary>
    /// The path from one group to another as vertices carrying the type of the edge to the next,
    /// preferring user-defined edges, or an empty list if there is no path.
    /// </summary>
    /// <exception cref="GroupsPathException">A group is not defined, or the user-defined relationships contain a cycle.</exception>
    public IReadOnlyList<Vertex> FindPath(string fromGroupName, string toGroupName)
    {
        int from, to;
        try
        {
            from = NodeByName(fromGroupName);
            to = NodeByName(toGroupName);
        }
        catch (UndefinedGroupException e)
        {
            throw new GroupsPathException(e);
        }

        BellmanFordPaths paths;
        try
        {
            paths = Algorithms.BellmanFord(_graph, from, type => type == EdgeType.UserLoadAfter ? UserEdgeWeight : OtherEdgeWeight);
        }
        catch (NegativeCycleException)
        {
            throw new GroupsPathException(PathfindingException.NegativeCycle());
        }

        var path = new List<Vertex> { new(_graph[to]) };
        var current = to;
        while (current != from)
        {
            if (paths.Predecessors[current] is not { } preceding) return [];

            // Bellman-Ford never makes a node its own predecessor; libloot treats it as no path
            // rather than walking the same node forever.
            if (preceding == current) return [];

            if (_graph.FindEdge(preceding, current) is not { } edge)
            {
                throw new GroupsPathException(PathfindingException.EdgeNotFound(_graph[preceding], _graph[current]));
            }

            path.Add(new Vertex(_graph[preceding], edge.Weight));
            current = preceding;
        }

        path.Reverse();
        return path;
    }

    /// <summary>
    /// Node indices with root nodes first, longest path first, and otherwise in node insertion
    /// order. The sort is stable: ties stay in insertion order, which is not the same as name order
    /// because user-only groups come after every masterlist group.
    /// </summary>
    public IReadOnlyList<int> SortedGroupNodes()
    {
        var nodes = Enumerable.Range(0, _graph.NodeCount).Select(node =>
        {
            if (_graph.HasInEdges(node)) return (Node: node, IsRoot: false, PathLength: 0);

            var visitor = new GroupsPathLengthVisitor();
            Search.DepthFirstSearch(_graph, new Dictionary<int, Colour>(), node, visitor);
            return (Node: node, IsRoot: true, PathLength: visitor.MaxPathLength);
        });

        return nodes.OrderByDescending(n => n.IsRoot).ThenByDescending(n => n.PathLength).Select(n => n.Node).ToList();
    }

    /// <exception cref="UndefinedGroupException">There is no group named "default".</exception>
    public int DefaultGroupNode() => NodeByNameOrNull(Group.DefaultName) ?? throw new UndefinedGroupException(Group.DefaultName);

    /// <exception cref="UndefinedGroupException">There is no group with the name.</exception>
    public int NodeByName(string name) => NodeByNameOrNull(name) ?? throw new UndefinedGroupException(name);

    private int? NodeByNameOrNull(string name)
    {
        for (var node = 0; node < _graph.NodeCount; node++)
        {
            if (_graph[node] == name) return node;
        }

        return null;
    }

    // Rust orders strings by UTF-8 bytes, which is code point order; ordinal string comparison
    // orders by UTF-16 unit and differs once supplementary characters are involved. OrderBy is stable.
    private static List<Group> SortedByName(IEnumerable<Group> groups) =>
        groups.OrderBy(g => g.Name, CodePointComparer.Instance).ToList();

    private static void AddGroups(
        DirectedGraph<string, EdgeType> graph, Dictionary<string, int> nodes, List<Group> groups, EdgeType edgeType)
    {
        foreach (var group in groups)
        {
            if (!nodes.ContainsKey(group.Name)) nodes[group.Name] = graph.AddNode(group.Name);
        }

        foreach (var group in groups)
        {
            var node = nodes[group.Name];
            foreach (var otherName in group.AfterGroups.Order(CodePointComparer.Instance))
            {
                if (!nodes.TryGetValue(otherName, out var other)) throw new UndefinedGroupException(otherName);
                graph.UpdateEdge(other, node, edgeType);
            }
        }
    }

    private sealed class GroupsPathLengthVisitor : IDfsVisitor<EdgeType>
    {
        private int _currentPathLength;

        public int MaxPathLength { get; private set; }

        public void VisitTreeEdge(GraphEdge<EdgeType> edge) { }
        public void VisitForwardOrCrossEdge(GraphEdge<EdgeType> edge) { }
        public void VisitBackEdge(GraphEdge<EdgeType> edge) { }

        public void DiscoverNode(int node)
        {
            _currentPathLength++;
            if (_currentPathLength > MaxPathLength) MaxPathLength = _currentPathLength;
        }

        public void FinishNode(int node) => _currentPathLength--;
    }
}
