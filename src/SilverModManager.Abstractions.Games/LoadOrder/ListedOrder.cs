namespace SilverModManager.Abstractions.Games.LoadOrder;

/// <summary>Merges what a later run of a source added into an order the user may have changed since the first run.</summary>
public static class ListedOrder
{
    /// <summary>
    /// <paramref name="current"/> as it is, with each item of <paramref name="listed"/> that <paramref name="isNew"/>
    /// accepts put at its listed slot: right after the nearest item listed before it that the result holds, else right
    /// before the nearest one listed after it, else last. A new item already in <paramref name="current"/> is moved
    /// there. Both lists run in the same direction; nothing else in <paramref name="current"/> moves.
    /// </summary>
    public static List<T> InsertNew<T>(IReadOnlyList<T> listed, IReadOnlyList<T> current, Func<T, bool> isNew, IEqualityComparer<T>? comparer = null)
    {
        comparer ??= EqualityComparer<T>.Default;
        var result = current.Where(item => !isNew(item)).ToList();
        var placed = new HashSet<T>(result, comparer);

        for (var i = 0; i < listed.Count; i++)
        {
            var item = listed[i];
            if (!isNew(item) || placed.Contains(item)) continue;

            result.Insert(SlotFor(listed, i, result, placed, comparer), item);
            placed.Add(item);
        }

        return result;
    }

    private static int SlotFor<T>(IReadOnlyList<T> listed, int index, List<T> result, HashSet<T> placed, IEqualityComparer<T> comparer)
    {
        for (var before = index - 1; before >= 0; before--)
        {
            if (placed.Contains(listed[before])) return IndexOf(result, listed[before], comparer) + 1;
        }

        for (var after = index + 1; after < listed.Count; after++)
        {
            if (placed.Contains(listed[after])) return IndexOf(result, listed[after], comparer);
        }

        return result.Count;
    }

    private static int IndexOf<T>(List<T> list, T item, IEqualityComparer<T> comparer) => list.FindIndex(candidate => comparer.Equals(candidate, item));
}
