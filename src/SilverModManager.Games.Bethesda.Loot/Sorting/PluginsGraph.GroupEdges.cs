using NexusMods.Games.Bethesda.Loot.Metadata;

namespace NexusMods.Games.Bethesda.Loot.Sorting;

// Follows libloot's sorting/plugins.rs (add_group_edges, GroupsPathVisitor).

internal sealed partial class PluginsGraph<T>
{
    /// <summary>
    /// Adds edges from the plugins in each group to the plugins in the groups that load after it,
    /// walking the groups graph depth first. Plugins in the default group are only used as sources
    /// in a last walk from the default group, so that where an edge would have to be dropped to
    /// avoid a cycle it is the default group's that goes.
    /// </summary>
    /// <exception cref="UndefinedGroupException">There is no group named "default".</exception>
    public void AddGroupEdges(GroupsGraph groupsGraph)
    {
        var pluginsInGroups = GetPluginsInGroups();
        var defaultGroupNode = groupsGraph.DefaultGroupNode();

        // Groups whose plugins already have all their edges, so that they are not added twice.
        var finishedNodes = new HashSet<int>();

        // The order resolves potential cycles in favour of earlier-loading groups. It does not make
        // the longest path be walked first: a root can be on several paths and the order does not
        // influence which one the search takes.
        foreach (var groupNode in groupsGraph.SortedGroupNodes())
        {
            // Every node gets a walk, not just the roots: a search visits each node once, so where
            // branches merge, the plugins of one branch would not be carried past the merge point.
            var visitor = new GroupsPathVisitor(this, groupsGraph, pluginsInGroups, finishedNodes, defaultGroupNode);
            Search.DepthFirstSearch(groupsGraph.Inner, new Dictionary<int, Colour>(), groupNode, visitor);
        }

        var lastVisitor = new GroupsPathVisitor(this, groupsGraph, pluginsInGroups, finishedNodes, null);
        Search.DepthFirstSearch(groupsGraph.Inner, new Dictionary<int, Colour>(), defaultGroupNode, lastVisitor);
    }

    private Dictionary<string, List<int>> GetPluginsInGroups()
    {
        var pluginsInGroups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var node = 0; node < NodeCount; node++)
        {
            var group = _graph[node].Group;
            if (!pluginsInGroups.TryGetValue(group, out var nodes)) pluginsInGroups[group] = nodes = [];
            nodes.Add(node);
        }

        return pluginsInGroups;
    }

    private void AddEdgesFromPlugin(int fromPlugin, IReadOnlyList<int> toPlugins, bool pathInvolvesUserMetadata)
    {
        foreach (var toPlugin in toPlugins)
        {
            if (IsPathCached(fromPlugin, toPlugin)) continue;

            var involvesUserMetadata = pathInvolvesUserMetadata
                || _graph[fromPlugin].GroupIsUserMetadata
                || _graph[toPlugin].GroupIsUserMetadata;
            var edgeType = involvesUserMetadata ? EdgeType.UserGroup : EdgeType.MasterlistGroup;

            // An edge that would close a cycle is dropped: the constraints already in the graph win.
            if (PathExists(toPlugin, fromPlugin)) continue;

            AddEdge(fromPlugin, toPlugin, edgeType);
        }
    }

    private sealed class GroupsPathVisitor(
        PluginsGraph<T> pluginsGraph,
        GroupsGraph groupsGraph,
        Dictionary<string, List<int>> groupsPlugins,
        HashSet<int> finishedGroupNodes,
        int? groupNodeToIgnoreAsSource) : IDfsVisitor<EdgeType>
    {
        private static readonly IReadOnlyList<int> NoPlugins = [];

        // The edges from the walk's start to the group being visited, each with the plugins of its
        // source group, so that the provenance of the path is known when edges are added.
        private readonly List<(GraphEdge<EdgeType> Edge, IReadOnlyList<int> Plugins)> _edgeStack = [];

        // Groups with a forward or cross edge in their subtree: their plugins cannot be carried past
        // the edge's target, so they are not marked as finished.
        private readonly HashSet<int> _unfinishableNodes = [];

        public void VisitTreeEdge(GraphEdge<EdgeType> edge)
        {
            // The plugins of a source group that is ignored (the default group, outside the last
            // walk) or already finished are left out, but the edge stays on the stack for its
            // provenance.
            var edgePlugins = ShouldIgnoreSourceNode(edge.Source) ? NoPlugins : FindPluginsInGroup(edge.Source);
            _edgeStack.Add((edge, edgePlugins));

            AddPluginGraphEdges(FindPluginsInGroup(edge.Target));
        }

        public void VisitForwardOrCrossEdge(GraphEdge<EdgeType> edge)
        {
            // None of the plugins in the path so far can have edges added past the target, so the
            // source and everything on the stack is unfinishable.
            foreach (var (stackEdge, _) in _edgeStack) _unfinishableNodes.Add(stackEdge.Source);
            _unfinishableNodes.Add(edge.Source);
        }

        public void VisitBackEdge(GraphEdge<EdgeType> edge) { }

        public void DiscoverNode(int node) { }

        public void FinishNode(int node)
        {
            // Marked finished so that a later walk sharing the set does not add edges from this
            // group's plugins again.
            if (groupNodeToIgnoreAsSource != node && !_unfinishableNodes.Contains(node))
            {
                finishedGroupNodes.Add(node);
            }

            // Drops the edge that has this node as its target; the start node has none.
            if (_edgeStack.Count > 0) _edgeStack.RemoveAt(_edgeStack.Count - 1);
        }

        private bool ShouldIgnoreSourceNode(int node) =>
            groupNodeToIgnoreAsSource == node || finishedGroupNodes.Contains(node);

        private IReadOnlyList<int> FindPluginsInGroup(int node) =>
            groupsPlugins.TryGetValue(groupsGraph.NodeName(node), out var plugins) ? plugins : NoPlugins;

        private void AddPluginGraphEdges(IReadOnlyList<int> targetPlugins)
        {
            for (var i = 0; i < _edgeStack.Count; i++)
            {
                var pathInvolvesUserMetadata = false;
                for (var j = i; j < _edgeStack.Count; j++)
                {
                    if (_edgeStack[j].Edge.Weight != EdgeType.UserLoadAfter) continue;
                    pathInvolvesUserMetadata = true;
                    break;
                }

                foreach (var fromPlugin in _edgeStack[i].Plugins)
                {
                    pluginsGraph.AddEdgesFromPlugin(fromPlugin, targetPlugins, pathInvolvesUserMetadata);
                }
            }
        }
    }
}
