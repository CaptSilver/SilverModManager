using SilverModManager.Games.Bethesda.LoadOrder.Ordering;

namespace SilverModManager.Games.CreationEngine.SortOrder;

/// <summary>
/// The order plugins take when the user has not placed them: what <see cref="PluginOrderResolver"/> makes of
/// plugins that have no index.
/// </summary>
internal static class PluginDefaultOrder
{
    /// <exception cref="PluginOrderCycleException">Plugins in one group list each other as masters.</exception>
    public static IReadOnlyList<PluginLoadoutData> Sort(IReadOnlyList<PluginLoadoutData> plugins)
    {
        var byName = plugins.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var entries = plugins
            .Select(p => new PluginOrderEntry(p.Name, p.IsMaster, p.IsBlueprint, p.Masters, null, p.EarlyLoaderIndex))
            .ToList();

        try
        {
            return PluginOrderResolver.Resolve(entries).Select(name => byName[name]).ToList();
        }
        catch (PluginOrderCycleException)
        {
            // The Load Order page builds its rows from this, and a plugin pair that lists each other must not
            // empty it. The file writer resolves the same plugins and refuses, which is where it gets reported.
            return plugins.OrderBy(p => p.IsMaster ? 0 : 1).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
