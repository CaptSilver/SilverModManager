using System.ComponentModel;
using SilverModManager.App.UI.Input.Reorder;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.App.UI.Pages.Sorting;

/// <summary>
/// Moves File Conflicts rows by a number of places. A mod moves among all mods; a collection moves as one unit among the
/// loose mods and the other collections, so stepping it never lands inside a collection or on its own mods. Both are
/// worked out as a drop next to the row at the destination, the same call a drag makes.
/// </summary>
internal sealed class FileConflictsReorderable(
    Func<PriorityRow[]> loadRows,
    Func<ListSortDirection> direction,
    Func<EntityId[], EntityId, bool, Task<bool>> drop) : IReorderable<EntityId>
{
    public int CountFor(EntityId item) => Displayed(loadRows(), item).Count;

    public int IndexOf(EntityId item)
    {
        var index = Displayed(loadRows(), item).IndexOf(item);
        if (index < 0) throw new InvalidOperationException($"Row {item} is not in the priority order");
        return index;
    }

    public async Task MoveBy(EntityId item, int delta, CancellationToken token)
    {
        if (!await TryMoveBy(item, delta, token)) throw new InvalidOperationException($"The move of row {item} was not applied");
    }

    /// <summary>Moves the row; false when it is not in the order or the move was refused and left undone.</summary>
    public async Task<bool> TryMoveBy(EntityId item, int delta, CancellationToken token)
    {
        var displayed = Displayed(loadRows(), item);
        var from = displayed.IndexOf(item);
        if (from < 0) return false;

        var to = Math.Clamp(from + delta, 0, displayed.Count - 1);
        if (to == from) return true;

        return await drop([item], displayed[to], to < from);
    }

    /// <summary>The keys the row moves among, top of the screen first.</summary>
    private List<EntityId> Displayed(PriorityRow[] rows, EntityId item)
    {
        var keys = PriorityUnits.IsCollection(rows, item)
            ? rows.Select(row => PriorityUnits.UnitOf(rows, row.Id.Value)).Distinct().ToList()
            : rows.Select(row => row.Id.Value).ToList();
        if (direction() == ListSortDirection.Descending) keys.Reverse();
        return keys;
    }
}
