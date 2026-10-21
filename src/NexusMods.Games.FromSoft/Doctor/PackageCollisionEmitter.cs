using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Extensions;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.FromSoft.Diagnostics;
using NexusMods.Games.FromSoft.Me3;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FromSoft.Doctor;

/// <summary>
/// Reports mods that provide the same file. Packages are not merged, so two packages with one path, or with paths
/// that differ only in case, leave the game one file and no say in which. Two files for one place in the overlay's
/// config layer are reported separately: the later one would silently replace the other there.
/// </summary>
public class PackageCollisionEmitter : IFromSoftDiagnosticEmitter
{
    private const string CaseNote = " The paths differ only in case, and the game's file lookup does not tell them apart.";

    // The check reads the loadout's items, not the sync tree, so it gives the same answer without one.
    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
        => Diagnose(loadout, FrozenDictionary<GamePath, SyncNode>.Empty, cancellationToken);

    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var packageFiles = new List<(string Package, string Path)>();
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
            else if (!parts[0].Equals(Me3ModsLayout.NativesFolder, StringComparison.OrdinalIgnoreCase))
                packageFiles.Add((parts[0], parts[1]));
        }

        foreach (var group in packageFiles.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var packages = group.Select(f => f.Package).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var paths = group.Select(f => f.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (packages.Length < 2 && paths.Length < 2) continue;

            yield return FromSoftDiagnostics.CreatePackageCollision(string.Join(", ", packages), string.Join(", ", paths), paths.Length > 1 ? CaseNote : "");
        }

        foreach (var group in overlayFiles.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var mods = group.Select(f => f.Mod).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (group.Count() < 2) continue;

            yield return FromSoftDiagnostics.CreateOverlayCollision(string.Join(", ", mods), group.Key);
        }
    }
}
