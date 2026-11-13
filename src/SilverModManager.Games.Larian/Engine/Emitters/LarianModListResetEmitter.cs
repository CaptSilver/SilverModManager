using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.Larian.Engine.ModList;
using SilverModManager.Games.Larian.Engine.SortOrder;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Larian.Engine.Emitters;

/// <summary>
/// Says that the game threw the mod list away twice and the app has stopped writing it, and names the paks that
/// had just joined the list as the likely cause.
/// </summary>
public sealed class LarianModListResetEmitter(IGame game) : ILoadoutDiagnosticEmitter
{
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        var variety = game.SortOrderManager.GetSortOrderVarieties().OfType<ILarianPakSortOrderVariety>().Single();
        if (variety.GetSortOrderIdFor(loadout.LoadoutId, loadout.Db) is not { HasValue: true } found) yield break;

        if (!LarianModListNotices.IsHeld(loadout.Db, found.Value)) yield break;
        if (LarianModListNotices.Find(loadout.Db, found.Value) is not { } notice) yield break;

        yield return Diagnostics.CreateModListReset(Summary: notice.Summary);
    }
}
