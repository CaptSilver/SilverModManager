using JetBrains.Annotations;

namespace SilverModManager.Sdk.Games;

/// <summary>
/// A kind of submission GameBanana lists per game. The <c>Mod</c> section is what most games want; the others are
/// separate listings under the same game.
/// </summary>
[PublicAPI]
public enum GameBananaSection
{
    Mod,
    Skin,
    Sound,
    Wip,
}

/// <summary>
/// A game GameBanana lists mods for.
/// </summary>
[PublicAPI]
public interface IGameBananaGame
{
    /// <summary>
    /// GameBanana's id for the game, the number in <c>https://gamebanana.com/games/{id}</c>.
    /// </summary>
    int GameBananaGameId { get; }

    /// <summary>
    /// The sections the catalog lists for the game, the first being the default.
    /// </summary>
    IReadOnlyList<GameBananaSection> GameBananaSections { get; }
}

/// <summary>
/// The GameBanana side of a game that declares it as data rather than as a hand-written class.
/// </summary>
[PublicAPI]
public sealed record GameBananaSupport(int GameBananaGameId, IReadOnlyList<GameBananaSection> GameBananaSections) : IGameBananaGame;

/// <summary>
/// Access to a game's GameBanana side, through the game's own implementation of the marker or the one its definition declares.
/// </summary>
[PublicAPI]
public static class GameBananaGame
{
    /// <summary>
    /// The game's GameBanana side, or null when GameBanana has no mods for it.
    /// </summary>
    public static IGameBananaGame? For(IGameData game) => game as IGameBananaGame ?? (game as IHasGameSources)?.Sources.GameBanana;
}
