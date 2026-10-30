using SilverModManager.Abstractions.Loadouts.Ids;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Synchronizer;

/// <summary>What the Apply button's status can say before it builds a sync tree.</summary>
internal enum StatusVerdict
{
    /// <summary>An apply is running.</summary>
    Pending,

    /// <summary>The loadout was deleted or another loadout is the one applied.</summary>
    OtherLoadoutSynced,

    /// <summary>Nothing an apply reads has changed since it ran.</summary>
    Current,

    /// <summary>Something may have changed: the loadout has to be compared with the disk.</summary>
    Compare,
}

internal static class StatusDecision
{
    /// <summary>
    /// Decides without a sync tree. The loadout is current while the newest of its inputs is no later than the apply,
    /// it has as many input datoms as right after the apply, and the disk index has not been rescanned since: the
    /// same inputs and the same disk render the same tree, so the apply would find nothing to do.
    /// The database head is deliberately not consulted: commits to anything else (diagnostics, the library, another
    /// game) move it without changing what an apply writes.
    /// </summary>
    /// <param name="inputsNow">The loadout's inputs now; read only when the answer depends on them.</param>
    /// <param name="inputsAtApply">The loadout's inputs as of the apply; read only when the answer depends on them.</param>
    public static StatusVerdict Decide(
        bool busy,
        bool loadoutExists,
        LoadoutId loadoutId,
        LoadoutWithTxId lastApplied,
        TxId lastScanTx,
        Func<SyncInputs> inputsNow,
        Func<SyncInputs> inputsAtApply)
    {
        if (!loadoutExists) return StatusVerdict.OtherLoadoutSynced;
        if (busy) return StatusVerdict.Pending;
        if (lastApplied.Id != loadoutId && lastApplied != default(LoadoutWithTxId)) return StatusVerdict.OtherLoadoutSynced;
        if (lastApplied.Id != loadoutId) return StatusVerdict.Compare;

        if (lastScanTx > lastApplied.Tx) return StatusVerdict.Compare;

        var now = inputsNow();
        if (now.MaxTx > lastApplied.Tx) return StatusVerdict.Compare;

        return now.Count == inputsAtApply().Count ? StatusVerdict.Current : StatusVerdict.Compare;
    }
}
