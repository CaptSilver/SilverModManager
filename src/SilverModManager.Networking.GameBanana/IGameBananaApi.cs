using SilverModManager.Networking.GameBanana.DTOs;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Networking.GameBanana;

/// <summary>
/// GameBanana's public API (<c>https://gamebanana.com/apiv11</c>). It needs no key or sign-in.
/// </summary>
public interface IGameBananaApi
{
    /// <summary>
    /// The game's newest and most active submissions of every kind, which is the page's default listing.
    /// </summary>
    Task<GameBananaListing> Subfeed(int gameId, int page, int perPage, CancellationToken ct);

    /// <summary>
    /// One section of a game in a sort order. <paramref name="sort"/> is the site's own key, such as
    /// <c>Generic_MostDownloaded</c>.
    /// </summary>
    Task<GameBananaListing> Index(int gameId, GameBananaSection section, string sort, int page, int perPage, CancellationToken ct);

    /// <summary>
    /// The game's mods whose name or text matches <paramref name="text"/>, best match first.
    /// </summary>
    Task<GameBananaListing> Search(int gameId, string text, int page, CancellationToken ct);

    /// <summary>
    /// The mod's page with its files.
    /// </summary>
    /// <exception cref="GameBananaUnavailableException">The mod is private, withheld or does not exist.</exception>
    Task<GameBananaProfile> Profile(long modId, CancellationToken ct);
}

/// <summary>
/// One page of a listing. <paramref name="RecordCount"/> is the whole listing's length, not the page's.
/// </summary>
public sealed record GameBananaListing(IReadOnlyList<GameBananaRecord> Records, int RecordCount, bool IsComplete);
