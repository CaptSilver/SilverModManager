using System.Runtime.CompilerServices;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Games.Larian.BaldursGate3.ModList;
using NexusMods.Games.Larian.BaldursGate3.SortOrder;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.Larian.BaldursGate3.Emitters;

/// <summary>
/// Says that the game changed the mod list and the app wrote its own list over the change. While the list is held
/// the notice belongs to <see cref="Bg3ModListResetEmitter"/>.
/// </summary>
public sealed class Bg3ModListReplacedEmitter(IGame game) : ILoadoutDiagnosticEmitter
{
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        var variety = game.SortOrderManager.GetSortOrderVarieties().OfType<IBg3PakSortOrderVariety>().Single();
        if (variety.GetSortOrderIdFor(loadout.LoadoutId, loadout.Db) is not { HasValue: true } found) yield break;

        if (Bg3ModListNotices.IsHeld(loadout.Db, found.Value)) yield break;
        if (Bg3ModListNotices.Find(loadout.Db, found.Value) is not { } notice || notice.Summary == "") yield break;

        yield return Diagnostics.CreateModListReplaced(
            Summary: notice.Summary,
            ReplacedAt: notice.ReplacedAt.ToLocalTime().ToString("g"));
    }
}
