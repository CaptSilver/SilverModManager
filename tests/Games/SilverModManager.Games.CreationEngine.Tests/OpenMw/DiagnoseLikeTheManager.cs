using System.Collections.Frozen;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.CreationEngine.Tests.OpenMw;

/// <summary>
/// Runs the loadout emitters the way the diagnostic manager does, over the sync tree built from the recorded disk
/// state, so the emitters that read the tree (missing masters) see the plugins.
/// </summary>
internal static class DiagnoseLikeTheManager
{
    public static async Task<List<Diagnostic>> Diagnose(this IGame game, Loadout.ReadOnly loadout)
    {
        var synchronizer = game.Synchronizer;
        var metadata = loadout.Installation;
        var scanned = synchronizer.GetLastScannedDiskState(metadata);
        var previous = GameInstallMetadata.LastSyncedLoadoutTransaction.TryGetValue(metadata, out _)
            ? synchronizer.GetPreviouslyAppliedDiskState(metadata)
            : scanned;
        var tree = synchronizer.BuildSyncTree(scanned, previous, loadout);
        synchronizer.ProcessSyncTree(tree);
        var frozen = tree.ToFrozenDictionary();

        var result = new List<Diagnostic>();
        foreach (var emitter in game.DiagnosticEmitters.OfType<ILoadoutDiagnosticEmitter>())
        {
            await foreach (var diagnostic in emitter.Diagnose(loadout, frozen, CancellationToken.None))
                result.Add(diagnostic);
        }
        return result;
    }
}
