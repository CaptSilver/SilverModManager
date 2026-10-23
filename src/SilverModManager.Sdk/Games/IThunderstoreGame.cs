using JetBrains.Annotations;

namespace SilverModManager.Sdk.Games;

/// <summary>
/// A game whose mods are published on Thunderstore. It lives in Sdk beside <see cref="IGameData"/> because
/// the library source, the networking project and the game projects all need it, and Sdk is the only
/// project below all of them.
/// </summary>
[PublicAPI]
public interface IThunderstoreGame
{
    /// <summary>
    /// The Thunderstore community slug, e.g. <c>repo</c> in <c>thunderstore.io/c/repo/</c>.
    /// </summary>
    string ThunderstoreCommunity { get; }

    /// <summary>
    /// The <c>Namespace-Name</c> of the package that installs the mod loader for this game.
    /// </summary>
    string ThunderstoreLoaderPackage { get; }

    /// <summary>
    /// Other packages that are the same loader as <see cref="ThunderstoreLoaderPackage"/>; a mod
    /// depending on one of these is satisfied by the game's loader.
    /// </summary>
    IReadOnlyList<string> ThunderstoreLoaderAliases { get; }
}

/// <summary>
/// Access to a game's Thunderstore support.
/// </summary>
[PublicAPI]
public static class ThunderstoreGame
{
    /// <summary>
    /// Returns the game's Thunderstore support, or null if its mods don't come from Thunderstore.
    /// </summary>
    public static IThunderstoreGame? For(IGameData game) => game as IThunderstoreGame;
}
