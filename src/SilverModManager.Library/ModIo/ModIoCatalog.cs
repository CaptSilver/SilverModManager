using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Library.Catalog;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.ModIo;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Library.ModIo;

/// <summary>
/// A game's mods on mod.io as an <see cref="IModCatalog"/>. Where the game's own mod manager downloads the signed-in
/// account's subscriptions, installing subscribes the account and downloads nothing; for any other game the file is
/// downloaded and installed here.
/// </summary>
/// <remarks>
/// mod.io answers no listing without the user's token, so a search without one throws
/// <see cref="ModIoNotSignedInException"/> naming the Settings row rather than showing an empty page.
/// </remarks>
internal sealed class ModIoCatalog(
    IModIoApi api,
    IConnection connection,
    IEnumerable<IGameData> games,
    IServiceProvider provider,
    Func<HttpClient> client,
    ModIoPickups pickups,
    ILogger<ModIoCatalog> logger) : IModCatalog
{
    internal const int PageSize = 30;

    /// <inheritdoc />
    public string DisplayName => "mod.io";

    /// <inheritdoc />
    public bool Serves(IGameData game) => ModIoGame.For(game) is not null;

    // The keys are mod.io's own sort fields; a leading minus is descending.
    /// <inheritdoc />
    public IReadOnlyList<CatalogSort> Sorts { get; } =
    [
        new("-date_updated", CatalogSortLabels.RecentlyUpdated),
        new("-downloads_total", CatalogSortLabels.MostDownloaded),
        new("-date_live", CatalogSortLabels.Newest),
        new("name", CatalogSortLabels.Name),
    ];

    /// <inheritdoc />
    public string InstallLabel(IGameData game) => ModIoGame.For(game)?.GameSyncsSubscriptions == true ? "Subscribe" : "Install";

    /// <inheritdoc />
    public async Task<CatalogPage> Search(IGameData game, CatalogQuery query, CancellationToken cancellationToken)
    {
        var support = SupportOf(game);
        var offset = int.TryParse(query.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

        var page = await api.Mods(support.ModIoGameId, new ModIoQuery(query.Text.Trim(), query.Sort.Key, offset, PageSize), cancellationToken);

        var cards = page.Items.Where(mod => query.IncludeAdult || !mod.IsAdult).Select(ToCard).ToArray();
        var next = offset + PageSize;
        return new CatalogPage(cards, next < page.Total ? next.ToString(CultureInfo.InvariantCulture) : null, page.Total, false);
    }

    /// <inheritdoc />
    public async Task<CatalogCard?> Find(IGameData game, string id, CancellationToken cancellationToken)
    {
        var support = SupportOf(game);
        if (!uint.TryParse(id.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var modId)) return null;

        return await api.Mod(support.ModIoGameId, modId, cancellationToken) is { } mod ? ToCard(mod) : null;
    }

    /// <inheritdoc />
    public async Task<CatalogInstallOutcome> Install(CatalogCard card, LoadoutId loadout, CancellationToken cancellationToken)
    {
        if (card.Handle is not ModIoHandle handle) throw new ArgumentException($"'{card.Title}' is not a mod.io card.", nameof(card));

        try
        {
            var game = GameOfLoadout(loadout);
            var support = ModIoGame.For(game) ?? throw new ModIoInstallRefusedException($"{MarkdownText.Inline(game.DisplayName)} has no mods on mod.io.", null);

            if (support.GameSyncsSubscriptions)
            {
                await api.Subscribe(support.ModIoGameId, handle.ModId, includeDependencies: true, cancellationToken);
                return new CatalogInstallOutcome.Subscribed(
                    $"Sign in to mod.io in the game's Mod Manager with this account; the mod appears in the {game.DisplayName} mods group after the game syncs.");
            }

            return await Download(game, support, handle.ModId, loadout, cancellationToken);
        }
        catch (UserFacingException e)
        {
            logger.LogInformation("Not installing mod.io mod {ModId}: {Reason}", handle.ModId, e.Message);
            return new CatalogInstallOutcome.Refused(e);
        }
    }

    private async Task<CatalogInstallOutcome> Download(IGameData game, IModIoGame support, uint modId, LoadoutId loadout, CancellationToken cancellationToken)
    {
        // The listing is read now: the address in it expires.
        var mod = await api.Mod(support.ModIoGameId, modId, cancellationToken)
            ?? throw new ModIoInstallRefusedException($"mod.io has no mod {modId} for {MarkdownText.InlineMidLine(game.DisplayName)}.", null);
        var page = PageOf(mod, support);
        var file = mod.Modfile ?? throw new CardNotInstallableException(mod.Name);
        if (file.ScanProblem is { } problem) throw new ModIoInstallRefusedException($"{MarkdownText.Code(file.FileName)} was not installed: mod.io's virus scan {problem}.", page);

        var acquisition = new GameAcquisition(game.GameId);
        Exception? rejected = null;
        var item = new ResolvedInstall($"{mod.Name} ({file.FileName})", Dependency: false, InLibrary: HeldCopy(support.ModIoGameId, file.Id), async token =>
        {
            var task = AddModIoFileJob.Create(provider, client(), support.ModIoGameId, mod.Id, file, page, acquisition, mod.DateUpdated);
            await using var registration = token.Register(() => provider.GetRequiredService<SilverModManager.Sdk.Jobs.IJobMonitor>().Cancel(task));
            try
            {
                return (await task).AsLibraryFile().AsLibraryItem();
            }
            catch (Exception e) when (ModIoFiles.IsRejectedAddress(e))
            {
                rejected = e;
                throw;
            }
        });

        try
        {
            var installed = await provider.GetRequiredService<ILoadoutInstallCore>().Run(loadout, $"modio:{support.ModIoGameId}:{mod.Id}:{file.Id}", [item], cancellationToken);
            return new CatalogInstallOutcome.Installed(installed);
        }
        catch (Exception) when (rejected is not null)
        {
            // mod.io would not hand the file to the app. The user started this, so the page opens and the app waits for
            // the file to turn up in the Downloads folder.
            logger.LogInformation("mod.io refused the download address of mod {ModId}: {Reason}", mod.Id, rejected.Message);
            pickups.Arm(support.ModIoGameId, mod.Id, file, mod.DateUpdated, acquisition);
            return new CatalogInstallOutcome.OpenedOnWebsite(page);
        }
    }

    // A file the library already holds is reused: its bytes were checked when it was added.
    private LibraryItem.ReadOnly? HeldCopy(uint gameId, uint fileId)
    {
        var held = ModIoLibraryFile.All(connection.Db).FirstOrDefault(file => file.GameId == gameId && file.FileId == fileId);
        return held.IsValid() ? held.AsLibraryFile().AsLibraryItem() : null;
    }

    private IGameData GameOfLoadout(LoadoutId loadout)
    {
        var data = Loadout.Load(connection.Db, loadout);
        var game = data.IsValid() && GameInstallMetadata.Game.TryGetValue(data.Installation, out var gameId)
            ? games.FirstOrDefault(candidate => candidate.GameId == gameId)
            : null;
        return game ?? throw new ModIoInstallRefusedException("The loadout's game is not one this app knows.", null);
    }

    private static IModIoGame SupportOf(IGameData game)
        => ModIoGame.For(game) ?? throw new ArgumentException($"{game.DisplayName} has no mods on mod.io.", nameof(game));

    internal static Uri PageOf(ModIoMod mod, IModIoGame support)
        => Uri.TryCreate(mod.ProfileUrl, UriKind.Absolute, out var page) ? page : new Uri($"https://mod.io/g/{support.ModIoNameId}");

    private static CatalogCard ToCard(ModIoMod mod) => new(
        mod.Id.ToString(CultureInfo.InvariantCulture),
        mod.Name,
        mod.SubmittedBy?.Username ?? "",
        mod.Summary ?? "",
        Uri.TryCreate(mod.Logo?.Thumb320x180, UriKind.Absolute, out var icon) ? icon : null,
        Uri.TryCreate(mod.ProfileUrl, UriKind.Absolute, out var page) ? page : null,
        mod.Modfile?.Version ?? "",
        mod.Stats?.DownloadsTotal ?? 0,
        Rating: null,
        mod.IsAdult,
        mod.DateUpdated > 0 ? DateTimeOffset.FromUnixTimeSeconds(mod.DateUpdated) : null,
        new ModIoHandle(mod.Id));
}

/// <summary>
/// The mod a mod.io card installs. The mod is read again when the card is installed, so a card left open for a day
/// installs the file the mod lists then.
/// </summary>
internal sealed record ModIoHandle(uint ModId) : ICatalogHandle
{
    public string Catalog => "mod.io";
}
