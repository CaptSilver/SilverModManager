using System.Text.RegularExpressions;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Library.GitHub;

/// <summary>
/// The repositories on each game's GitHub shelf: the ones the game declares plus the ones the user added, which are
/// <see cref="GitHubShelfEntry"/> rows.
/// </summary>
internal sealed partial class GitHubShelfEntries(IConnection connection, IEnumerable<IGameData> games)
{
    // GitHub's own limits on names, loosely: they end up in request paths, so anything outside them is refused.
    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9_-]*[A-Za-z0-9])?$")]
    private static partial Regex OwnerPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex RepositoryPattern();

    /// <summary>
    /// Reads <c>owner/repository</c> or a github.com address that starts with it (the repository, its releases page, a
    /// tag). False for anything else.
    /// </summary>
    public static bool TryParseRepository(string text, out string owner, out string repository)
    {
        owner = repository = "";
        var path = text.Trim();
        foreach (var prefix in (ReadOnlySpan<string>)["https://github.com/", "http://github.com/", "https://www.github.com/", "github.com/", "www.github.com/"])
        {
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            path = path[prefix.Length..];
            break;
        }

        if (path.Contains("://", StringComparison.Ordinal)) return false;

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;

        var name = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        if (!OwnerPattern().IsMatch(parts[0]) || !RepositoryPattern().IsMatch(name) || name is "." or "..") return false;

        owner = parts[0];
        repository = name;
        return true;
    }

    /// <summary>The game's shelf: what it declares, then what the user added, each repository once.</summary>
    public IReadOnlyList<ShelfRepository> For(IGameData game)
    {
        var rows = RowsOf(game.GameId);
        var listed = new List<ShelfRepository>();

        foreach (var declared in GitHubShelfGame.For(game)?.GitHubShelf ?? [])
        {
            var row = rows.FirstOrDefault(candidate => Same(candidate, declared.Owner, declared.Repository));
            listed.Add(new ShelfRepository(game.GameId, declared.Owner, declared.Repository, row?.RepositoryId ?? declared.RepositoryId,
                declared.AssetSuffix, declared.NameHint, declared.Role, row?.IncludePrereleases ?? false, Declared: true));
        }

        foreach (var row in rows)
        {
            if (listed.Any(entry => entry.Key == row.Key)) continue;
            listed.Add(row);
        }

        return listed;
    }

    /// <summary>Whether the game has anything on its shelf.</summary>
    public bool Any(IGameData game) => GitHubShelfGame.For(game) is { GitHubShelf.Count: > 0 } || RowsOf(game.GameId).Count > 0;

    /// <summary>
    /// The shelf repository a library file came from, whichever game lists it, or null when none does. A file is
    /// matched by repository id when it has one and by name otherwise.
    /// </summary>
    public ShelfRepository? Find(GitHubReleaseLibraryFile.ReadOnly file)
    {
        var id = GitHubReleaseLibraryFile.RepositoryId.TryGetValue(file, out var recorded) ? recorded : (ulong?)null;
        var owner = GitHubReleaseLibraryFile.Organization.TryGetValue(file, out var org) ? org : null;
        var name = GitHubReleaseLibraryFile.Repository.TryGetValue(file, out var repo) ? repo : null;

        ShelfRepository? found = null;
        foreach (var entry in games.SelectMany(For).Concat(GitHubShelfEntry.All(connection.Db).Select(ToRepository)))
        {
            var matches = (id is not null && entry.RepositoryId == id)
                          || (owner is not null && name is not null && string.Equals(entry.Owner, owner, StringComparison.OrdinalIgnoreCase) && string.Equals(entry.Repository, name, StringComparison.OrdinalIgnoreCase));
            if (!matches) continue;

            // The same repository can be on several shelves; one that opted into prereleases decides for all of them.
            found = found is null || (entry.IncludePrereleases && !found.IncludePrereleases) ? entry : found;
        }

        return found;
    }

    /// <summary>
    /// Puts a repository on a game's shelf. <paramref name="repository"/> is <c>owner/repository</c> or a github.com address.
    /// </summary>
    public async Task<ShelfRepository> Add(GameId game, string repository, string assetSuffix, string? nameHint, bool includePrereleases)
    {
        if (!TryParseRepository(repository, out var owner, out var name))
            throw new GitHubShelfEntryRefusedException($"'{repository.Trim()}' is not a GitHub repository. Use owner/repository or the address of its page on github.com.");
        if (string.IsNullOrWhiteSpace(assetSuffix))
            throw new GitHubShelfEntryRefusedException($"Say which release file to take from {owner}/{name}: the ending of its name, such as .zip or .7z.");

        var existing = ShelfOf(game).FirstOrDefault(entry => string.Equals(entry.Owner, owner, StringComparison.OrdinalIgnoreCase) && string.Equals(entry.Repository, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) throw new GitHubShelfEntryRefusedException($"{existing.Name} is already on this game's shelf.");

        using var tx = connection.BeginTransaction();
        var entryRow = new GitHubShelfEntry.New(tx)
        {
            Organization = owner,
            Repository = name,
            AssetSuffix = assetSuffix.Trim(),
            GameId = game,
            IncludePrereleases = includePrereleases,
        };
        if (!string.IsNullOrWhiteSpace(nameHint)) tx.Add(entryRow.Id, GitHubShelfEntry.NameHint, nameHint.Trim());
        var result = await tx.Commit();
        return ToRepository(result.Remap(entryRow));
    }

    /// <summary>
    /// Records the repository id of an entry: on its row, or a new row when a game declared the entry and nothing has
    /// been stored for it yet.
    /// </summary>
    public async Task Remember(ShelfRepository entry, ulong repositoryId)
    {
        var row = GitHubShelfEntry.All(connection.Db).FirstOrDefault(candidate => candidate.GameId == entry.Game && Same(ToRepository(candidate), entry.Owner, entry.Repository));
        using var tx = connection.BeginTransaction();
        if (row.IsValid())
        {
            if (GitHubShelfEntry.RepositoryId.TryGetValue(row, out _)) return;
            tx.Add(row.Id, GitHubShelfEntry.RepositoryId, repositoryId);
        }
        else
        {
            var created = new GitHubShelfEntry.New(tx)
            {
                Organization = entry.Owner,
                Repository = entry.Repository,
                AssetSuffix = entry.AssetSuffix,
                GameId = entry.Game,
                IncludePrereleases = entry.IncludePrereleases,
            };
            tx.Add(created.Id, GitHubShelfEntry.RepositoryId, repositoryId);
            if (entry.NameHint is { } hint) tx.Add(created.Id, GitHubShelfEntry.NameHint, hint);
        }

        await tx.Commit();
    }

    private IReadOnlyList<ShelfRepository> ShelfOf(GameId game)
        => games.FirstOrDefault(candidate => candidate.GameId == game) is { } known ? For(known) : RowsOf(game);

    private List<ShelfRepository> RowsOf(GameId game)
        => [.. GitHubShelfEntry.All(connection.Db).Where(row => row.GameId == game).Select(ToRepository)];

    private static bool Same(ShelfRepository row, string owner, string repository)
        => string.Equals(row.Owner, owner, StringComparison.OrdinalIgnoreCase) && string.Equals(row.Repository, repository, StringComparison.OrdinalIgnoreCase);

    private static ShelfRepository ToRepository(GitHubShelfEntry.ReadOnly row) => new(
        row.GameId,
        row.Organization,
        row.Repository,
        GitHubShelfEntry.RepositoryId.TryGetValue(row, out var id) ? id : null,
        row.AssetSuffix,
        GitHubShelfEntry.NameHint.TryGetValue(row, out var hint) ? hint : null,
        "Added by you",
        row.IncludePrereleases,
        Declared: false);
}

/// <summary>
/// Text that does not name a GitHub repository, or a repository that is already listed.
/// </summary>
internal sealed class GitHubShelfEntryRefusedException(string message) : UserFacingException(message)
{
    public override string Title => "The repository was not added";
}
