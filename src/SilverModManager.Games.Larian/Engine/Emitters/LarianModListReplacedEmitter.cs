using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.Larian.Engine.ModList;
using SilverModManager.Games.Larian.Engine.SortOrder;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Larian.Engine.Emitters;

/// <summary>
/// Says that the game changed the mod list and the app wrote its own list over the change. While the list is held
/// the notice belongs to <see cref="LarianModListResetEmitter"/>.
/// </summary>
public sealed class LarianModListReplacedEmitter(IGame game) : ILoadoutDiagnosticEmitter
{
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        var variety = game.SortOrderManager.GetSortOrderVarieties().OfType<ILarianPakSortOrderVariety>().Single();
        if (variety.GetSortOrderIdFor(loadout.LoadoutId, loadout.Db) is not { HasValue: true } found) yield break;

        if (LarianModListNotices.IsHeld(loadout.Db, found.Value)) yield break;
        if (LarianModListNotices.Find(loadout.Db, found.Value) is not { } notice || notice.Summary == "") yield break;

        yield return Diagnostics.CreateModListReplaced(
            Summary: notice.Summary,
            ReplacedAt: notice.ReplacedAt.ToLocalTime().ToString("g"));
    }
}
