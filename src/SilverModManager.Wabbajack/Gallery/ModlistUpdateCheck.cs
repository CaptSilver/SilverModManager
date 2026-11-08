using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack.Gallery;

/// <summary>
/// An installed modlist whose gallery entry names a different release than the file it was installed from.
/// <see cref="ArchivesToDownload"/> and <see cref="DownloadSize"/> are the entry's totals for the whole list: the new
/// <c>.wabbajack</c> is not in the library yet, so which archives are already held is not known until it is.
/// </summary>
/// <param name="IsReupload">The author replaced the release in place, under the date the user already has.</param>
/// <param name="IsStale">The listing is an offline copy older than the cache lifetime.</param>
public sealed record ModlistUpdate(
    WabbajackModlistLoadoutGroup.ReadOnly Group,
    WabbajackModlistLibraryFile.ReadOnly Installed,
    GalleryEntry Newer,
    bool IsReupload,
    int ArchivesToDownload,
    Size DownloadSize,
    bool IsStale,
    DateTimeOffset ListingFetchedAt);

/// <summary>Finds the installed modlists of a loadout that the gallery has a newer release of.</summary>
public sealed class ModlistUpdateCheck(IWabbajackGalleryClient client, IConnection connection, ILogger<ModlistUpdateCheck> logger, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private int _unavailableLogged;

    /// <summary>
    /// Reads the gallery as the cache allows. A gallery that cannot be read at all means no offers, not an error: the
    /// reason is logged once until a read succeeds again.
    /// </summary>
    public async Task<IReadOnlyList<ModlistUpdate>> Check(LoadoutId loadout, CancellationToken ct)
    {
        GalleryListing listing;
        try
        {
            listing = await client.Get(refresh: false, ct);
        }
        catch (GalleryUnavailableException e)
        {
            if (Interlocked.Exchange(ref _unavailableLogged, 1) == 0)
                logger.LogInformation(e, "The gallery is unavailable, so installed modlists are not checked for newer versions");
            return [];
        }

        Interlocked.Exchange(ref _unavailableLogged, 0);
        return Offers(connection.Db, loadout, listing);
    }

    /// <summary>
    /// The offers the cached listing gives, without touching the network, read from <paramref name="db"/> so that the
    /// groups resolve at the caller's snapshot. A cached listing older than the cache lifetime is marked stale: the
    /// cache itself never says so.
    /// </summary>
    public IReadOnlyList<ModlistUpdate> FromCache(IDb db, LoadoutId loadout)
    {
        if (client.GetCached() is not { } listing) return [];
        var stale = listing.IsStale || _time.GetUtcNow() - listing.FetchedAt >= WabbajackGalleryClient.CacheLifetime;
        return Offers(db, loadout, listing with { IsStale = stale });
    }

    private static List<ModlistUpdate> Offers(IDb db, LoadoutId loadout, GalleryListing listing)
    {
        var offers = new List<ModlistUpdate>();
        foreach (var group in WabbajackModlistLoadoutGroup.All(db))
        {
            if (!group.IsValid()) continue;
            if (group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().LoadoutId != loadout) continue;

            var installed = group.Modlist;
            if (ModlistIdentity.EntryFor(listing, installed) is not { } entry || !ModlistIdentity.IsNewer(entry, installed)) continue;

            offers.Add(new ModlistUpdate(group, installed, entry, ModlistIdentity.IsReupload(entry, installed),
                entry.ArchiveCount ?? 0, Size.FromLong((long)(entry.ArchivesSize ?? 0)), listing.IsStale, listing.FetchedAt));
        }
        return offers;
    }
}
