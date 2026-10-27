using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;

internal static class DiskStateEntries
{
    /// <summary>
    /// Removes a path's disk state entry.
    /// </summary>
    /// <remarks>
    /// By entity, so what is retracted is what the entry holds when the transaction runs. Values rebuilt from
    /// the sync tree go stale when a rescan lands after the tree was built, and a retraction that names a value
    /// the entry no longer has leaves the entry half there.
    /// </remarks>
    internal static void Retract(ITransaction tx, SyncNode node) => tx.Delete(node.Disk.EntityId, recursive: false);
}
