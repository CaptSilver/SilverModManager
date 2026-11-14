using SilverModManager.App.UI.Input.Reorder;
using SilverModManager.Abstractions.Loadouts;

namespace SilverModManager.App.UI.LeftMenu.Loadout;

/// <summary>
/// Moves a collection among the collections the user can move. The default collection and the entries after the
/// collections keep their places, so a move that would pass them stops at the last movable collection.
/// </summary>
internal sealed class CollectionReorderable(LoadoutCollectionList list) : IReorderable<CollectionGroupId>
{
    public int CountFor(CollectionGroupId item) => list.MovableCollections().Count;

    public int IndexOf(CollectionGroupId item)
    {
        var index = list.MovableCollections().IndexOf(item);
        if (index < 0) throw new InvalidOperationException($"Collection {item} cannot be moved");
        return index;
    }

    public Task MoveBy(CollectionGroupId item, int delta, CancellationToken token)
    {
        MoveNow(item, delta);
        return Task.CompletedTask;
    }

    /// <summary>The move itself; it is synchronous, so the context menu's items call it without waiting on a task.</summary>
    public void MoveNow(CollectionGroupId item, int delta)
    {
        var from = IndexOf(item);
        var to = Math.Clamp(from + delta, 0, list.MovableCollections().Count - 1);
        if (to != from) list.Move(list.FirstMovableIndex + from, list.FirstMovableIndex + to);
    }
}
