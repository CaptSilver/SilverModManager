using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Extensions;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.FromSoft.Diagnostics;
using NexusMods.Games.FromSoft.Me3;
using NexusMods.Games.FromSoft.Merge;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FromSoft.Doctor;

/// <summary>
/// Reports mods that provide the same file and the app will not merge. Which paths those are is the merge index's
/// call, so this agrees with the apply: a path the engine merges is not a collision, and one it can't merge says why.
/// Two files for one place in the overlay's config layer are reported separately: the later one would silently
/// replace the other there. Neither blocks the apply.
/// </summary>
public class PackageCollisionEmitter(LoadoutMergeSources sources) : IFromSoftDiagnosticEmitter
{
    private const string CaseNote = " The paths differ only in case, and the game's file lookup does not tell them apart.";

    // The check reads the loadout's items, not the sync tree, so it gives the same answer without one.
    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
        => Diagnose(loadout, FrozenDictionary<GamePath, SyncNode>.Empty, cancellationToken);

    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        foreach (var candidate in sources.Index(loadout).Candidates)
        {
            if (candidate.Outcome.Merges) continue;

            var packages = candidate.Contributors.Select(c => c.PackageId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            var caseOnly = candidate.Spellings.Count > 1;
            yield return FromSoftDiagnostics.CreatePackageCollision(
                string.Join(", ", packages),
                string.Join(", ", candidate.Spellings),
                caseOnly ? CaseNote : "",
                candidate.Outcome.Reason ?? throw new InvalidOperationException($"{candidate.Path} is not merged and says no reason"));
        }

        var overlayFiles = new List<(string Mod, string Path)>();
        foreach (var item in LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath())
        {
            var loadoutItem = item.AsLoadoutItem();
            if (loadoutItem.GetThisAndParents().Any(p => p.IsDisabled)) continue;

            var target = (GamePath)item.TargetPath;
            if (target.LocationId != LocationId.Me3Mods) continue;

            var parts = target.Path.Path.Split('/', 2);
            if (parts.Length < 2) continue;

            if (parts[0].Equals(Me3ModsLayout.OverlayFolder, StringComparison.OrdinalIgnoreCase))
                overlayFiles.Add((loadoutItem.Parent.AsLoadoutItem().Name, parts[1]));
        }

        foreach (var group in overlayFiles.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var mods = group.Select(f => f.Mod).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (group.Count() < 2) continue;

            yield return FromSoftDiagnostics.CreateOverlayCollision(string.Join(", ", mods), group.Key);
        }
    }
}
