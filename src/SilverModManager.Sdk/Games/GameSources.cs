using JetBrains.Annotations;

namespace SilverModManager.Sdk.Games;

/// <summary>
/// A GitHub repository whose releases carry a tool or loader a game needs, and which asset of a release to take.
/// </summary>
/// <param name="Owner">The repository owner, such as <c>garyttierney</c>.</param>
/// <param name="Repository">The repository name, such as <c>me3</c>.</param>
/// <param name="AssetSuffix">The ending of the release asset's file name, such as <c>.7z</c>.</param>
/// <param name="NameHint">Text the right asset's name contains, to pick among several with the same ending; null when the ending is enough.</param>
/// <param name="Role">What the repository's release is for, shown beside it ("Mod loader").</param>
[PublicAPI]
public sealed record GitHubShelfEntryDefinition(string Owner, string Repository, string AssetSuffix, string? NameHint, string Role)
{
    /// <summary>
    /// The repository's numeric id when the game knows it, which saves the app a request to learn it and keeps a
    /// release it already downloaded matched to this entry after a rename.
    /// </summary>
    public ulong? RepositoryId { get; init; }
}

/// <summary>
/// A game whose tools and loaders are published as GitHub releases, which the app lists on a shelf page.
/// </summary>
[PublicAPI]
public interface IGitHubShelfGame
{
    /// <summary>The releases the shelf lists for this game.</summary>
    IReadOnlyList<GitHubShelfEntryDefinition> GitHubShelf { get; }
}

/// <summary>
/// The shelf of a game that declares it as data rather than as a hand-written class.
/// </summary>
[PublicAPI]
public sealed record GitHubShelfSupport(IReadOnlyList<GitHubShelfEntryDefinition> GitHubShelf) : IGitHubShelfGame;

/// <summary>
/// Access to a game's GitHub shelf, through the game's own implementation of the marker or the one its definition declares.
/// </summary>
[PublicAPI]
public static class GitHubShelfGame
{
    /// <summary>
    /// The game's GitHub shelf, or null when none of its tools come from GitHub releases.
    /// </summary>
    public static IGitHubShelfGame? For(IGameData game) => game as IGitHubShelfGame ?? (game as IHasGameSources)?.Sources.GitHubShelf;
}

/// <summary>
/// What a game declares about where its mods and tools come from. Each member implements one source's marker
/// interface and is null when that source does not serve the game. A game that is a hand-written class implements the
/// marker itself; a game that is a definition record sets the member, because a record cannot implement an interface
/// per game. Every source finds its games through its marker's <c>For</c>, never through a cast alone.
/// </summary>
[PublicAPI]
public sealed record GameSources(IGitHubShelfGame? GitHubShelf = null, IGameBananaGame? GameBanana = null, ISteamWorkshopGame? SteamWorkshop = null, IModIoGame? ModIo = null)
{
    /// <summary>A game no source serves.</summary>
    public static GameSources None { get; } = new();
}

/// <summary>
/// A game that carries <see cref="GameSources"/>, which is what a definition-built game does.
/// </summary>
[PublicAPI]
public interface IHasGameSources
{
    GameSources Sources { get; }
}
