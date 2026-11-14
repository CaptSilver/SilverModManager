using SilverModManager.Sdk.Games;

namespace SilverModManager.Library.GitHub;

/// <summary>
/// One repository on a game's GitHub shelf, whether the game declares it, the user added it or both. A declared
/// entry the app has learned the repository id of is both: the row only carries what was learned.
/// </summary>
/// <param name="RepositoryId">The numeric id, once known. It survives a rename of the repository.</param>
/// <param name="Role">What the release is for, shown on the card.</param>
/// <param name="Declared">Whether the game itself lists the repository.</param>
internal sealed record ShelfRepository(
    GameId Game,
    string Owner,
    string Repository,
    ulong? RepositoryId,
    string AssetSuffix,
    string? NameHint,
    string Role,
    bool IncludePrereleases,
    bool Declared)
{
    /// <summary>What the card and the verbs call it: <c>owner/repository</c>.</summary>
    public string Name => $"{Owner}/{Repository}";

    /// <summary>Names the repository regardless of the case it was typed in.</summary>
    public string Key => Name.ToLowerInvariant();

    /// <summary>How to ask GitHub for it: by id when that is known, since the name can change.</summary>
    public GitHubRepositoryRef Reference => RepositoryId is { } id ? GitHubRepositoryRef.FromId(id) : GitHubRepositoryRef.FromName(Owner, Repository);

    /// <summary>The repository's page.</summary>
    public Uri Page => new($"https://github.com/{Owner}/{Repository}");
}
