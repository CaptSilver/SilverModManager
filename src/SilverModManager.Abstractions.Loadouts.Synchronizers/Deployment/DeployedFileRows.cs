using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;

internal static class DeployedFileRows
{
    /// <summary>
    /// Records that the loadout's version of the file now sits at <paramref name="path"/>, reusing the
    /// path's disk state entry when it already has one.
    /// </summary>
    /// <param name="writeTime">The deployed path's modification time as the next index will read it.</param>
    internal static void Record(ITransaction tx, DeploymentContext context, GamePath path, SyncNode node, DateTimeOffset writeTime)
        => Record(tx, context, path, node, node.Loadout.Hash, node.Loadout.Size, writeTime);

    /// <summary>
    /// Records the bytes just written to <paramref name="path"/>. A generated file has no loadout hash to take
    /// them from, so the caller passes the hash and size of what it rendered.
    /// </summary>
    internal static void Record(ITransaction tx, DeploymentContext context, GamePath path, SyncNode node, Hash hash, Size size, DateTimeOffset writeTime)
    {
        if (node.HaveDisk)
        {
            var id = node.Disk.EntityId;
            tx.Add(id, DiskStateEntry.Hash, hash);
            tx.Add(id, DiskStateEntry.Size, size);
            tx.Add(id, DiskStateEntry.LastModified, writeTime);
        }
        else
        {
            _ = new DiskStateEntry.New(tx, tx.TempId(DiskStateEntry.EntryPartition))
            {
                Path = path.ToGamePathParentTuple(context.MetadataId),
                Hash = hash,
                Size = size,
                LastModified = writeTime,
                GameId = context.MetadataId,
            };
        }
    }
}
