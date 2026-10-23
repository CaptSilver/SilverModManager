using System.Runtime.CompilerServices;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Diagnostics.References;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Sdk.Loadouts;
using NexusMods.Wabbajack.Models;

namespace NexusMods.Wabbajack.Diagnostics;

/// <summary>
/// Warns, once per installed modlist, that some of its directives were not executed. Registered for every game.
/// </summary>
public sealed class WabbajackSkippedFilesEmitter : ILoadoutDiagnosticEmitter
{
    private const int MaxPathsListed = 10;

    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
        => DiagnoseAsync(loadout, cancellationToken);

    private static async IAsyncEnumerable<Diagnostic> DiagnoseAsync(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var db = loadout.Db;
        // Starting from the groups rather than the loadout's items: a modlist loadout holds every installed file as an
        // item, and rows left behind by a group that is gone are never reached.
        foreach (var group in WabbajackModlistLoadoutGroup.All(db))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!group.IsValid()) continue;

            var item = group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem();
            if (item.LoadoutId != loadout.LoadoutId) continue;

            var skipped = WabbajackSkippedFile.FindByGroup(db, group.Id).Select(row => row.To).Order(StringComparer.Ordinal).ToArray();
            if (skipped.Length == 0) continue;

            var paths = string.Join('\n', skipped.Take(MaxPathsListed).Select(path => $"- `{path}`"));
            if (skipped.Length > MaxPathsListed) paths += $"\n- and {skipped.Length - MaxPathsListed} more";

            yield return WabbajackDiagnostics.CreateModlistFilesSkipped(
                new LoadoutItemGroupReference { TxId = db.BasisTxId, DataId = group.Id },
                item.Name,
                skipped.Length,
                paths);
        }
    }
}
