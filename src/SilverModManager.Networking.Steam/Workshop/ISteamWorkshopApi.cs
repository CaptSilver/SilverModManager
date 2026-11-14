using SilverModManager.Networking.Steam.Workshop.DTOs;

namespace SilverModManager.Networking.Steam.Workshop;

/// <summary>
/// The parts of Steam's Web API about the Workshop: what it says about an item, which needs no key, and a search, which
/// needs the user's own Web API key. Nothing here downloads Workshop content.
/// </summary>
public interface ISteamWorkshopApi
{
    /// <summary>
    /// What Steam says about each item, one entry per id Steam answered for, in the order Steam listed them. An item
    /// that does not exist comes back with a result code rather than being left out.
    /// </summary>
    /// <exception cref="SteamWorkshopUnavailableException">Steam did not answer.</exception>
    /// <exception cref="SteamWorkshopReplyUnreadableException">Steam answered with something that is not item details.</exception>
    Task<IReadOnlyList<PublishedFileDetails>> Details(IReadOnlyList<ulong> ids, CancellationToken ct);

    /// <summary>
    /// One page of a game's Workshop, matching the text (everything when it is empty).
    /// </summary>
    /// <param name="cursor">The previous page's <see cref="WorkshopQueryPage.NextCursor"/>, or null for the first page.</param>
    /// <param name="key">The user's Steam Web API key.</param>
    /// <exception cref="SteamWebApiKeyRejectedException">Steam did not accept the key.</exception>
    /// <exception cref="SteamWorkshopUnavailableException">Steam did not answer.</exception>
    /// <exception cref="SteamWorkshopReplyUnreadableException">Steam answered with something that is not a search result.</exception>
    Task<WorkshopQueryPage> Query(uint appId, string text, string? cursor, string key, CancellationToken ct);

    /// <summary>
    /// Whether Steam accepts the key, asked with a search that only counts. False means Steam refused it; a Steam that
    /// cannot answer is an exception, since that says nothing about the key.
    /// </summary>
    /// <exception cref="SteamWorkshopUnavailableException">Steam did not answer.</exception>
    Task<bool> IsKeyAccepted(string key, CancellationToken ct);
}
