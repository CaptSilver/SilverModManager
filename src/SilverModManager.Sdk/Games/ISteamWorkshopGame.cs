using JetBrains.Annotations;

namespace SilverModManager.Sdk.Games;

/// <summary>
/// A game whose mods are Steam Workshop items. Steam downloads and updates them into its own Workshop folder; the app
/// reads that folder and never writes it.
/// </summary>
[PublicAPI]
public interface ISteamWorkshopGame
{
    /// <summary>The app id Steam files the game's Workshop items under, which is not always the game's own.</summary>
    uint SteamWorkshopAppId { get; }

    /// <summary>
    /// Whether the game keeps its own list of enabled mods that the app can write. Where it does not, the Workshop
    /// items are listed and nothing more.
    /// </summary>
    bool HasOrderFile { get; }
}

/// <summary>
/// The Workshop side of a game that declares it as data rather than as a hand-written class.
/// </summary>
[PublicAPI]
public sealed record SteamWorkshopSupport(uint SteamWorkshopAppId, bool HasOrderFile) : ISteamWorkshopGame;

/// <summary>
/// Access to a game's Workshop side, through the game's own implementation of the marker or the one its definition declares.
/// </summary>
[PublicAPI]
public static class SteamWorkshopGame
{
    /// <summary>
    /// The game's Workshop side, or null when the game has no Workshop items.
    /// </summary>
    public static ISteamWorkshopGame? For(IGameData game) => game as ISteamWorkshopGame ?? (game as IHasGameSources)?.Sources.SteamWorkshop;
}
