namespace SilverModManager.App.UI.Input.Reorder;

/// <summary>
/// The one way a page that orders rows moves one. Indexes and deltas are in displayed order: the top row is 0 and a
/// positive delta moves down the screen. A page that shows its list descending translates to its stored order itself.
/// </summary>
public interface IReorderable<TKey>
{
    /// <summary>Moves the row by <paramref name="delta"/> places, stopping at either end. Throws when the move was not applied.</summary>
    Task MoveBy(TKey item, int delta, CancellationToken token);

    /// <summary>Where the row is now, in displayed order.</summary>
    int IndexOf(TKey item);

    /// <summary>
    /// How many places the row can be moved among. It depends on the row: a collection on the File Conflicts page moves
    /// among collections and loose mods, a mod among all mods.
    /// </summary>
    int CountFor(TKey item);
}
