using NexusMods.Abstractions.Loadouts;

namespace NexusMods.Abstractions.Games;

/// <summary>
/// A sort order variety that can stop writing its list and hand the file back to the game, because something the
/// game installed took the list over. The Load Order page shows the summary and lets the user take the list back.
/// </summary>
public interface IHeldGameEditSource
{
    /// <summary>
    /// Emits a summary of why the variety is holding back while it does, and null once it is writing again.
    /// </summary>
    IObservable<string?> HoldSummary(SortOrderId sortOrderId);

    /// <summary>
    /// Ends the hold so the variety writes the loadout's order again.
    /// </summary>
    Task Release(SortOrderId sortOrderId);
}
