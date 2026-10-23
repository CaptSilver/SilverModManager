using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.Paths;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FileHashes.Emitters;

public class NoWayToSourceFilesOnDisk : ILoadoutDiagnosticEmitter
{
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // TODO: Enable this diagnostic once users have a way to back up the game files from UI
        yield break;
        
        var syncronizer = loadout.InstallationInstance.GetGame().Synchronizer;
        var syncTree = await syncronizer.BuildSyncTree(loadout);
        syncronizer.ProcessSyncTree(syncTree);
        
        var totalSize = Size.Zero;
        var count = 0;
        
        foreach (var (_, node) in syncTree)
        {
            if (node.Loadout.Hash == node.Disk.Hash && node.SourceItemType == LoadoutSourceItemType.Game && !node.Signature.HasFlag(Signature.DiskArchived))
            {
                totalSize += node.Loadout.Size;
                count++;
            }
        }

        if (count > 0)
        {
            yield return Diagnostics.CreateGameFilesDoNotHaveSource(totalSize, count);
        }
    }
}
