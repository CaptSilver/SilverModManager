namespace SilverModManager.Abstractions.Games.LoadOrder;

/// <summary>
/// Movement and three-way merge helpers for orders and flags: mods and plugins share them.
/// </summary>
public static class OrderDelta
{
    /// <summary>
    /// Entries of <paramref name="current"/> that are not in the longest common subsequence with
    /// <paramref name="previous"/>, after both are filtered to the keys present in both. Entries in only
    /// one sequence are ignored: they were added or removed, not moved. Keys must be unique per sequence;
    /// a repeated key throws <see cref="ArgumentException"/>.
    /// </summary>
    public static IReadOnlySet<T> Moved<T>(IReadOnlyList<T> previous, IReadOnlyList<T> current, IEqualityComparer<T>? comparer = null)
        where T : notnull
    {
        comparer ??= EqualityComparer<T>.Default;

        var inPrevious = ToUniqueSet(previous, comparer, nameof(previous));
        var inCurrent = ToUniqueSet(current, comparer, nameof(current));

        var indexInPrevious = new Dictionary<T, int>(comparer);
        foreach (var key in previous)
        {
            if (inCurrent.Contains(key)) indexInPrevious[key] = indexInPrevious.Count;
        }

        var b = current.Where(inPrevious.Contains).ToArray();

        // Both filtered sequences hold the same unique keys, so their LCS is the longest increasing
        // subsequence of b's positions in previous. Patience sorting finds it in O(n log n) time and O(n) memory.
        var tails = new List<int>();
        var predecessor = new int[b.Length];
        for (var j = 0; j < b.Length; j++)
        {
            var position = indexInPrevious[b[j]];
            var slot = LowerBound(tails, b, indexInPrevious, position);
            predecessor[j] = slot > 0 ? tails[slot - 1] : -1;
            if (slot == tails.Count) tails.Add(j);
            else tails[slot] = j;
        }

        var kept = new bool[b.Length];
        for (var j = tails.Count > 0 ? tails[^1] : -1; j >= 0; j = predecessor[j]) kept[j] = true;

        var moved = new HashSet<T>(comparer);
        for (var j = 0; j < b.Length; j++)
        {
            if (!kept[j]) moved.Add(b[j]);
        }
        return moved;
    }

    private static HashSet<T> ToUniqueSet<T>(IReadOnlyList<T> keys, IEqualityComparer<T> comparer, string name)
        where T : notnull
    {
        var set = new HashSet<T>(comparer);
        foreach (var key in keys)
        {
            if (!set.Add(key)) throw new ArgumentException($"{name} contains a duplicate key: {key}", name);
        }
        return set;
    }

    // First slot whose tail sits at a position in previous that is not below `position`.
    private static int LowerBound<T>(List<int> tails, T[] b, Dictionary<T, int> indexInPrevious, int position)
        where T : notnull
    {
        var lo = 0;
        var hi = tails.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (indexInPrevious[b[tails[mid]]] < position) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>
    /// Three-way merge of one value against the base it was edited from. A value the user left alone
    /// follows the new one; an edited value stays the user's, and is a conflict when the new one also changed to something else.
    /// </summary>
    public static (T Value, bool Conflict) ThreeWay<T>(T old, T @new, T user, IEqualityComparer<T>? comparer = null)
    {
        comparer ??= EqualityComparer<T>.Default;

        if (comparer.Equals(user, old)) return (@new, false);
        return (user, !comparer.Equals(@new, old) && !comparer.Equals(@new, user));
    }
}
