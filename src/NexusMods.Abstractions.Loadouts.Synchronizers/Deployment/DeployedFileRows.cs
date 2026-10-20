using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Sdk.Games;

namespace NexusMods.Abstractions.Loadouts.Synchronizers.Deployment;

internal static class DeployedFileRows
{
    /// <summary>
    /// Records that the loadout's version of the file now sits at <paramref name="path"/>, reusing the
    /// path's disk state entry when it already has one.
    /// </summary>
    /// <param name="writeTime">The deployed path's modification time as the next index will read it.</param>
    internal static void Record(ITransaction tx, DeploymentContext context, GamePath path, SyncNode node, DateTimeOffset writeTime)
    {
        if (node.HaveDisk)
        {
            var id = node.Disk.EntityId;
            tx.Add(id, DiskStateEntry.Hash, node.Loadout.Hash);
            tx.Add(id, DiskStateEntry.Size, node.Loadout.Size);
            tx.Add(id, DiskStateEntry.LastModified, writeTime);
        }
        else
        {
            _ = new DiskStateEntry.New(tx, tx.TempId(DiskStateEntry.EntryPartition))
            {
                Path = path.ToGamePathParentTuple(context.MetadataId),
                Hash = node.Loadout.Hash,
                Size = node.Loadout.Size,
                LastModified = writeTime,
                GameId = context.MetadataId,
            };
        }
    }
}
