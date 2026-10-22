namespace NexusMods.Games.Bethesda.Loot.Sorting;

// Follows libloot's sorting/plugins.rs (sort_plugins).

public static class PluginSorter
{
    /// <summary>
    /// Sorts plugins into a load order from their masters, metadata and early-loading plugins. The
    /// result does not depend on the order the plugins are given in.
    /// </summary>
    /// <exception cref="UndefinedGroupException">A plugin is in a group that does not exist.</exception>
    /// <exception cref="PluginGraphValidationException">An edge contradicts master flags.</exception>
    /// <exception cref="CyclicInteractionException">The constraints form a cycle.</exception>
    /// <exception cref="SortingException">The plugins cannot be sorted for another reason.</exception>
    public static SortResult Sort<T>(
        IReadOnlyList<PluginSortingData<T>> plugins, GroupsGraph groupsGraph, IReadOnlyList<string> earlyLoadingPlugins)
        where T : ISortingPlugin
    {
        if (plugins.Count == 0) return new SortResult([], default, false);

        Validation.ValidatePluginGroups(plugins, groupsGraph);

        // Vertex order decides which edges get added, so it has to be the same for the same
        // plugins whatever order they arrive in and whatever the current load order is. Rust
        // compares str by UTF-8 bytes. OrderBy is stable.
        var byName = plugins.OrderBy(p => p.Name, Utf8ByteOrderComparer.Instance).ToList();

        // Master flags make for O(masters * non-masters) edges, and every search costs more the
        // more edges there are, so masters, non-masters and blueprint masters are sorted as three
        // graphs. That also means an edge from a non-master to a master is ignored instead of
        // reported as a cycle; the validation below reports the ones that matter.
        var masters = byName.Where(p => p.IsMaster && !p.IsBlueprintMaster).ToList();
        var blueprintMasters = byName.Where(p => p.IsBlueprintMaster).ToList();
        var nonMasters = byName.Where(p => !p.IsMaster).ToList();

        Validation.ValidateSpecificAndHardcodedEdges(masters, blueprintMasters, nonMasters, earlyLoadingPlugins);

        // Blueprint masters are sorted before non-masters, as libloot does, so that when both
        // partitions fail it is the same error that gets reported.
        var mastersResult = SortPartition(masters, groupsGraph, earlyLoadingPlugins);
        var blueprintMastersResult = SortPartition(blueprintMasters, groupsGraph, earlyLoadingPlugins);
        var nonMastersResult = SortPartition(nonMasters, groupsGraph, earlyLoadingPlugins);

        var order = new List<string>(byName.Count);
        order.AddRange(mastersResult.Order);
        order.AddRange(nonMastersResult.Order);
        order.AddRange(blueprintMastersResult.Order);

        List<(string First, string Second)> unlinked =
            [.. mastersResult.Unlinked, .. nonMastersResult.Unlinked, .. blueprintMastersResult.Unlinked];

        return new SortResult(
            order,
            default,
            mastersResult.OverlapEdgesAdded || blueprintMastersResult.OverlapEdgesAdded || nonMastersResult.OverlapEdgesAdded)
        {
            UnlinkedNeighbours = unlinked,
        };
    }

    private readonly record struct PartitionResult(
        IReadOnlyList<string> Order, bool OverlapEdgesAdded, IReadOnlyList<(string First, string Second)> Unlinked);

    private static PartitionResult SortPartition<T>(
        IReadOnlyList<PluginSortingData<T>> plugins,
        GroupsGraph groupsGraph,
        IReadOnlyList<string> earlyLoadingPlugins)
        where T : ISortingPlugin
    {
        var graph = new PluginsGraph<T>();
        foreach (var plugin in plugins) graph.AddNode(plugin);

        graph.AddSpecificEdges();
        graph.AddEarlyLoadingPluginEdges(earlyLoadingPlugins);

        // From here on edges are only added if they do not make a cycle, and overlap and tie-break
        // edges are slow to add, so checking now gives the cheap early failure.
        graph.CheckForCycles();

        graph.AddGroupEdges(groupsGraph);
        var overlapEdgesAdded = graph.AddOverlapEdges();
        graph.AddTieBreakEdges();

        // Again in case a bug let one through. It takes no significant time.
        graph.CheckForCycles();

        var sorted = graph.TopologicalSort();

        // libloot only logs a path with no edge between two neighbours. The tie-break edges are there
        // to rule that out, so a pair here means the result may not be the only valid order.
        var unlinked = new List<(string First, string Second)>();
        if (graph.CheckPathIsHamiltonian(sorted) is var (first, second)) unlinked.Add((graph[first].Name, graph[second].Name));

        return new PartitionResult(sorted.Select(node => graph[node].Name).ToList(), overlapEdgesAdded, unlinked);
    }
}
