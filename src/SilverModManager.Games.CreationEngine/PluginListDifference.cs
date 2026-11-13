using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.Bethesda.LoadOrder.Formats;

namespace SilverModManager.Games.CreationEngine;

/// <summary>
/// How the plugin list the game left on disk differs from the one the app would write: plugins it switched off or
/// on, plugins that load in a different place relative to the rest, and (openmw.cfg) plugins loaded under the other
/// of <c>content=</c> and <c>groundcover=</c>. Plugins the app does not know, and the
/// ones the game always loads first, are not compared.
/// </summary>
public sealed record PluginListDifference(
    IReadOnlyList<string> SwitchedOff,
    IReadOnlyList<string> SwitchedOn,
    IReadOnlyList<string> Moved,
    IReadOnlyList<string> GroundcoverChanged)
{
    public PluginListDifference(IReadOnlyList<string> switchedOff, IReadOnlyList<string> switchedOn, IReadOnlyList<string> moved)
        : this(switchedOff, switchedOn, moved, []) { }

    private const int MaxNames = 10;

    public bool IsEmpty => SwitchedOff.Count + SwitchedOn.Count + Moved.Count + GroundcoverChanged.Count == 0;

    /// <param name="expected">The app's list, in load order.</param>
    /// <param name="onDisk">The game's file in file order. A plugin it does not list is inactive.</param>
    /// <param name="comparesSwitches">
    /// False for a file that lists every plugin whether it is on or not (loadorder.txt), where only the order says
    /// anything.
    /// </param>
    public static PluginListDifference Between(IReadOnlyList<PluginListEntry> expected, IReadOnlyList<PluginListEntry> onDisk, bool comparesSwitches = true)
    {
        var known = expected.Where(e => !e.IsEarlyLoader).ToList();

        var disk = new Dictionary<string, (int Index, bool Active, bool Groundcover)>(PluginNameComparer.Instance);
        var knownNames = known.Select(e => e.Name).ToHashSet(PluginNameComparer.Instance);
        for (var i = 0; i < onDisk.Count; i++)
        {
            if (knownNames.Contains(onDisk[i].Name))
                disk.TryAdd(onDisk[i].Name, (i, onDisk[i].Active, onDisk[i].Groundcover));
        }

        var off = comparesSwitches
            ? known.Where(e => e.Active && !(disk.TryGetValue(e.Name, out var d) && d.Active)).Select(e => e.Name).ToList()
            : [];
        var on = comparesSwitches
            ? known.Where(e => !e.Active && disk.TryGetValue(e.Name, out var d) && d.Active).Select(e => e.Name).ToList()
            : [];

        // Only openmw.cfg has two keys; any other file parses every entry as content, so the flag never differs there.
        var regrouped = comparesSwitches
            ? known.Where(e => e.Active && disk.TryGetValue(e.Name, out var d) && d.Active && d.Groundcover != e.Groundcover).Select(e => e.Name).ToList()
            : [];

        // A plugin that stayed in the longest run still in the app's order did not move; the others did. That
        // counts a plugin dragged across forty others as one move, not forty-one. openmw.cfg writes its groundcover=
        // lines as a block after the content= lines, and OpenMW orders each key on its own, so each key is its own
        // run; a plugin that changed key is already reported as such.
        var regroupedNames = regrouped.ToHashSet(PluginNameComparer.Instance);
        var listed = known.Where(e => disk.ContainsKey(e.Name) && !regroupedNames.Contains(e.Name)).Select(e => e.Name).ToList();
        var movedNames = listed
            .GroupBy(name => disk[name].Groundcover)
            .SelectMany(key =>
            {
                var names = key.ToList();
                var stayed = LongestIncreasingRun(names.Select(name => disk[name].Index).ToList());
                return names.Where((_, i) => !stayed.Contains(i));
            })
            .ToHashSet(PluginNameComparer.Instance);
        var moved = listed.Where(movedNames.Contains).ToList();

        return new PluginListDifference(off, on, moved, regrouped);
    }

    /// <summary>What several files of one game, each compared on its own, add up to.</summary>
    public static PluginListDifference Combine(params PluginListDifference[] differences) => new(
        Union(differences.SelectMany(d => d.SwitchedOff)),
        Union(differences.SelectMany(d => d.SwitchedOn)),
        Union(differences.SelectMany(d => d.Moved)),
        Union(differences.SelectMany(d => d.GroundcoverChanged)));

    private static IReadOnlyList<string> Union(IEnumerable<string> names) => names.Distinct(PluginNameComparer.Instance).ToList();

    /// <summary>
    /// The text shown to the user, e.g. <c>1 switched off, 2 moved: A.esp, B.esp, C.esp</c>. Counts of zero are left
    /// out and at most ten names follow.
    /// </summary>
    public string Summary()
    {
        var counts = new List<string>(4);
        if (SwitchedOff.Count > 0) counts.Add($"{SwitchedOff.Count} switched off");
        if (SwitchedOn.Count > 0) counts.Add($"{SwitchedOn.Count} switched on");
        if (Moved.Count > 0) counts.Add($"{Moved.Count} moved");
        if (GroundcoverChanged.Count > 0) counts.Add($"{GroundcoverChanged.Count} groundcover changed");

        var names = SwitchedOff.Concat(SwitchedOn).Concat(Moved).Concat(GroundcoverChanged).Distinct(PluginNameComparer.Instance).ToList();
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
