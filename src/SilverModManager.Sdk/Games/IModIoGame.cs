using JetBrains.Annotations;

namespace SilverModManager.Sdk.Games;

/// <summary>
/// A game mod.io hosts mods for.
/// </summary>
[PublicAPI]
public interface IModIoGame
{
    /// <summary>
    /// mod.io's numeric id for the game, the <c>game_id</c> its API takes in <c>/games/{id}/mods</c>.
    /// </summary>
    uint ModIoGameId { get; }

    /// <summary>
    /// The game's name id, the last part of <c>https://mod.io/g/{name-id}</c>.
    /// </summary>
    string ModIoNameId { get; }

    /// <summary>
    /// Whether the game's own mod manager downloads the signed-in account's mod.io subscriptions. Where it does, the app
    /// subscribes the account and downloads nothing. Where this has not been confirmed the app downloads the file
    /// itself.
    /// </summary>
    bool GameSyncsSubscriptions { get; }
}

/// <summary>
/// The mod.io side of a game that declares it as data rather than as a hand-written class.
/// </summary>
[PublicAPI]
public sealed record ModIoSupport(uint ModIoGameId, string ModIoNameId, bool GameSyncsSubscriptions) : IModIoGame;

/// <summary>
/// Access to a game's mod.io side, through the game's own implementation of the marker or the one its definition declares.
/// </summary>
[PublicAPI]
public static class ModIoGame
{
    /// <summary>
    /// The game's mod.io side, or null when mod.io has no mods for it.
    /// </summary>
    public static IModIoGame? For(IGameData game) => game as IModIoGame ?? (game as IHasGameSources)?.Sources.ModIo;
}
