namespace SilverModManager.Abstractions.Games.LoadOrder;

/// <summary>Whether a game has the Load Order page, which edits the mod priority rows.</summary>
public static class LoadOrderPage
{
    /// <summary>
    /// A game with a sort order of its own edits that instead, and a game can hide the page when the order it would
    /// show is one the game does not read.
    /// </summary>
    public static bool IsShown(IGame game)
        => game.SortOrderManager.GetSortOrderVarieties().Count == 0 && game is not ILoadOrderPageVisibility { HidesLoadOrderPage: true };
}
