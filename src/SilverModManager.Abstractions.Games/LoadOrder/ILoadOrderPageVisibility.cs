namespace SilverModManager.Abstractions.Games.LoadOrder;

/// <summary>
/// Implemented by a game that has no load order to edit, so the Load Order page would only show a list the game
/// ignores. A game that does not implement it shows the page.
/// </summary>
public interface ILoadOrderPageVisibility
{
    bool HidesLoadOrderPage { get; }
}
