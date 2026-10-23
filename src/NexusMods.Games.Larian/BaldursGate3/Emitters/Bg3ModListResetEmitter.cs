using System.Runtime.CompilerServices;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Games.Larian.BaldursGate3.ModList;
using NexusMods.Games.Larian.BaldursGate3.SortOrder;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.Larian.BaldursGate3.Emitters;

/// <summary>
/// Says that the game threw the mod list away twice and the app has stopped writing it, and names the paks that
/// had just joined the list as the likely cause.
/// </summary>
public sealed class Bg3ModListResetEmitter(IGame game) : ILoadoutDiagnosticEmitter
{
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        var variety = game.SortOrderManager.GetSortOrderVarieties().OfType<IBg3PakSortOrderVariety>().Single();
        if (variety.GetSortOrderIdFor(loadout.LoadoutId, loadout.Db) is not { HasValue: true } found) yield break;

        if (!Bg3ModListNotices.IsHeld(loadout.Db, found.Value)) yield break;
        if (Bg3ModListNotices.Find(loadout.Db, found.Value) is not { } notice) yield break;

        yield return Diagnostics.CreateModListReset(Summary: notice.Summary);
    }
}
