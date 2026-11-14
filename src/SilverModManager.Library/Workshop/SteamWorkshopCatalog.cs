using System.Globalization;
using SilverModManager.Abstractions.Library.Catalog;
using SilverModManager.Abstractions.Secrets;
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
/// With the user's own Web API key stored the list is a search of the whole Workshop, paged by Steam's cursor. Without
/// one it is the folder, and the cards say how to get the rest.
/// </remarks>
internal sealed class SteamWorkshopCatalog(WorkshopItemsView view, ISteamWorkshopApi api, IGameRegistry registry, IOSInterop os, ISecretStore secrets) : IModCatalog
{
    internal const string SubscribedKey = "subscribed";

    internal const string KeyHint = "Add your Steam Web API key under Settings → Accounts to browse";

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
        string hint;
        switch (await secrets.GetAsync(WellKnownSecrets.SteamWebApiKey, cancellationToken))
        {
            case SecretResult.Found { Value.Length: > 0 } found when SteamWorkshopGame.For(game) is { } support:
                return await Browse(support.SteamWorkshopAppId, text, query.Cursor, found.Value, cancellationToken);
            case SecretResult.Unavailable unavailable:
                hint = $"The Steam Web API key cannot be read: {unavailable.Reason}";
                break;
            default:
                hint = KeyHint;
                break;
        }

        var cards = new List<CatalogCard>();
        foreach (var install in registry.LocateGameInstallations().Where(install => install.Game.GameId == game.GameId))
        {
            foreach (var row in await view.For(install, cancellationToken))
            {
                if (text.Length > 0
                    && !row.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
                    && !row.Id.ToString(CultureInfo.InvariantCulture).Contains(text, StringComparison.Ordinal)) continue;

                cards.Add(new CatalogCard(
                    row.Id.ToString(CultureInfo.InvariantCulture), row.Title, "", row.UpdatedBySteam ? $"Updated by Steam. {hint}" : hint,
                    Icon: null, row.WebPage, Version: "", Downloads: 0, Rating: null, IsAdult: false, row.Updated, new WorkshopHandle(row.Id)));
            }
        }

        return new CatalogPage(cards, NextCursor: null, cards.Count, FromCache: false);
    }

    private async Task<CatalogPage> Browse(uint appId, string text, string? cursor, string key, CancellationToken cancellationToken)
    {
        var page = await api.Query(appId, text, cursor, key, cancellationToken);
        var cards = page.Items
            .Where(item => item.IsAvailable)
            .Select(item => new CatalogCard(
                item.Id.ToString(CultureInfo.InvariantCulture), item.Title ?? item.Id.ToString(CultureInfo.InvariantCulture), "", item.ShortDescription ?? "",
                item.PreviewUrl, SteamWorkshopLinks.WebPage(item.Id), Version: "", item.Subscriptions ?? 0, Rating: null, IsAdult: false, item.TimeUpdated, new WorkshopHandle(item.Id)))
            .ToList();
        return new CatalogPage(cards, page.NextCursor, page.Total, FromCache: false);
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
