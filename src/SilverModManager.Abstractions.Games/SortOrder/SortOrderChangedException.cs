using SilverModManager.Sdk.Errors;

namespace SilverModManager.Abstractions.Games;

/// <summary>
/// A variety's persist step rejected a write because the sort order changed after the revision it was based on.
/// Moves catch it and apply the move again to the latest order; it reaches a caller only when the order kept
/// changing through every attempt, and then its message is for the player.
/// </summary>
public class SortOrderChangedException(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "The load order was not changed";
}
