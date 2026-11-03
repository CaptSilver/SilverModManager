using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.BuiltInEntities;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts;

/// <summary>
/// Explicit loadout snapshots. These aren't really snapshots in the traditional sense, instead they are simply
/// markers on a transaction that indicate that we want this transaction to show up in the undo history.
/// </summary>
[Include<Transaction>]
public partial class LoadoutSnapshot : IModelDefinition
{
    private const string Namespace = "SilverModManager.Loadouts.LoadoutSnapshot";
    
    /// <summary>
    /// Inserted on a transaction when a loadout is reverted to some previous state.
    /// </summary>
    public static readonly ReferencesAttribute<Loadout> Snapshot = new(Namespace, nameof(Snapshot));

    /// <summary>
    /// Marks the transaction being built as a restore point for the loadout. Reverting to it restores the state as of
    /// that transaction, so a restore point written before a change is an undo point and one written inside it is not.
    /// </summary>
    public static void Mark(ITransaction tx, LoadoutId loadoutId)
    {
        tx.Add(EntityId.From(TxId.Tmp.Value), Snapshot, loadoutId);
    }

    /// <summary>
    /// Commits a transaction that holds nothing but the marker, and returns it. Call it before the change it should
    /// be able to undo; the change goes in a transaction of its own afterwards.
    /// </summary>
    public static async Task<TxId> WriteRestorePoint(IConnection connection, LoadoutId loadoutId)
    {
        using var tx = connection.BeginTransaction();
        Mark(tx, loadoutId);
        return (await tx.Commit()).NewTx;
    }
}
