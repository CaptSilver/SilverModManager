using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Library.Catalog;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.GameBanana;
using SilverModManager.Networking.GameBanana.DTOs;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Library.GameBanana;

/// <summary>
/// A game's mods on GameBanana as an <see cref="IModCatalog"/>, and the way a mod gets installed from its ids.
/// </summary>
/// <remarks>
/// Listings come from the site's own Subfeed, Index and search. A listing carries no download counts or descriptions,
/// so cards have neither. Only the game's first section is listed by sort or search; the Subfeed lists every
/// section the game declares.
/// </remarks>
internal sealed class GameBananaCatalog(
    IGameBananaApi api,
    IConnection connection,
    IEnumerable<IGameData> games,
    IServiceProvider provider,
    Func<HttpClient> client,
    ILogger<GameBananaCatalog> logger) : IModCatalog, IGameBananaInstaller
{
    internal const int PageSize = 30;

    // The Subfeed is the site's own front page of a game, so it is the default. The other keys are the site's sort names.
    internal const string ActivityKey = "activity";

    /// <inheritdoc />
    public string DisplayName => "GameBanana";

    /// <inheritdoc />
    public bool Serves(IGameData game) => GameBananaGame.For(game) is not null;

    // The site has no sort by name (it answers a sort it does not know with an input error), so there is none here.
    /// <inheritdoc />
    public IReadOnlyList<CatalogSort> Sorts { get; } =
    [
        new(ActivityKey, "Latest activity"),
        new("Generic_MostDownloaded", CatalogSortLabels.MostDownloaded),
        new("Generic_LatestUpdated", CatalogSortLabels.RecentlyUpdated),
        new("Generic_Newest", CatalogSortLabels.Newest),
    ];

    /// <inheritdoc />
    public string InstallLabel(IGameData game) => "Install";

    /// <inheritdoc />
    public async Task<CatalogPage> Search(IGameData game, CatalogQuery query, CancellationToken cancellationToken)
    {
        var support = SupportOf(game);
        var page = int.TryParse(query.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : 1;
        var text = query.Text.Trim();

        var listing = text.Length > 0
            ? await api.Search(support.GameBananaGameId, text, page, cancellationToken)
            : query.Sort.Key == ActivityKey
                ? await api.Subfeed(support.GameBananaGameId, page, PageSize, cancellationToken)
                : await api.Index(support.GameBananaGameId, support.GameBananaSections[0], query.Sort.Key, page, PageSize, cancellationToken);

        var sections = support.GameBananaSections.Select(static section => section.ToString()).ToHashSet(StringComparer.Ordinal);
        var cards = listing.Records
            .Where(record => record.ModelName is null || sections.Contains(record.ModelName))
            .Where(record => query.IncludeAdult || record.HasContentRatings != true)
            .Select(ToCard)
            .ToArray();

        var more = page * PageSize < listing.RecordCount;
        return new CatalogPage(cards, more ? (page + 1).ToString(CultureInfo.InvariantCulture) : null, listing.RecordCount, false);
    }

    /// <inheritdoc />
    public async Task<CatalogCard?> Find(IGameData game, string id, CancellationToken cancellationToken)
    {
        var support = SupportOf(game);
        if (!long.TryParse(id.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var modId)) return null;

        try
        {
            var profile = await api.Profile(modId, cancellationToken);
            return profile.Game is { } listed && listed.Id != support.GameBananaGameId ? null : ToCard(profile);
        }
        catch (GameBananaUnavailableException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public Task<CatalogInstallOutcome> Install(CatalogCard card, LoadoutId loadout, CancellationToken cancellationToken)
        => card.Handle is GameBananaHandle handle
            ? Install(handle, loadout, cancellationToken)
            : throw new ArgumentException($"'{card.Title}' is not a GameBanana card.", nameof(card));

    /// <inheritdoc />
    public Task<CatalogInstallOutcome> Install(long modId, long? fileId, LoadoutId loadout, CancellationToken cancellationToken)
        => Install(new GameBananaHandle(modId, fileId), loadout, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<int?> GameOf(long modId, CancellationToken cancellationToken)
    {
        try
        {
            return (await api.Profile(modId, cancellationToken)).Game is { } game ? checked((int)game.Id) : null;
        }
        catch (GameBananaUnavailableException)
        {
            return null;
        }
    }

    /// <summary>
    /// What the site's analysis flagged in the file an install of <paramref name="handle"/> would take, or in every file
    /// when the mod has several and none is chosen. The page confirms these with the user before it installs.
    /// </summary>
    public async Task<IReadOnlyList<string>> Warnings(GameBananaHandle handle, CancellationToken cancellationToken)
    {
        var profile = await api.Profile(handle.ModId, cancellationToken);
        var files = handle.FileId is { } id ? profile.AllFiles.Where(file => file.Id == id) : profile.AllFiles;
        return [.. files.SelectMany(static file => file.WarningLines).Distinct()];
    }

    /// <summary>
    /// Installs the file into the loadout. A refusal comes back as <see cref="CatalogInstallOutcome.Refused"/>, a used-up
    /// request budget included.
    /// </summary>
    public async Task<CatalogInstallOutcome> Install(GameBananaHandle handle, LoadoutId loadout, CancellationToken cancellationToken)
    {
        try
        {
            var game = GameOfLoadout(loadout);
            var support = GameBananaGame.For(game) ?? throw new GameBananaRefusedException($"{MarkdownText.Inline(game.DisplayName)} has no mods on GameBanana.", null);
            var profile = await api.Profile(handle.ModId, cancellationToken);
            var page = PageOf(profile);

            if (profile.Game is { } listed && listed.Id != support.GameBananaGameId)
                throw new GameBananaRefusedException($"This mod is listed under {MarkdownText.InlineMidLine(listed.Name ?? listed.Id.ToString(CultureInfo.InvariantCulture))}, not {MarkdownText.InlineMidLine(game.DisplayName)}.", page, profile.AlternateFileSources);

            var file = Pick(profile, handle, page);
            if (!file.IsClean)
                throw new GameBananaRefusedException($"GameBanana's virus scan did not clear {MarkdownText.Code(file.FileName)}.", page, profile.AlternateFileSources);
            if (profile.LicenseChecklist?.ForbidsInstalling == true)
                throw new GameBananaRefusedException("The author does not allow installing this mod.", page, profile.AlternateFileSources);

            var item = new ResolvedInstall($"{profile.Name} ({file.FileName})", Dependency: false, InLibrary: HeldCopy(file.Id), async token =>
            {
                var task = AddGameBananaFileJob.Create(provider, client(), profile.Id, file, page, new GameAcquisition(game.GameId), support.GameBananaGameId, profile.UpdateStamp, profile.Version);
                await using var registration = token.Register(() => provider.GetRequiredService<SilverModManager.Sdk.Jobs.IJobMonitor>().Cancel(task));
                return (await task).AsLibraryFile().AsLibraryItem();
            });

            var installed = await provider.GetRequiredService<ILoadoutInstallCore>().Run(loadout, $"gamebanana:{profile.Id}:{file.Id}", [item], cancellationToken);
            return new CatalogInstallOutcome.Installed(installed);
        }
        catch (UserFacingException e)
        {
            logger.LogInformation("Not installing GameBanana mod {ModId}: {Reason}", handle.ModId, e.Message);
            return new CatalogInstallOutcome.Refused(e);
        }
    }

    // A file the library already holds is reused: its bytes were checked when it was added.
    private LibraryItem.ReadOnly? HeldCopy(long fileId)
    {
        var held = GameBananaLibraryFile.All(connection.Db).FirstOrDefault(file => file.FileId == fileId);
        return held.IsValid() ? held.AsLibraryFile().AsLibraryItem() : null;
    }

    private static GameBananaFile Pick(GameBananaProfile profile, GameBananaHandle handle, Uri page)
    {
        var files = profile.AllFiles;
        if (files.Count == 0) throw new CardNotInstallableException(profile.Name);

        if (handle.FileId is { } id)
            return files.FirstOrDefault(file => file.Id == id) ?? throw new GameBananaRefusedException($"The mod has no file {id}.", page, profile.AlternateFileSources);

        return files.Count == 1 ? files[0] : throw new GameBananaChooseFileException(page, files);
    }

    private IGameData GameOfLoadout(LoadoutId loadout)
    {
        var data = Loadout.Load(connection.Db, loadout);
        var game = data.IsValid() && GameInstallMetadata.Game.TryGetValue(data.Installation, out var gameId)
            ? games.FirstOrDefault(candidate => candidate.GameId == gameId)
            : null;
        return game ?? throw new GameBananaRefusedException("The loadout's game is not one this app knows.", null);
    }

    private static IGameBananaGame SupportOf(IGameData game)
        => GameBananaGame.For(game) ?? throw new ArgumentException($"{game.DisplayName} has no mods on GameBanana.", nameof(game));

    private static Uri PageOf(GameBananaProfile profile)
        => Uri.TryCreate(profile.ProfileUrl, UriKind.Absolute, out var page) ? page : new Uri($"https://gamebanana.com/mods/{profile.Id}");

    private static CatalogCard ToCard(GameBananaRecord record) => new(
        record.Id.ToString(CultureInfo.InvariantCulture),
        record.Name,
        record.Submitter?.Name ?? "",
        record.RootCategory?.Name ?? "",
        record.PreviewMedia?.Thumbnail,
        Uri.TryCreate(record.ProfileUrl, UriKind.Absolute, out var page) ? page : new Uri($"https://gamebanana.com/mods/{record.Id}"),
        record.Version ?? "",
        Downloads: 0,
        Rating: null,
        IsAdult: record.HasContentRatings == true,
        Stamp(record.DateUpdated ?? record.DateAdded),
        new GameBananaHandle(record.Id, null));

    private static CatalogCard ToCard(GameBananaProfile profile) => new(
        profile.Id.ToString(CultureInfo.InvariantCulture),
        profile.Name,
        profile.Submitter?.Name ?? "",
        profile.Category?.Name ?? "",
        Icon: null,
        PageOf(profile),
        profile.Version ?? "",
        Downloads: 0,
        Rating: null,
        IsAdult: false,
        Stamp(profile.DateUpdated),
        new GameBananaHandle(profile.Id, null));

    private static DateTimeOffset? Stamp(long? seconds) => seconds is > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : null;
}

/// <summary>
/// The mod a GameBanana card installs, and the file of it when one was chosen. The page is read again when the card is
/// installed, so a card left open for a day installs what the mod lists then.
/// </summary>
internal sealed record GameBananaHandle(long ModId, long? FileId = null) : ICatalogHandle
{
    public string Catalog => "GameBanana";
}
