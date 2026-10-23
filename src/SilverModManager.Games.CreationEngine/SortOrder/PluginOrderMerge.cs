using NexusMods.Abstractions.Games;

namespace NexusMods.Games.CreationEngine.SortOrder;

/// <summary>
/// Combines a plugin list from outside (the game's own file, a modlist's profile) with the rows a loadout has, into
/// the complete order to store.
/// </summary>
internal static class PluginOrderMerge
{
    /// <summary>
    /// The locked rows first, then the <paramref name="listed"/> names that are not early loaders, then every
    /// existing unlocked row the list does not mention, in the order it has.
    /// </summary>
    /// <remarks>
    /// A stored order only moves the rows it names, so leaving the unlisted rows out would let them keep indexes
    /// that collide with the new ones. A listed name that has a row takes the row's spelling; one that has none
    /// still gets an entry, which stays hidden until the plugin arrives. The early loaders stay in the locked block
    /// whatever the list says about them. A null switch leaves a row's switch as it is.
    /// </remarks>
    public static IReadOnlyList<(string Name, bool? IsEnabled)> Build(
        IReadOnlyList<PluginSortData> existingRows,
        IReadOnlyList<(string Name, bool? IsEnabled)> listed,
        ISet<string> earlyLoaders)
    {
        var rows = existingRows.OrderBy(row => row.SortIndex).ToList();
        var rowsByKey = rows.ToDictionary(row => row.Key);
        var seen = new HashSet<SortItemKey<string>>();
        var named = new List<(string Name, bool? IsEnabled)>();
        foreach (var (name, enabled) in listed)
        {
            var key = PluginSortData.KeyFor(name);
            if (!seen.Add(key)) continue;

            if (rowsByKey.TryGetValue(key, out var row))
            {
                if (row.EarlyLoaderIndex is null) named.Add((row.Name, enabled));
            }
            else if (!earlyLoaders.Contains(name))
            {
                named.Add((name, enabled));
            }
        }

        return rows.Where(row => row.EarlyLoaderIndex is not null).Select(row => (row.Name, (bool?)null))
            .Concat(named)
            .Concat(rows.Where(row => row.EarlyLoaderIndex is null && !seen.Contains(row.Key)).Select(row => (row.Name, (bool?)null)))
            .ToList();
    }
}
