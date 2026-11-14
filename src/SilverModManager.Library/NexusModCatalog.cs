using System.Globalization;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Library.Catalog;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Library;

/// <summary>
/// The Nexus Mods catalog of a game as a <see cref="IModCatalog"/>, over the same search client the Nexus page uses.
/// </summary>
internal sealed class NexusModCatalog(
    INexusModsBrowser browser,
    INexusModsInstaller installer,
    ILoadoutInstallCore installCore,
    ILoginManager login,
    IGameDomainToGameIdMappingCache domains) : IModCatalog
{
    /// <summary>The name the left menu and the Source column use; the Nexus page keeps its own view under it.</summary>
    internal const string Name = "Nexus Mods";

    /// <inheritdoc />
    public string DisplayName => Name;

    /// <inheritdoc />
    public IReadOnlyList<CatalogSort> Sorts { get; } =
    [
        new(nameof(NexusModsSort.Endorsements), CatalogSortLabels.MostEndorsed),
        new(nameof(NexusModsSort.Downloads), CatalogSortLabels.MostDownloaded),
        new(nameof(NexusModsSort.Updated), CatalogSortLabels.RecentlyUpdated),
        new(nameof(NexusModsSort.Name), CatalogSortLabels.Name),
    ];

    /// <inheritdoc />
    public bool Serves(IGameData game) => game.NexusModsGameId.HasValue;

    /// <inheritdoc />
    public string InstallLabel(IGameData game) => "Install";

    /// <inheritdoc />
    public async Task<CatalogPage> Search(IGameData game, CatalogQuery query, CancellationToken cancellationToken)
    {
        var id = game.NexusModsGameId.HasValue
            ? game.NexusModsGameId.Value
            : throw new ArgumentException($"{game.DisplayName} is not on Nexus Mods.", nameof(game));
        var offset = int.TryParse(query.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        var sort = Enum.TryParse<NexusModsSort>(query.Sort.Key, out var known) ? known : NexusModsSort.Endorsements;

        var page = await browser.Search(new NexusModsSearchQuery(id, query.Text.Trim(), sort, offset, NexusModsSearchQuery.DefaultPageSize, query.IncludeAdult), cancellationToken);

        var domain = domains.GetDomain(id);
        var cards = page.Hits.Select(hit => new CatalogCard(
            hit.ModId.Value.ToString(CultureInfo.InvariantCulture),
            hit.Name,
            hit.Author,
            hit.Summary,
            hit.ThumbnailUrl,
            NexusModsUrlBuilder.GetModUri(domain, hit.ModId),
            hit.Version,
            hit.Downloads,
            null,
            hit.AdultContent,
            hit.UpdatedAt,
            new NexusCardHandle(id, hit.ModId))).ToArray();

        var next = page.Offset + page.Hits.Count;
        return new CatalogPage(cards, page.HasMore ? next.ToString(CultureInfo.InvariantCulture) : null, page.TotalCount, FromCache: false);
    }

    /// <inheritdoc />
    public Task<CatalogCard?> Find(IGameData game, string id, CancellationToken cancellationToken)
    {
        // The browse API has no lookup by id, and an install needs only the game and the mod, so the card is built
        // from them; the title is a stand-in until the user sees the mod's own page.
        if (!game.NexusModsGameId.HasValue || !uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var mod)) return Task.FromResult<CatalogCard?>(null);

        var nexusId = game.NexusModsGameId.Value;
        var modId = ModId.From(mod);
        return Task.FromResult<CatalogCard?>(new CatalogCard(id, $"Mod {id}", "", "", null, NexusModsUrlBuilder.GetModUri(domains.GetDomain(nexusId), modId),
            "", 0, null, false, null, new NexusCardHandle(nexusId, modId)));
    }

    /// <inheritdoc />
    public async Task<CatalogInstallOutcome> Install(CatalogCard card, LoadoutId loadout, CancellationToken cancellationToken)
    {
        if (card.Handle is not NexusCardHandle handle) throw new ArgumentException($"'{card.Title}' is not a Nexus Mods card.", nameof(card));

        var files = await installer.Files(handle.Game, handle.Mod, cancellationToken);
        if (NexusModsMainFile.Pick(files) is not { } file)
        {
            // Several main files are variants the user has to choose between, so the site is where that happens.
            return new CatalogInstallOutcome.OpenedOnWebsite(new UriBuilder(NexusModsUrlBuilder.GetModUri(domains.GetDomain(handle.Game), handle.Mod)) { Query = "tab=files" }.Uri);
        }

        // The outcome names the file, as nexus install does: a card built by Find has no real title to name it by.
        var name = files.First(row => row.Id == file).Name;

        // A file the library already holds installs for any account: a free member who clicked the site's download
        // button has put it there. Only a missing file needs the premium download API, or the site for anyone else.
        var resolved = installer.Resolve(handle.Game, handle.Mod, file, name, dependency: false, loadout);
        if (resolved.InLibrary is null && await NexusAccount.Current(login, cancellationToken) != NexusAccountKind.Premium)
            return new CatalogInstallOutcome.OpenedOnWebsite(installer.DownloadPage(handle.Game, handle.Mod, file));

        // The core joins a second click on the same mod to the first one's run instead of downloading again.
        return new CatalogInstallOutcome.Installed(await installCore.Run(loadout, $"nexus:{handle.Mod}", [resolved], cancellationToken));
    }
}

/// <summary>
/// The Nexus game and mod a card installs.
/// </summary>
internal sealed record NexusCardHandle(NexusModsGameId Game, ModId Mod) : ICatalogHandle
{
    public string Catalog => NexusModCatalog.Name;
}
