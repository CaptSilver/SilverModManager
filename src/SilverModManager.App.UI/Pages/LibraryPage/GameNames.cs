using SilverModManager.Sdk.Games;

namespace SilverModManager.App.UI.Pages.LibraryPage;

/// <summary>Display names for the games the app knows, for text that names a game by its id.</summary>
public sealed class GameNames(IEnumerable<IGameData> games)
{
    private readonly Dictionary<GameId, string> _names = games
        .GroupBy(game => game.GameId)
        .ToDictionary(group => group.Key, group => group.First().DisplayName);

    /// <summary>The game's display name, or its id when no registered game has it.</summary>
    public string NameOf(GameId game) => _names.TryGetValue(game, out var name) ? name : game.ToString();
}
