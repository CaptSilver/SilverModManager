using Microsoft.Extensions.Logging;
using SilverModManager.Networking.Steam.Workshop;
using SilverModManager.Networking.Steam.Workshop.DTOs;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Library.Workshop;

/// <summary>
/// The Workshop items a game's folder holds, titled and dated from Steam's keyless details call. A view only: the
/// folder is Steam's, outside every location the app manages, so nothing here writes it and no synchronizer sees it.
/// </summary>
/// <remarks>
/// Steam's answers are kept for the refresh age so that entering a page and the update check do not ask twice. When Steam
/// cannot be asked the folder is still listed, each item under its id, and nothing reads as updated.
/// </remarks>
internal sealed class WorkshopItemsView(ISteamWorkshopApi api, TimeProvider time, SteamWorkshopSettings settings, ILogger<WorkshopItemsView> logger)
{
    private readonly Dictionary<ulong, (DateTimeOffset At, PublishedFileDetails Details)> _kept = [];
    private readonly System.Threading.Lock _gate = new();

    /// <summary>
    /// The rows of the installation's Workshop folder, ordered by item id as the folder lists them. Empty for a game that
    /// has no Workshop items or an install outside a Steam library.
    /// </summary>
    public async Task<IReadOnlyList<WorkshopRow>> For(GameInstallation install, CancellationToken cancellationToken, bool force = false)
    {
        if (SteamWorkshopGame.For(install.Game) is not { } support) return [];

        var items = WorkshopFolder.Items(install.Locations[LocationId.Game].Path, support.SteamWorkshopAppId);
        if (items.Count == 0) return [];

        var details = await DetailsOf(items.Select(item => item.PublishedFileId).ToArray(), force, cancellationToken);
        return [.. items.Select(item => Row(item, details.GetValueOrDefault(item.PublishedFileId)))];
    }

    private static WorkshopRow Row(WorkshopItem item, PublishedFileDetails? details)
    {
        var mtime = new DateTimeOffset(Directory.GetLastWriteTimeUtc(item.Folder.GetFullPath()), TimeSpan.Zero).ToUnixTimeSeconds();
        if (details is not { IsAvailable: true }) return new WorkshopRow(item.PublishedFileId, item.PublishedFileId.ToString(System.Globalization.CultureInfo.InvariantCulture), null, mtime, false);

        var updated = details.TimeUpdated;
        var title = string.IsNullOrWhiteSpace(details.Title) ? item.PublishedFileId.ToString(System.Globalization.CultureInfo.InvariantCulture) : details.Title;
        return new WorkshopRow(item.PublishedFileId, title, updated, mtime, updated is { } stamp && stamp.ToUnixTimeSeconds() > mtime);
    }

    private async Task<Dictionary<ulong, PublishedFileDetails>> DetailsOf(ulong[] ids, bool force, CancellationToken cancellationToken)
    {
        var found = new Dictionary<ulong, PublishedFileDetails>();
        var missing = new List<ulong>();
        lock (_gate)
        {
            foreach (var id in ids)
            {
                if (!force && _kept.TryGetValue(id, out var kept) && time.GetUtcNow() - kept.At < settings.RefreshAge) found[id] = kept.Details;
                else missing.Add(id);
            }
        }

        if (missing.Count == 0) return found;

        try
        {
            var answered = await api.Details(missing, cancellationToken);
            lock (_gate)
            {
                foreach (var details in answered)
                {
                    _kept[details.Id] = (time.GetUtcNow(), details);
                    found[details.Id] = details;
                }
            }
        }
        catch (Exception e) when (e is UserFacingException or HttpRequestException)
        {
            logger.LogWarning("Not titling {Count} Steam Workshop items: {Reason}", missing.Count, e.Message);
        }

        return found;
    }
}
