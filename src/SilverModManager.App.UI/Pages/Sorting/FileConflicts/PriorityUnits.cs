using System.ComponentModel;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.Pages.Sorting;

/// <summary>A priority row with the collection its mod belongs to, if any.</summary>
internal readonly record struct PriorityRow(LoadoutItemGroupPriorityId Id, LoadoutItemGroupId Group, LoadoutItemGroupId? Collection);

/// <summary>
/// What a row of the File Conflicts list stands for. The list holds mods and collections; priority only exists for
/// mods, so a collection is its mods moved together and a drop is worked out in terms of the mods' rows.
/// </summary>
internal static class PriorityUnits
{
    /// <summary>The loadout's priority rows, lowest priority first.</summary>
    public static PriorityRow[] Load(IDb db, LoadoutId loadoutId) => LoadoutItemGroupPriority
        .FindByLoadout(db, loadoutId)
        .Where(static row => row.Target.IsValid())
        .OrderBy(static row => row.Priority)
        .Select(static row =>
        {
            var item = row.Target.AsLoadoutItem();
            var collection = item.HasParent() && item.Parent.TryGetAsCollectionGroup(out var group)
                ? (LoadoutItemGroupId?)group.AsLoadoutItemGroup().LoadoutItemGroupId
                : null;
            return new PriorityRow(row.LoadoutItemGroupPriorityId, row.TargetId, collection);
        })
        .ToArray();

    /// <summary>
    /// The priority rows the dragged or moved <paramref name="keys"/> stand for: a collection's key is all of its
    /// rows, any other key is the row it names. Rows come out lowest priority first and only once; a key that
    /// names nothing the rows know is passed through as a row id.
    /// </summary>
    /// <param name="rows">The loadout's rows, lowest priority first.</param>
    public static LoadoutItemGroupPriorityId[] Expand(IReadOnlyList<PriorityRow> rows, IEnumerable<EntityId> keys)
    {
        var ids = new List<LoadoutItemGroupPriorityId>();
        foreach (var key in keys.Distinct())
        {
            var members = rows.Where(row => row.Collection?.Value == key).Select(static row => row.Id).ToList();
            if (members.Count > 0) ids.AddRange(members);
            else ids.Add(LoadoutItemGroupPriorityId.From(key));
        }

        var wanted = ids.ToHashSet();
        var known = rows.Select(static row => row.Id).Where(wanted.Contains).ToList();

        // Ids the rows don't know are passed through, after the ones they do.
        return known.Concat(wanted.Except(known)).ToArray();
    }

    /// <summary>Whether <paramref name="key"/> is a collection that has mods in <paramref name="rows"/>.</summary>
    public static bool IsCollection(IReadOnlyList<PriorityRow> rows, EntityId key) => rows.Any(row => row.Collection?.Value == key);

    /// <summary>The key of the unit a row is shown as part of: its collection, or the row itself when it has none.</summary>
    public static EntityId UnitOf(IReadOnlyList<PriorityRow> rows, EntityId rowKey)
        => rows.FirstOrDefault(row => row.Id.Value == rowKey) is { Collection: { } collection } ? collection.Value : rowKey;

    /// <summary>The highest-priority row of a collection, or the row <paramref name="key"/> names.</summary>
    public static LoadoutItemGroupPriorityId TopRow(IReadOnlyList<PriorityRow> rows, EntityId key)
    {
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (rows[i].Collection?.Value == key) return rows[i].Id;
        }

        return LoadoutItemGroupPriorityId.From(key);
    }

    /// <summary>
    /// Finds the row that moved rows are to land directly over, or none when they go to the bottom of the order.
    /// </summary>
    /// <param name="rows">The loadout's rows, lowest priority first.</param>
    /// <param name="moved">The rows being moved. They are never the answer: moving a row over itself moves nothing.</param>
    /// <param name="target">The row or collection that was dropped on.</param>
    /// <param name="aboveTarget">Whether the rows go above the target in priority (they would win over it).</param>
    /// <returns>False when the target is not in the rows, so the caller doesn't have to guess a place.</returns>
    public static bool TryFindLoser(
        IReadOnlyList<PriorityRow> rows,
        IReadOnlyCollection<LoadoutItemGroupPriorityId> moved,
        EntityId target,
        bool aboveTarget,
        out LoadoutItemGroupPriorityId? loser)
    {
        loser = null;
        var span = Enumerable.Range(0, rows.Count)
            .Where(i => rows[i].Collection?.Value == target || rows[i].Id.Value == target)
            .ToList();
        if (span.Count == 0) return false;

        // Over the target's highest row, or under its lowest.
        var index = aboveTarget ? span[^1] : span[0] - 1;
        while (index >= 0 && moved.Contains(rows[index].Id)) index--;

        if (index >= 0) loser = rows[index].Id;
        return true;
    }

    /// <summary>
    /// The list shows the lowest priority at the top when ascending and the highest when descending, so a drop above
    /// a row on screen is below it in priority in the first case and above it in the second.
    /// </summary>
    public static bool IsAboveInPriority(ListSortDirection direction, bool visuallyAbove)
        => visuallyAbove == (direction == ListSortDirection.Descending);
}

/// <summary>
/// The order of the File Conflicts list. A mod sits at its own place in the priority order; a collection row sits
/// at the edge of its mods that faces the top of the list, so it reads as their heading.
/// </summary>
internal static class FileConflictsOrder
{
    internal readonly record struct Key(int Position, bool IsCollection);

    public static Key Anchor(bool isCollection, int index, int lowest, int highest, ListSortDirection direction)
        => new(isCollection ? (direction == ListSortDirection.Ascending ? lowest : highest) : index, isCollection);

    public static int Compare(Key a, Key b, ListSortDirection direction)
    {
        var byPosition = a.Position.CompareTo(b.Position);
        if (direction == ListSortDirection.Descending) byPosition = -byPosition;
        if (byPosition != 0) return byPosition;

        // Same place: the collection heads its first mod.
        return b.IsCollection.CompareTo(a.IsCollection);
    }
}
