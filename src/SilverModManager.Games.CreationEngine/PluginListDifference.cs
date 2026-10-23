using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.Bethesda.LoadOrder.Formats;

namespace SilverModManager.Games.CreationEngine;

/// <summary>
/// How the plugin list the game left on disk differs from the one the app would write: plugins it switched off or
/// on, and plugins that load in a different place relative to the rest. Plugins the app does not know, and the
/// ones the game always loads first, are not compared.
/// </summary>
public sealed record PluginListDifference(
    IReadOnlyList<string> SwitchedOff,
    IReadOnlyList<string> SwitchedOn,
    IReadOnlyList<string> Moved)
{
    private const int MaxNames = 10;

    public bool IsEmpty => SwitchedOff.Count + SwitchedOn.Count + Moved.Count == 0;

    /// <param name="expected">The app's list, in load order.</param>
    /// <param name="onDisk">The game's file in file order. A plugin it does not list is inactive.</param>
    /// <param name="comparesSwitches">
    /// False for a file that lists every plugin whether it is on or not (loadorder.txt), where only the order says
    /// anything.
    /// </param>
    public static PluginListDifference Between(IReadOnlyList<PluginListEntry> expected, IReadOnlyList<PluginListEntry> onDisk, bool comparesSwitches = true)
    {
        var known = expected.Where(e => !e.IsEarlyLoader).ToList();

        var disk = new Dictionary<string, (int Index, bool Active)>(PluginNameComparer.Instance);
        var knownNames = known.Select(e => e.Name).ToHashSet(PluginNameComparer.Instance);
        for (var i = 0; i < onDisk.Count; i++)
        {
            if (knownNames.Contains(onDisk[i].Name))
                disk.TryAdd(onDisk[i].Name, (i, onDisk[i].Active));
        }

        var off = comparesSwitches
            ? known.Where(e => e.Active && !(disk.TryGetValue(e.Name, out var d) && d.Active)).Select(e => e.Name).ToList()
            : [];
        var on = comparesSwitches
            ? known.Where(e => !e.Active && disk.TryGetValue(e.Name, out var d) && d.Active).Select(e => e.Name).ToList()
            : [];

        // A plugin that stayed in the longest run still in the app's order did not move; the others did. That
        // counts a plugin dragged across forty others as one move, not forty-one.
        var listed = known.Where(e => disk.ContainsKey(e.Name)).Select(e => e.Name).ToList();
        var stayed = LongestIncreasingRun(listed.Select(name => disk[name].Index).ToList());
        var moved = listed.Where((_, i) => !stayed.Contains(i)).ToList();

        return new PluginListDifference(off, on, moved);
    }

    /// <summary>What several files of one game, each compared on its own, add up to.</summary>
    public static PluginListDifference Combine(params PluginListDifference[] differences) => new(
        Union(differences.SelectMany(d => d.SwitchedOff)),
        Union(differences.SelectMany(d => d.SwitchedOn)),
        Union(differences.SelectMany(d => d.Moved)));

    private static IReadOnlyList<string> Union(IEnumerable<string> names) => names.Distinct(PluginNameComparer.Instance).ToList();

    /// <summary>
    /// The text shown to the user, e.g. <c>1 switched off, 2 moved: A.esp, B.esp, C.esp</c>. Counts of zero are left
    /// out and at most ten names follow.
    /// </summary>
    public string Summary()
    {
        var counts = new List<string>(3);
        if (SwitchedOff.Count > 0) counts.Add($"{SwitchedOff.Count} switched off");
        if (SwitchedOn.Count > 0) counts.Add($"{SwitchedOn.Count} switched on");
        if (Moved.Count > 0) counts.Add($"{Moved.Count} moved");

        var names = SwitchedOff.Concat(SwitchedOn).Concat(Moved).Distinct(PluginNameComparer.Instance).ToList();
        var text = string.Join(", ", counts) + ": " + string.Join(", ", names.Take(MaxNames));
        return names.Count > MaxNames ? $"{text} and {names.Count - MaxNames} more" : text;
    }

    /// <summary>The positions of one longest strictly increasing subsequence of <paramref name="values"/>.</summary>
    private static HashSet<int> LongestIncreasingRun(IReadOnlyList<int> values)
    {
        var tails = new List<int>();
        var previous = new int[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            var lo = 0;
            var hi = tails.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (values[tails[mid]] < values[i]) lo = mid + 1;
                else hi = mid;
            }
            previous[i] = lo > 0 ? tails[lo - 1] : -1;
            if (lo == tails.Count) tails.Add(i);
            else tails[lo] = i;
        }

        var run = new HashSet<int>();
        for (var at = tails.Count > 0 ? tails[^1] : -1; at >= 0; at = previous[at])
            run.Add(at);
        return run;
    }
}
