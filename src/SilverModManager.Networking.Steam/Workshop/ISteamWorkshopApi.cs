using SilverModManager.Networking.Steam.Workshop.DTOs;

namespace SilverModManager.Networking.Steam.Workshop;

/// <summary>
/// The part of Steam's Web API that needs no key: what the Workshop says about an item. Nothing here downloads
/// Workshop content.
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
}
