using System.Globalization;
using SilverModManager.Abstractions.Library.Catalog;
using SilverModManager.Networking.Steam.Workshop;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Library.Workshop;

/// <summary>
/// A game's Steam Workshop as an <see cref="IModCatalog"/>: the items the user is subscribed to, and a way to open any
/// item's page so Steam can subscribe to it. Nothing is downloaded here; Steam owns the files.
/// </summary>
/// <remarks>
/// The list is the folder, not the Workshop: browsing the whole Workshop needs the user's own Web API key.
/// </remarks>
internal sealed class SteamWorkshopCatalog(WorkshopItemsView view, ISteamWorkshopApi api, IGameRegistry registry, IOSInterop os) : IModCatalog
{
    internal const string SubscribedKey = "subscribed";

    /// <inheritdoc />
    public string DisplayName => "Steam Workshop";

    /// <inheritdoc />
    public bool Serves(IGameData game) => SteamWorkshopGame.For(game) is not null;

    /// <inheritdoc />
    public IReadOnlyList<CatalogSort> Sorts { get; } = [new(SubscribedKey, "Subscribed")];

    /// <inheritdoc />
    public string InstallLabel(IGameData game) => "Subscribe";

    /// <inheritdoc />
    public async Task<CatalogPage> Search(IGameData game, CatalogQuery query, CancellationToken cancellationToken)
    {
        var text = query.Text.Trim();
        var cards = new List<CatalogCard>();
        foreach (var install in registry.LocateGameInstallations().Where(install => install.Game.GameId == game.GameId))
        {
            foreach (var row in await view.For(install, cancellationToken))
            {
                if (text.Length > 0
                    && !row.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
                    && !row.Id.ToString(CultureInfo.InvariantCulture).Contains(text, StringComparison.Ordinal)) continue;

                cards.Add(new CatalogCard(
                    row.Id.ToString(CultureInfo.InvariantCulture), row.Title, "", row.UpdatedBySteam ? "Updated by Steam" : "",
                    Icon: null, row.WebPage, Version: "", Downloads: 0, Rating: null, IsAdult: false, row.Updated, new WorkshopHandle(row.Id)));
            }
        }

        return new CatalogPage(cards, NextCursor: null, cards.Count, FromCache: false);
    }

    /// <inheritdoc />
    public async Task<CatalogCard?> Find(IGameData game, string id, CancellationToken cancellationToken)
    {
        if (SteamWorkshopGame.For(game) is not { } support
            || !ulong.TryParse(id.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var itemId)) return null;

        var details = (await api.Details([itemId], cancellationToken)).FirstOrDefault(item => item.Id == itemId);
        if (details is not { IsAvailable: true } || details.ConsumerAppId != support.SteamWorkshopAppId) return null;

        return new CatalogCard(
            itemId.ToString(CultureInfo.InvariantCulture), details.Title ?? itemId.ToString(CultureInfo.InvariantCulture), "", "",
            details.PreviewUrl, SteamWorkshopLinks.WebPage(itemId), Version: "", Downloads: 0, Rating: null, IsAdult: false, details.TimeUpdated, new WorkshopHandle(itemId));
    }

    /// <summary>
    /// Opens the item in Steam, where the user subscribes. Steam then downloads it, and the group on the Load Order page
    /// lists it when it lands.
    /// </summary>
    public Task<CatalogInstallOutcome> Install(CatalogCard card, LoadoutId loadout, CancellationToken cancellationToken)
    {
        if (card.Handle is not WorkshopHandle handle) throw new ArgumentException($"'{card.Title}' is not a Steam Workshop card.", nameof(card));

        os.OpenUri(SteamWorkshopLinks.Subscribe(handle.Id));
        return Task.FromResult<CatalogInstallOutcome>(new CatalogInstallOutcome.Subscribed("Steam downloads it; the group updates when it lands"));
    }

    /// <summary>
    /// Opens the item's page on steamcommunity.com in the browser, for a machine where <c>steam://</c> goes nowhere.
    /// </summary>
    public void OpenOnWeb(ulong id) => os.OpenUri(SteamWorkshopLinks.WebPage(id));
}

/// <summary>The item a Workshop card subscribes to.</summary>
internal sealed record WorkshopHandle(ulong Id) : ICatalogHandle
{
    public string Catalog => "Steam Workshop";
}
