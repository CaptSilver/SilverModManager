using SilverModManager.Games.Bethesda.LoadOrder;

namespace SilverModManager.Games.Bethesda.Loot.Sorting;

// Follows libloot's sorting/plugins.rs (PluginsGraph).

/// <summary>
/// The graph of load order constraints between the plugins of one partition. An edge goes from
/// the plugin that loads first to the plugin that loads after it.
/// </summary>
internal sealed partial class PluginsGraph<T> where T : ISortingPlugin
{
    private readonly DirectedGraph<PluginSortingData<T>, EdgeType> _graph = new();

    // Every pair known to be connected by a path, so that an edge is only added when it is not
    // already implied. Dropping this changes which edges exist and so the sort result.
    private readonly Dictionary<int, HashSet<int>> _pathsCache = [];

    public int NodeCount => _graph.NodeCount;

    public PluginSortingData<T> this[int node] => _graph[node];

    public DirectedGraph<PluginSortingData<T>, EdgeType> Inner => _graph;

    public int AddNode(PluginSortingData<T> plugin) => _graph.AddNode(plugin);

    public void AddEdge(int from, int to, EdgeType edgeType)
    {
        if (IsPathCached(from, to)) return;

        _graph.AddEdge(from, to, edgeType);
        CachePath(from, to);
    }

    public void AddSpecificEdges()
    {
        for (var node = 0; node < NodeCount; node++)
        {
            var plugin = _graph[node];

            // Masters and non-masters are sorted separately now, so this should add nothing, but
            // it stays as a safety net.
            for (var other = node + 1; other < NodeCount; other++)
            {
                var otherPlugin = _graph[other];
                if (plugin.IsMaster == otherPlugin.IsMaster) continue;

                if (otherPlugin.IsMaster) AddEdge(other, node, EdgeType.MasterFlag);
                else AddEdge(node, other, EdgeType.MasterFlag);
            }

            AddEdgesFrom(plugin.Masters, node, EdgeType.Master);
            AddEdgesFrom(plugin.MasterlistRequirements, node, EdgeType.MasterlistRequirement);
            AddEdgesFrom(plugin.UserRequirements, node, EdgeType.UserRequirement);
            AddEdgesFrom(plugin.MasterlistLoadAfter, node, EdgeType.MasterlistLoadAfter);
            AddEdgesFrom(plugin.UserLoadAfter, node, EdgeType.UserLoadAfter);
        }
    }

    // Files that are not in the graph are skipped: metadata often names plugins that are not installed.
    private void AddEdgesFrom(IReadOnlyList<string> names, int node, EdgeType edgeType)
    {
        foreach (var name in names)
        {
            if (NodeIndexByName(name) is { } other) AddEdge(other, node, edgeType);
        }
    }

    public void AddEarlyLoadingPluginEdges(IReadOnlyList<string> earlyLoadingPlugins)
    {
        if (earlyLoadingPlugins.Count == 0) return;

        var earlyLoaders = new List<(int Position, int Node)>();
        var others = new List<int>();
        for (var node = 0; node < NodeCount; node++)
        {
            var position = IndexOfName(earlyLoadingPlugins, _graph[node].Name);
            if (position >= 0) earlyLoaders.Add((position, node));
            else others.Add(node);
        }

        earlyLoaders = earlyLoaders.OrderBy(e => e.Position).ToList();

        for (var i = 1; i < earlyLoaders.Count; i++)
        {
            AddEdge(earlyLoaders[i - 1].Node, earlyLoaders[i].Node, EdgeType.Hardcoded);
        }

        // Only the last early loader needs an edge to the rest: the others reach them through it.
        if (earlyLoaders.Count == 0) return;
        var last = earlyLoaders[^1].Node;
        foreach (var other in others) AddEdge(last, other, EdgeType.Hardcoded);
    }

    /// <exception cref="CyclicInteractionException">The graph has a cycle.</exception>
    public void CheckForCycles()
    {
        if (Search.FindCycle(_graph, plugin => plugin.Name) is { } cycle) throw new CyclicInteractionException(cycle);
    }

    /// <exception cref="CycleInvolvingPluginException">The graph has a cycle.</exception>
    public IReadOnlyList<int> TopologicalSort()
    {
        try
        {
            return Algorithms.TopologicalSort(_graph);
        }
        catch (GraphCycleException e)
        {
            throw new CycleInvolvingPluginException(_graph[e.Node].Name);
        }
    }

    /// <summary>The first pair of consecutive nodes in the path with no edge joining them, if any.</summary>
    /// <remarks>
    /// libloot only logs this, as a sign that the sorted order is not the only one the constraints
    /// allow. The sorter has no logger, so the caller gets the pair and decides.
    /// </remarks>
    public (int First, int Second)? CheckPathIsHamiltonian(IReadOnlyList<int> path)
    {
        for (var i = 1; i < path.Count; i++)
        {
            if (_graph.FindEdge(path[i - 1], path[i]) is null) return (path[i - 1], path[i]);
        }

        return null;
    }

    public void CachePath(int from, int to)
    {
        if (!_pathsCache.TryGetValue(from, out var targets)) _pathsCache[from] = targets = [];
        targets.Add(to);
    }

    public bool IsPathCached(int from, int to) => _pathsCache.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>The node with the plugin's name, ignoring case, or null.</summary>
    public int? NodeIndexByName(string name)
    {
        for (var node = 0; node < NodeCount; node++)
        {
            if (PluginNameComparer.Instance.Equals(_graph[node].Name, name)) return node;
        }

        return null;
    }

    public bool PathExists(int from, int to)
    {
        if (IsPathCached(from, to)) return true;

        return Search.BidirectionalBfs(_graph, from, to, new PathCacher(this, from, to));
    }

    /// <summary>A shortest path from one node to another, or null if there is none.</summary>
    /// <exception cref="PathfindingException">The search met a node it cannot trace back to its start or end.</exception>
    public IReadOnlyList<int>? FindPath(int from, int to)
    {
        var finder = new PathFinder(this, from, to);
        return Search.BidirectionalBfs(_graph, from, to, finder) ? finder.Path() : null;
    }

    private static int IndexOfName(IReadOnlyList<string> names, string name)
    {
        for (var i = 0; i < names.Count; i++)
        {
            if (PluginNameComparer.Instance.Equals(names[i], name)) return i;
        }

        return -1;
    }

    private sealed class PathCacher(PluginsGraph<T> graph, int from, int to) : IBidirBfsVisitor
    {
        public void VisitForwardBfsEdge(int source, int target) => graph.CachePath(from, target);

        public void VisitReverseBfsEdge(int source, int target) => graph.CachePath(source, to);

        public void VisitIntersectionNode(int node) { }
    }

    private sealed class PathFinder(PluginsGraph<T> graph, int from, int to) : IBidirBfsVisitor
    {
        private readonly Dictionary<int, int> _forwardParents = [];
        private readonly Dictionary<int, int> _reverseChildren = [];
        private int? _intersection;

        public void VisitForwardBfsEdge(int source, int target)
        {
            graph.CachePath(from, target);
            _forwardParents[target] = source;
        }

        public void VisitReverseBfsEdge(int source, int target)
        {
            graph.CachePath(source, to);
            _reverseChildren[source] = target;
        }

        public void VisitIntersectionNode(int node) => _intersection = node;

        public IReadOnlyList<int>? Path()
        {
            if (_intersection is not { } intersection) return null;

            var path = new List<int> { intersection };
            var current = intersection;
            while (current != from)
            {
                if (!_forwardParents.TryGetValue(current, out var next))
                {
                    throw PathfindingException.PrecedingNodeNotFound(graph[current].Name);
                }

                path.Add(next);
                current = next;
            }

            path.Reverse();

            current = intersection;
            while (current != to)
            {
                if (!_reverseChildren.TryGetValue(current, out var next))
                {
                    throw PathfindingException.FollowingNodeNotFound(graph[current].Name);
                }

                path.Add(next);
                current = next;
            }

            return path;
        }
    }
}
