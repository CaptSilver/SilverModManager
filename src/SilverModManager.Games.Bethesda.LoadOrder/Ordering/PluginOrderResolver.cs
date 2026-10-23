// Follows libloadorder src/load_order/mutable.rs (hoist_masters, move_elements, early-loader handling)
// and libloot src/sorting/plugins.rs (the master / non-master / blueprint partitions).
namespace SilverModManager.Games.Bethesda.LoadOrder.Ordering;

/// <summary>
/// Turns the user's arrangement into a load order the game accepts: early loaders first, then masters,
/// non-masters and blueprint masters, each partition keeping the user's order wherever masters allow it,
/// and finally masters hoisted the way the game would hoist them. The result passes
/// <see cref="PluginOrderValidation.Validate"/>.
/// </summary>
public static class PluginOrderResolver
{
    /// <exception cref="ArgumentException">Two entries share a name.</exception>
    /// <exception cref="PluginOrderCycleException">Plugins in one partition need each other as masters.</exception>
    public static IReadOnlyList<string> Resolve(IReadOnlyList<PluginOrderEntry> entries)
    {
        var lookup = IndexEntries(entries);

        var early = entries
            .Where(e => e.LoadsEarly)
            .OrderBy(e => e.EarlyLoaderIndex)
            .ThenBy(e => e.Name, PluginNameComparer.Instance)
            .ToList();
        var rest = entries.Except(early).ToList();

        var order = new List<PluginOrderEntry>(entries.Count);
        order.AddRange(early);
        order.AddRange(SortPartition(rest.Where(e => e.IsMaster && !e.IsBlueprintMaster).ToList()));
        order.AddRange(SortPartition(rest.Where(e => !e.IsMaster).ToList()));
        order.AddRange(SortPartition(rest.Where(e => e.IsBlueprintMaster).ToList()));

        HoistMasters(order);
        return order.Select(e => e.Name).ToList();
    }

    internal static Dictionary<string, PluginOrderEntry> IndexEntries(IReadOnlyList<PluginOrderEntry> entries)
    {
        var lookup = new Dictionary<string, PluginOrderEntry>(entries.Count, PluginNameComparer.Instance);
        foreach (var entry in entries)
        {
            if (!lookup.TryAdd(entry.Name, entry))
                throw new ArgumentException($"Plugin '{entry.Name}' appears more than once.", nameof(entries));
        }

        return lookup;
    }

    /// <summary>
    /// Kahn's algorithm over "loads after each master in this partition", taking the available plugin
    /// with the lowest index at each step. Masters outside the partition or not installed add no edge;
    /// hoisting and the partition order handle the first, and the second is reported elsewhere.
    /// </summary>
    private static List<PluginOrderEntry> SortPartition(List<PluginOrderEntry> partition)
    {
        var byName = new Dictionary<string, PluginOrderEntry>(partition.Count, PluginNameComparer.Instance);
        foreach (var plugin in partition) byName[plugin.Name] = plugin;

        var blockedBy = new Dictionary<string, int>(partition.Count, PluginNameComparer.Instance);
        var dependents = new Dictionary<string, List<PluginOrderEntry>>(partition.Count, PluginNameComparer.Instance);
        foreach (var plugin in partition)
        {
            var needs = plugin.Masters
                .Where(m => byName.ContainsKey(m) && !PluginNameComparer.Instance.Equals(m, plugin.Name))
                .Distinct(PluginNameComparer.Instance)
                .ToList();
            blockedBy[plugin.Name] = needs.Count;
            foreach (var master in needs)
            {
                if (!dependents.TryGetValue(master, out var list)) dependents[master] = list = [];
                list.Add(plugin);
            }
        }

        var available = new PriorityQueue<PluginOrderEntry, PluginOrderEntry>(UserOrder.Instance);
        foreach (var plugin in partition.Where(p => blockedBy[p.Name] == 0)) available.Enqueue(plugin, plugin);

        var sorted = new List<PluginOrderEntry>(partition.Count);
        while (available.TryDequeue(out var next, out _))
        {
            sorted.Add(next);
            if (!dependents.TryGetValue(next.Name, out var waiting)) continue;
            foreach (var dependent in waiting)
            {
                if (--blockedBy[dependent.Name] == 0) available.Enqueue(dependent, dependent);
            }
        }

        if (sorted.Count < partition.Count)
            throw new PluginOrderCycleException(partition.Where(p => blockedBy[p.Name] > 0).Select(p => p.Name).ToList());

        return sorted;
    }

    /// <summary>
    /// A master file whose master loads later makes the game load that master directly before it, so the
    /// list is rewritten to say so. A plugin wanted by several masters moves for the earliest one, which
    /// also satisfies the later ones.
    /// </summary>
    private static void HoistMasters(List<PluginOrderEntry> plugins)
    {
        // Names resolve to the first plugin of that name, and to the first non-blueprint one for a
        // non-blueprint dependent, so keep both lookups instead of scanning the list per master.
        var firstAny = new Dictionary<string, int>(plugins.Count, PluginNameComparer.Instance);
        var firstNonBlueprint = new Dictionary<string, int>(plugins.Count, PluginNameComparer.Instance);
        for (var i = 0; i < plugins.Count; i++)
        {
            firstAny.TryAdd(plugins[i].Name, i);
            if (!plugins[i].IsBlueprintMaster) firstNonBlueprint.TryAdd(plugins[i].Name, i);
        }

        var moves = new SortedDictionary<int, int>();
        for (var index = 0; index < plugins.Count; index++)
        {
            var plugin = plugins[index];
            if (!plugin.IsMaster) continue;

            // The game loads an early loader first whatever it needs, so nothing is moved in front of one.
            if (plugin.LoadsEarly) continue;

            // A non-blueprint master cannot be hoisted by looking at a blueprint file, so such a
            // file is skipped when searching for the master's position.
            var positions = plugin.IsBlueprintMaster ? firstAny : firstNonBlueprint;
            foreach (var master in plugin.Masters)
            {
                if (positions.TryGetValue(master, out var pos) && pos > index) moves.TryAdd(pos, index);
            }
        }

        MoveElements(plugins, moves);
    }

    private static void MoveElements<T>(List<T> list, SortedDictionary<int, int> fromTo)
    {
        // Moves go earliest source first, so a source index is never disturbed by an earlier move, but a
        // destination can be: moving an element from after it to before it shifts it up by one.
        var pending = fromTo.ToList();
        for (var i = 0; i < pending.Count; i++)
        {
            var (from, to) = (pending[i].Key, pending[i].Value);
            var item = list[from];
            list.RemoveAt(from);
            list.Insert(to, item);

            for (var j = i + 1; j < pending.Count; j++)
            {
                if (pending[j].Value < from && pending[j].Value > to)
                    pending[j] = new KeyValuePair<int, int>(pending[j].Key, pending[j].Value + 1);
            }
        }
    }

    /// <summary>Lowest index first, no index last, then by name.</summary>
    private sealed class UserOrder : IComparer<PluginOrderEntry>
    {
        public static readonly UserOrder Instance = new();

        public int Compare(PluginOrderEntry? x, PluginOrderEntry? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return 1;
            if (y is null) return -1;

            var byIndex = (x.SortIndex, y.SortIndex) switch
            {
                ({ } a, { } b) => a.CompareTo(b),
                ({ }, null) => -1,
                (null, { }) => 1,
                _ => 0,
            };
            return byIndex != 0 ? byIndex : PluginNameComparer.Instance.Compare(x.Name, y.Name);
        }
    }
}
