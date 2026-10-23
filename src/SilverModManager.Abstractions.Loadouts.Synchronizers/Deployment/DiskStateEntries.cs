using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Sdk.Games;

namespace NexusMods.Abstractions.Loadouts.Synchronizers.Deployment;

internal static class DiskStateEntries
{
    /// <summary>
    /// Removes a path's disk state entry, using the values the sync tree last saw for it.
    /// </summary>
    internal static void Retract(ITransaction tx, GameInstallMetadataId gameMetadataId, GamePath path, SyncNode node)
    {
        var id = node.Disk.EntityId;
        tx.Retract(id, DiskStateEntry.Path, ((EntityId)gameMetadataId, path.LocationId, path.Path));
        tx.Retract(id, DiskStateEntry.Hash, node.Disk.Hash);
        tx.Retract(id, DiskStateEntry.Size, node.Disk.Size);
        tx.Retract(id, DiskStateEntry.LastModified, new DateTimeOffset(node.Disk.LastModifiedTicks, TimeSpan.Zero));
        tx.Retract(id, DiskStateEntry.Game, (EntityId)gameMetadataId);
    }
}
