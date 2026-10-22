using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Games.CreationEngine.SortOrder;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.CreationEngine.Emitters;

/// <summary>
/// Says that the plugin list the game had on disk was replaced by the app's, while the notice from the Apply that
/// did it is still there.
/// </summary>
public class PluginListReplacedEmitter(ICreationEngineGame game) : ILoadoutDiagnosticEmitter
{
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        await Task.Yield();

        var variety = game.SortOrderManager.GetSortOrderVarieties().OfType<IPluginSortOrderVariety>().Single();
        if (variety.GetSortOrderIdFor(loadout.LoadoutId, loadout.Db) is not { HasValue: true } sortOrderId) yield break;

        foreach (var notice in PluginListNotices.For(loadout.Db, sortOrderId.Value).Take(1))
            yield return Diagnostics.CreatePluginListReplaced(notice.ReplacedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), notice.Summary);
    }
}
