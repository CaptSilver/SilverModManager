using System.Globalization;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Library.Catalog;
using SilverModManager.Networking.Thunderstore;
using SilverModManager.Networking.Thunderstore.DTOs;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Thunderstore;

namespace SilverModManager.Library;

/// <summary>
/// The Thunderstore index of a game's community as a <see cref="IModCatalog"/>.
/// </summary>
internal sealed class ThunderstoreModCatalog(IThunderstoreCatalogSource catalogs, IThunderstoreInstaller installer, ILogger<ThunderstoreModCatalog> logger) : IModCatalog
{
    internal const int PageSize = 50;

    // Thunderstore lists standalone mod-manager apps as packages in every community. Installing one into a game
    // from inside this app makes no sense, so they never get a card. The catalog's category tags don't mark them
    // reliably (they sit beside real tools like BepInEx packs), so they are matched by full name.
    private static readonly HashSet<string> OtherModManagers = new(StringComparer.OrdinalIgnoreCase)
    {
        "ebkr-r2modman",
        "Kesomannen-GaleModManager",
    };

    /// <inheritdoc />
    public string DisplayName => "Thunderstore";

    /// <inheritdoc />
    public bool Serves(IGameData game) => ThunderstoreGame.For(game) is not null;

    /// <inheritdoc />
    public IReadOnlyList<CatalogSort> Sorts => ThunderstoreSorts.Choices;

    /// <inheritdoc />
    public string InstallLabel(IGameData game) => "Install";

    /// <inheritdoc />
    public async Task<CatalogPage> Search(IGameData game, CatalogQuery query, CancellationToken cancellationToken)
    {
        var community = CommunityOf(game);
        var fromCache = await RefreshIfStale(community, cancellationToken);
        var catalog = await catalogs.Load(community, cancellationToken);

        var listed = ThunderstoreSorts.Order(
            catalog.Search(query.Text).Where(package => !OtherModManagers.Contains(package.FullName) && (query.IncludeAdult || !package.HasNsfwContent)),
            query.Sort.Key);

        var start = int.TryParse(query.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) ? Math.Min(offset, listed.Count) : 0;
        var cards = listed.Skip(start).Take(PageSize).Select(package => ToCard(package, community)).ToArray();
        var next = start + cards.Length;
        return new CatalogPage(cards, next < listed.Count ? next.ToString(CultureInfo.InvariantCulture) : null, listed.Count, fromCache);
    }

    /// <inheritdoc />
    public async Task<CatalogCard?> Find(IGameData game, string id, CancellationToken cancellationToken)
    {
        var community = CommunityOf(game);
        await RefreshIfStale(community, cancellationToken);
        var catalog = await catalogs.Load(community, cancellationToken);

        // The id is Namespace-Name; a team name may contain dashes, so each split is tried against the index.
        var listing = new ThunderstoreDependencyString(id.Trim(), default).Candidates()
            .Select(candidate => catalog.TryGet(candidate, out var found) ? found : null)
            .FirstOrDefault(found => found is not null && !OtherModManagers.Contains(found.FullName));
        return listing is null ? null : ToCard(listing, community);
    }

    private static string CommunityOf(IGameData game)
        => (ThunderstoreGame.For(game) ?? throw new ArgumentException($"{game.DisplayName} has no Thunderstore support.", nameof(game))).ThunderstoreCommunity;

    /// <inheritdoc />
    public async Task<CatalogInstallOutcome> Install(CatalogCard card, LoadoutId loadout, CancellationToken cancellationToken)
    {
        if (card.Handle is not ThunderstoreCardHandle handle) throw new ArgumentException($"'{card.Title}' is not a Thunderstore card.", nameof(card));

        if (handle.Version is not { } version) return new CatalogInstallOutcome.Refused(new CardNotInstallableException(card.Title));

        try
        {
            // The downloads are jobs, so the spine's download indicator shows their progress.
            return new CatalogInstallOutcome.Installed(await installer.InstallAsync(version, loadout, cancellationToken));
        }
        catch (UserFacingException e)
        {
            return new CatalogInstallOutcome.Refused(e);
        }
    }

    // A failed refresh keeps the cached index usable; only a missing one is an error, which Load reports.
    private async Task<bool> RefreshIfStale(string community, CancellationToken cancellationToken)
    {
        try
        {
            if (catalogs.IsStale(community)) await catalogs.Refresh(community, cancellationToken);
            return false;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Could not refresh the Thunderstore index of {Community}", community);
            return true;
        }
    }

    private static CatalogCard ToCard(PackageListing package, string community)
    {
        var latest = package.Versions.FirstOrDefault(v => v.IsActive) ?? package.Versions.FirstOrDefault();
        var reference = LatestRef(package, latest);
        var page = Uri.TryCreate(package.PackageUrl, UriKind.Absolute, out var url) ? url
            : reference is { } r ? ThunderstoreUrls.PackagePage(community, r.Package) : null;
        return new CatalogCard(
            package.FullName,
            ThunderstoreSorts.NameOf(package),
            package.Owner ?? "",
            latest?.Description ?? "",
            Uri.TryCreate(latest?.Icon, UriKind.Absolute, out var icon) ? icon : null,
            page,
            latest?.VersionNumber ?? "",
            ThunderstoreSorts.TotalDownloads(package),
            package.RatingScore,
            package.HasNsfwContent,
            package.DateUpdated,
            new ThunderstoreCardHandle(reference));
    }

    private static ThunderstoreVersionRef? LatestRef(PackageListing package, PackageVersion? latest)
    {
        if (latest is null || package.Owner is null) return null;
        if (!ThunderstorePackageRef.TryCreate(package.Owner, package.Name, out var reference)) return null;
        return ThunderstoreVersionNumber.TryParse(latest.VersionNumber, out var number) ? new ThunderstoreVersionRef(reference, number) : null;
    }
}

/// <summary>
/// The version a Thunderstore card installs; null when the package has no version the app can name.
/// </summary>
internal sealed record ThunderstoreCardHandle(ThunderstoreVersionRef? Version) : ICatalogHandle
{
    public string Catalog => "Thunderstore";
}
