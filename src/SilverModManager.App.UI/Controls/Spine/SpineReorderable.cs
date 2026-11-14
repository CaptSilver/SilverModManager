using SilverModManager.App.UI.Input.Reorder;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.Controls.Spine;

/// <summary>
/// Moves a loadout's icon along the spine. A move past either end lands on the last icon there; the spine's own
/// <c>MoveLoadout</c> refuses an index outside the list, so the clamp is here.
/// </summary>
internal sealed class SpineReorderable(Func<IReadOnlyList<LoadoutId>> displayed, Action<int, int> moveLoadout) : IReorderable<LoadoutId>
{
    public int CountFor(LoadoutId item) => displayed().Count;

    public int IndexOf(LoadoutId item)
    {
        var index = displayed().ToList().IndexOf(item);
        if (index < 0) throw new InvalidOperationException($"Loadout {item} is not on the spine");
        return index;
    }

    public Task MoveBy(LoadoutId item, int delta, CancellationToken token)
    {
        MoveNow(item, delta);
        return Task.CompletedTask;
    }

    /// <summary>The move itself; it is synchronous, so the icon menu's commands call it without waiting on a task.</summary>
    public void MoveNow(LoadoutId item, int delta)
    {
        var from = IndexOf(item);
        var to = Math.Clamp(from + delta, 0, displayed().Count - 1);
        if (to != from) moveLoadout(from, to);
    }
}
