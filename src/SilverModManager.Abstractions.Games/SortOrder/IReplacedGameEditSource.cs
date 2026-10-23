using NexusMods.Abstractions.Loadouts;

namespace NexusMods.Abstractions.Games;

/// <summary>
/// A sort order variety that notices when something outside the app rewrote the game's own order file and the
/// app replaced that edit with the loadout's order, so the Load Order page can tell the user.
/// </summary>
public interface IReplacedGameEditSource
{
    /// <summary>
    /// Emits a summary of the replaced edit while one is waiting to be acknowledged, and null once there is none.
    /// </summary>
    IObservable<string?> ReplacedEditSummary(SortOrderId sortOrderId);

    /// <summary>
    /// Acknowledges the notice so <see cref="ReplacedEditSummary"/> goes back to null.
    /// </summary>
    Task Dismiss(SortOrderId sortOrderId);
}
