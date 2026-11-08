using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Games.Bethesda.LoadOrder;

namespace SilverModManager.Games.CreationEngine.SortOrder;

/// <summary>
/// Moves a loadout's plugin order from one version of a modlist's profile to the next without touching what the
/// user arranged: only plugins the list added or moved change place, and only switches the list changed change.
/// </summary>
internal static class PluginOrderDelta
{
    /// <summary>
    /// The complete target order: the user's rows as they are, with plugins that are new in <paramref name="current"/>,
    /// or moved between <paramref name="previous"/> and <paramref name="current"/> while the user left their predecessor alone, placed
    /// after their <paramref name="current"/> predecessor; switches by three-way merge. Every row is returned
    /// (untouched ones with a null switch) so a stored order keeps its indexes consistent.
    /// </summary>
    /// <remarks>
    /// <see cref="PluginOrderMerge.Build"/> hoists every named row to the top, which would move the user's
    /// untouched plugins. A plugin both sides moved stays where the user put it and is added to
    /// <paramref name="conflicts"/> when the list wanted it somewhere else.
    /// </remarks>
    public static IReadOnlyList<(string Name, bool? IsEnabled)> Build(
        IReadOnlyList<PluginSortData> existingRows,
        IReadOnlyList<(string Name, bool? IsEnabled)> previous,
        IReadOnlyList<(string Name, bool? IsEnabled)> current,
        ISet<string> earlyLoaders,
        ICollection<string> conflicts)
    {
        var comparer = PluginNameComparer.Instance;
        var rows = existingRows.OrderBy(row => row.SortIndex).ToList();
        var locked = rows.Where(row => row.EarlyLoaderIndex is not null).ToList();
        var lockedNames = locked.Select(row => row.Name).ToHashSet(comparer);
        var unlocked = rows.Where(row => row.EarlyLoaderIndex is null).ToList();
        var rowsByName = unlocked.ToDictionary(row => row.Name, comparer);

        bool IsLocked(string name) => lockedNames.Contains(name) || earlyLoaders.Contains(name);

        var previousSwitches = Switches(previous, IsLocked);
        var currentSwitches = Switches(current, IsLocked);
        var previousNames = previousSwitches.Keys.ToList();
        var currentNames = currentSwitches.Keys.ToList();
        var userNames = unlocked.Select(row => row.Name).ToList();

        var previousSet = previousNames.ToHashSet(comparer);
        var currentSet = currentNames.ToHashSet(comparer);
        var listMoved = OrderDelta.Moved(previousNames, currentNames, comparer);

        // Predecessors are compared among the plugins all three orders share, so a plugin the user added
        // themselves, or one only one profile has, never reads as a move.
        var shared = userNames.Where(n => previousSet.Contains(n) && currentSet.Contains(n)).ToHashSet(comparer);
        var previousShared = previousNames.Where(shared.Contains).ToList();
        var currentShared = currentNames.Where(shared.Contains).ToList();
        var userShared = userNames.Where(shared.Contains).ToList();

        var placed = new List<string>(userNames);
        var reposition = new List<string>();
        foreach (var name in currentNames)
        {
            if (!previousSet.Contains(name) || !rowsByName.ContainsKey(name))
            {
                reposition.Add(name);
            }
            else if (listMoved.Contains(name))
            {
                var old = Predecessor(previousShared, name, comparer);
                var user = Predecessor(userShared, name, comparer);
                if (comparer.Equals(old, user))
                {
                    reposition.Add(name);
                    continue;
                }

                var (_, conflict) = OrderDelta.ThreeWay(old, Predecessor(currentShared, name, comparer), user, comparer);
                if (conflict) conflicts.Add(rowsByName[name].Name);
            }
        }

        foreach (var name in reposition) placed.RemoveAll(existing => comparer.Equals(existing, name));
        foreach (var name in reposition)
        {
            var spelled = rowsByName.TryGetValue(name, out var row) ? row.Name : name;
            var at = 0;
            for (var i = currentNames.FindIndex(n => comparer.Equals(n, name)) - 1; i >= 0; i--)
            {
                var found = placed.FindIndex(existing => comparer.Equals(existing, currentNames[i]));
                if (found < 0) continue;
                at = found + 1;
                break;
            }
            placed.Insert(at, spelled);
        }

        var result = locked.Select(row => (row.Name, (bool?)null)).ToList();
        foreach (var name in placed)
        {
            result.Add((name, SwitchFor(name, rowsByName, previousSwitches, currentSwitches)));
        }
        return result;
    }

    // The listed switch when the user's row does not already say it; null leaves the row as it is.
    private static bool? SwitchFor(
        string name,
        Dictionary<string, PluginSortData> rowsByName,
        Dictionary<string, bool?> previousSwitches,
        Dictionary<string, bool?> currentSwitches)
    {
        if (!currentSwitches.TryGetValue(name, out var listed) || listed is null) return null;
        if (!rowsByName.TryGetValue(name, out var row)) return listed;

        // A list that did not mention the plugin before gives no base to tell the user's edit from the default.
        var value = previousSwitches.GetValueOrDefault(name) is { } old
            ? OrderDelta.ThreeWay(old, listed.Value, row.IsEnabled).Value
            : listed.Value;
        return value == row.IsEnabled ? null : value;
    }

    private static Dictionary<string, bool?> Switches(IReadOnlyList<(string Name, bool? IsEnabled)> list, Func<string, bool> isLocked)
    {
        var result = new Dictionary<string, bool?>(PluginNameComparer.Instance);
        foreach (var (name, enabled) in list)
        {
            if (!isLocked(name)) result.TryAdd(name, enabled);
        }
        return result;
    }

    private static string? Predecessor(IReadOnlyList<string> names, string name, StringComparer comparer)
    {
        for (var i = 1; i < names.Count; i++)
        {
            if (comparer.Equals(names[i], name)) return names[i - 1];
        }
        return null;
    }
}
