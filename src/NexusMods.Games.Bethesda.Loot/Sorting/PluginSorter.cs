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
        => Sort(plugins, groupsGraph, earlyLoadingPlugins, SortStages.All);

    internal static SortResult Sort<T>(
        IReadOnlyList<PluginSortingData<T>> plugins,
        GroupsGraph groupsGraph,
        IReadOnlyList<string> earlyLoadingPlugins,
        SortStages stages)
        where T : ISortingPlugin
    {
        if (plugins.Count == 0) return new SortResult([], default);

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
        var mastersOrder = SortPartition(masters, groupsGraph, earlyLoadingPlugins, stages);
        var blueprintMastersOrder = SortPartition(blueprintMasters, groupsGraph, earlyLoadingPlugins, stages);
        var nonMastersOrder = SortPartition(nonMasters, groupsGraph, earlyLoadingPlugins, stages);

        var order = new List<string>(byName.Count);
        order.AddRange(mastersOrder);
        order.AddRange(nonMastersOrder);
        order.AddRange(blueprintMastersOrder);

        return new SortResult(order, default);
    }

    private static IReadOnlyList<string> SortPartition<T>(
        IReadOnlyList<PluginSortingData<T>> plugins,
        GroupsGraph groupsGraph,
        IReadOnlyList<string> earlyLoadingPlugins,
        SortStages stages)
        where T : ISortingPlugin
    {
        if ((stages & ~SortStages.Groups) != SortStages.None)
        {
            throw new NotImplementedException($"Sorting stages {stages & ~SortStages.Groups} are not implemented yet.");
        }

        var graph = new PluginsGraph<T>();
        foreach (var plugin in plugins) graph.AddNode(plugin);

        graph.AddSpecificEdges();
        graph.AddEarlyLoadingPluginEdges(earlyLoadingPlugins);

        // Later stages only add edges that do not make cycles, so checking now gives the cheap
        // early failure.
        graph.CheckForCycles();

        if (stages.HasFlag(SortStages.Groups)) graph.AddGroupEdges(groupsGraph);

        // Overlap and tie-break edges are added here.

        graph.CheckForCycles();

        var sorted = graph.TopologicalSort();
        return sorted.Select(node => graph[node].Name).ToList();
    }
}
