using System.Globalization;
using JetBrains.Annotations;
using SilverModManager.Library.Workshop;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Library.Lock;

/// <summary>
/// The Steam Workshop items of a loadout's game, as lock entries.
/// </summary>
/// <remarks>
/// A Workshop item is no library file and no loadout item, so the export of a loadout's groups never reaches it; these
/// are added beside them. Each entry names the item by its published file id and carries no hash, because Steam owns
/// the bytes.
/// </remarks>
[PublicAPI]
public interface IWorkshopLockEntries
{
    /// <summary>One entry per item in the Workshop folder of the loadout's game; none when the game has no Workshop items.</summary>
    ValueTask<IReadOnlyList<LockEntry>> For(Loadout.ReadOnly loadout, CancellationToken cancellationToken);
}

internal sealed class WorkshopLockEntries(IGameRegistry registry, WorkshopItemsView view) : IWorkshopLockEntries
{
    public async ValueTask<IReadOnlyList<LockEntry>> For(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        if (!registry.TryGetGameInstallation(loadout, out var install)) return [];

        var rows = await view.For(install, cancellationToken);
        return [.. rows.Select(ToEntry)];
    }

    internal static LockEntry ToEntry(WorkshopRow row)
        => new($"workshop-{row.Id}", row.Updated?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "", row.Id.ToString(CultureInfo.InvariantCulture), row.Title, "", LockSources.SteamWorkshop);

    /// <summary>What an import tells the user about an entry: where to subscribe.</summary>
    internal static string SubscribeMessage(LockEntry entry)
    {
        if (!ulong.TryParse(entry.FileId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            throw new LockException($"{entry.Id}: the lock names no Steam Workshop item id for {entry.Asset}.");

        return $"{entry.Id}: subscribe to {entry.Asset} in Steam: {SteamWorkshopLinks.Subscribe(id)} (or {SteamWorkshopLinks.WebPage(id)}).";
    }
}
