using NexusMods.Games.Bethesda.LoadOrder;

namespace NexusMods.Games.Bethesda.Loot.Sorting;

// Follows libloot's sorting/validate.rs.

/// <summary>
/// Checks that the edges sorting is about to add do not contradict master flags, which would
/// otherwise be hidden by sorting masters, non-masters and blueprint masters separately.
/// </summary>
internal static class Validation
{
    /// <exception cref="UndefinedGroupException">A plugin is in a group that the graph does not have.</exception>
    public static void ValidatePluginGroups<T>(IEnumerable<PluginSortingData<T>> plugins, GroupsGraph groupsGraph)
        where T : ISortingPlugin
    {
        var groupNames = new HashSet<string>(StringComparer.Ordinal);
        for (var node = 0; node < groupsGraph.NodeCount; node++) groupNames.Add(groupsGraph.NodeName(node));

        foreach (var plugin in plugins)
        {
            if (!groupNames.Contains(plugin.Group)) throw new UndefinedGroupException(plugin.Group);
        }
    }

    /// <exception cref="PluginGraphValidationException">An edge would put a non-master or blueprint master before a master, or a non-master before an early loader.</exception>
    public static void ValidateSpecificAndHardcodedEdges<T>(
        IReadOnlyList<PluginSortingData<T>> masters,
        IReadOnlyList<PluginSortingData<T>> blueprintMasters,
        IReadOnlyList<PluginSortingData<T>> nonMasters,
        IReadOnlyList<string> earlyLoadingPlugins)
        where T : ISortingPlugin
    {
        var nonMastersSet = new HashSet<string>(nonMasters.Select(p => p.Name), PluginNameComparer.Instance);
        var blueprintMastersSet = new HashSet<string>(blueprintMasters.Select(p => p.Name), PluginNameComparer.Instance);

        try
        {
            foreach (var master in masters) ValidatePlugin(master, nonMastersSet, blueprintMastersSet);

            // An empty set so that the non-masters are not validated against themselves.
            var empty = new HashSet<string>(PluginNameComparer.Instance);
            foreach (var nonMaster in nonMasters) ValidatePlugin(nonMaster, empty, blueprintMastersSet);

            ValidateEarlyLoadingPlugins(earlyLoadingPlugins, masters, nonMastersSet);
        }
        catch (CyclicInteractionException e)
        {
            throw new PluginGraphValidationException(e);
        }
    }

    private static void ValidatePlugin<T>(
        PluginSortingData<T> plugin, HashSet<string> nonMasters, HashSet<string> blueprintMasters)
        where T : ISortingPlugin
    {
        foreach (var master in plugin.Masters)
        {
            if (nonMasters.Contains(master))
            {
                throw new CyclicInteractionException(
                [
                    new Vertex(master, EdgeType.Master),
                    new Vertex(plugin.Name, EdgeType.MasterFlag),
                ]);
            }

            // The game ignores a blueprint master used as a master, and the plugin cannot be fixed
            // without editing it, so this is not an error.
        }

        ValidateFiles(plugin.MasterlistRequirements, plugin.Name, nonMasters, blueprintMasters, EdgeType.MasterlistRequirement);
        ValidateFiles(plugin.UserRequirements, plugin.Name, nonMasters, blueprintMasters, EdgeType.UserRequirement);
        ValidateFiles(plugin.MasterlistLoadAfter, plugin.Name, nonMasters, blueprintMasters, EdgeType.MasterlistLoadAfter);
        ValidateFiles(plugin.UserLoadAfter, plugin.Name, nonMasters, blueprintMasters, EdgeType.UserLoadAfter);
    }

    private static void ValidateFiles(
        IReadOnlyList<string> files,
        string pluginName,
        HashSet<string> nonMasters,
        HashSet<string> blueprintMasters,
        EdgeType edgeType)
    {
        foreach (var file in files)
        {
            if (nonMasters.Contains(file))
            {
                throw new CyclicInteractionException(
                [
                    new Vertex(file, edgeType),
                    new Vertex(pluginName, EdgeType.MasterFlag),
                ]);
            }

            if (blueprintMasters.Contains(file))
            {
                throw new CyclicInteractionException(
                [
                    new Vertex(file, edgeType),
                    new Vertex(pluginName, EdgeType.BlueprintMaster),
                ]);
            }
        }
    }

    private static void ValidateEarlyLoadingPlugins<T>(
        IReadOnlyList<string> earlyLoadingPlugins, IReadOnlyList<PluginSortingData<T>> masters, HashSet<string> nonMasters)
        where T : ISortingPlugin
    {
        if (masters.Count == 0) return;

        foreach (var plugin in earlyLoadingPlugins)
        {
            if (!nonMasters.Contains(plugin)) continue;

            // Only the first master is reported.
            throw new CyclicInteractionException(
            [
                new Vertex(plugin, EdgeType.Hardcoded),
                new Vertex(masters[0].Name, EdgeType.MasterFlag),
            ]);
        }
    }
}
