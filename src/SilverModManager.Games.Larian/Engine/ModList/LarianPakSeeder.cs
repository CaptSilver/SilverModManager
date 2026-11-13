using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Larian.BaldursGate3.GameInstalled;
using SilverModManager.Games.Larian.BaldursGate3.Models;
using SilverModManager.Games.Larian.BaldursGate3.SortOrder;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.Games.Larian.BaldursGate3.ModList;

/// <summary>
/// Fills a loadout's pak order once, from the mod list the game already has on disk, so the first Apply writes
/// back what the user was playing with instead of a guess.
/// </summary>
public static class Bg3PakSeeder
{
    public static bool IsSeeded(IDb db, SortOrderId sortOrderId) => Bg3PakSortOrder.Load(db, sortOrderId.Value).SeededFromGameFile;

    /// <summary>
    /// Stages the order of the game's entries, with each listed pak switched on, and marks the order as seeded, in
    /// one transaction. A pak the file does not list keeps its row and follows the listed ones: a mod installed in
    /// the app before the first Apply is meant to be loaded. A pak on disk that the loadout does not hold yet
    /// (<paramref name="notYetInLoadout"/>) still gets its row when the file lists it, kept hidden until the pak
    /// arrives, because on a first manage the paks set up by hand are taken into the loadout only by that Apply.
    /// Entries for modules with no pak at all are skipped; with no readable file the order only gets the flag.
    /// </summary>
    public static async Task Seed(
        IBg3PakSortOrderVariety variety,
        IConnection connection,
        SortOrderId sortOrderId,
        IReadOnlyList<Bg3PakCandidate> candidates,
        IReadOnlyList<Bg3PakCandidate> notYetInLoadout,
        ModsettingsDocument? gameFile)
    {
        var byUuid = new Dictionary<string, Bg3PakCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var pak in candidates.Concat(notYetInLoadout))
        {
            if (pak.Entry is not null) byUuid.TryAdd(pak.Entry.Uuid, pak);
        }

        var listed = new List<(string Name, bool? IsEnabled)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in gameFile?.Entries ?? [])
        {
            if (byUuid.TryGetValue(entry.Uuid, out var pak) && seen.Add(pak.Name)) listed.Add((pak.Name, true));
        }

        var rest = candidates.Where(p => !seen.Contains(p.Name)).Select(p => (p.Name, (bool?)null));

        using var tx = connection.BeginTransaction();
        variety.StageOrder(sortOrderId, listed.Concat(rest).ToList(), tx);
        tx.Add(sortOrderId.Value, Bg3PakSortOrder.SeededFromGameFile, true);
        await tx.Commit();
    }
}
