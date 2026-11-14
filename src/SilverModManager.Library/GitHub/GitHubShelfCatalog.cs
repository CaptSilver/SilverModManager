using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Library.Catalog;
using SilverModManager.Networking.GitHub;
using SilverModManager.Networking.GitHub.DTOs;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Library.GitHub;

/// <summary>
/// The GitHub shelf of a game as an <see cref="IModCatalog"/>: one card per repository whose release holds a loader or
/// tool the game needs, from the game's own list and the repositories the user added.
/// </summary>
/// <remarks>
/// A release GitHub publishes no digest for (anything uploaded before June 2025) installs under trust on first use,
/// and the row says so. The shelf is not general browsing: it lists what a game declares and what the user typed in.
/// </remarks>
internal sealed class GitHubShelfCatalog(
    IGitHubApi api,
    GitHubReleases releases,
    GitHubShelfEntries entries,
    IServiceProvider provider,
    Func<HttpClient> client,
    ILogger<GitHubShelfCatalog> logger) : IModCatalog
{
    /// <summary>
    /// What the add dialog and the add verb say before a repository goes on a shelf: the app takes whatever it publishes.
    /// </summary>
    public static string AddWarning(ShelfRepository entry) => $"This installs whatever {entry.Name} publishes. Nobody but its owner has checked it.";

    /// <inheritdoc />
    public string DisplayName => "GitHub";

    /// <inheritdoc />
    public bool Serves(IGameData game) => entries.Any(game);

    /// <inheritdoc />
    public IReadOnlyList<CatalogSort> Sorts { get; } = [new("name", CatalogSortLabels.Name)];

    /// <inheritdoc />
    public string InstallLabel(IGameData game) => "Install";

    /// <inheritdoc />
    public async Task<CatalogPage> Search(IGameData game, CatalogQuery query, CancellationToken cancellationToken)
    {
        var text = query.Text.Trim();
        var shelf = entries.For(game)
            .Where(entry => text.Length == 0 || entry.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Repository, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var cards = new List<CatalogCard>(shelf.Length);
        foreach (var entry in shelf) cards.Add(ToCard(entry, await releases.Latest(entry, force: false, cancellationToken)));
        return new CatalogPage(cards, null, cards.Count, false);
    }

    /// <inheritdoc />
    public async Task<CatalogCard?> Find(IGameData game, string id, CancellationToken cancellationToken)
    {
        var entry = entries.For(game).FirstOrDefault(candidate => candidate.Name.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));
        return entry is null ? null : ToCard(entry, await releases.Latest(entry, force: false, cancellationToken));
    }

    /// <inheritdoc />
    public async Task<CatalogInstallOutcome> Install(CatalogCard card, LoadoutId loadout, CancellationToken cancellationToken)
    {
        if (card.Handle is not GitHubShelfHandle handle) throw new ArgumentException($"'{card.Title}' is not a GitHub card.", nameof(card));

        return await Install(handle.Entry, loadout, cancellationToken);
    }

    /// <summary>
    /// Installs the repository's newest release into the loadout. A refusal comes back as
    /// <see cref="CatalogInstallOutcome.Refused"/>, a used-up request budget included.
    /// </summary>
    public async Task<CatalogInstallOutcome> Install(ShelfRepository entry, LoadoutId loadout, CancellationToken cancellationToken)
    {
        try
        {
            var release = await releases.Latest(entry, force: false, cancellationToken);
            if (release is null) return new CatalogInstallOutcome.Refused(new CardNotInstallableException(entry.Name));

            var sidecar = await ReleaseSidecars.Read(client(), release, logger, cancellationToken);
            var job = ReleaseJob(entry, release, sidecar);

            var item = new ResolvedInstall($"{entry.Repository} {release.TagName}", Dependency: false, InLibrary: null, async token =>
            {
                var task = job();
                await using var registration = token.Register(() => provider.GetRequiredService<IJobMonitor>().Cancel(task));
                return (await task).AsLibraryFile().AsLibraryItem();
            });

            var installed = await provider.GetRequiredService<ILoadoutInstallCore>().Run(loadout, $"github:{entry.Key}:{release.TagName}", [item], cancellationToken);
            return new CatalogInstallOutcome.Installed(installed);
        }
        catch (UserFacingException e)
        {
            return new CatalogInstallOutcome.Refused(e);
        }
    }

    // Addressed by name even when the id is known: the job records both, which keeps the library file readable by a
    // human and followable by id.
    private Func<IJobTask<AddGitHubReleaseAssetJob, GitHubReleaseLibraryFile.ReadOnly>> ReleaseJob(
        ShelfRepository entry, Release release, IReadOnlyDictionary<string, Sha256Value>? sidecar)
        => () => AddGitHubReleaseAssetJob.Create(
            provider, api, client(),
            GitHubRepositoryRef.FromName(entry.Owner, entry.Repository), release.TagName, entry.AssetSuffix,
            new GameAcquisition(entry.Game), entry.NameHint, expectedSha256: null, requireAdvertisedDigest: false,
            sidecar, releases.IdOf(entry));

    private static CatalogCard ToCard(ShelfRepository entry, Release? release)
    {
        var picked = release is null ? null : ReleaseAssets.Pick(release, entry.AssetSuffix, entry.NameHint);
        return new CatalogCard(
            entry.Name,
            entry.Repository,
            entry.Owner,
            FirstLine(release?.Body) ?? entry.Role,
            Icon: null,
            Page: release is not null && Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var page) ? page : entry.Page,
            release?.TagName ?? "",
            picked?.DownloadCount ?? 0,
            Rating: null,
            IsAdult: false,
            release?.PublishedAt,
            new GitHubShelfHandle(entry));
    }

    // Release notes open with a heading or a blank line as often as with a sentence.
    private static string? FirstLine(string? body)
    {
        if (body is null) return null;

        foreach (var line in body.Split('\n'))
        {
            var text = line.Trim().TrimStart('#', '*', '-', ' ').Trim();
            if (text.Length > 0) return text;
        }

        return null;
    }
}

/// <summary>
/// The repository a GitHub card installs from. The release is read again when it is installed, from the answer the
/// shelf kept, so a card left open for a day installs what the repository publishes then.
/// </summary>
internal sealed record GitHubShelfHandle(ShelfRepository Entry) : ICatalogHandle
{
    public string Catalog => "GitHub";
}
