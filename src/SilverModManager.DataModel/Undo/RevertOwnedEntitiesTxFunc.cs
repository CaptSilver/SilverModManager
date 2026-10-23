using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;

namespace NexusMods.DataModel.Undo;

/// <summary>
/// Rewrites a set of entities to their state in <paramref name="revertDb"/>. It diffs against the basis the
/// transaction commits on rather than against a snapshot taken earlier, so a write that landed in between is
/// not silently undone or duplicated.
/// </summary>
internal sealed class RevertOwnedEntitiesTxFunc(EntityId loadoutId, EntityId[] owned, IDb revertDb) : ITxFunction
{
    private readonly EntityId _loadoutId = loadoutId;

    public bool Equals(ITxFunction? other) => other is RevertOwnedEntitiesTxFunc func && func._loadoutId.Equals(_loadoutId);

    public override int GetHashCode() => _loadoutId.GetHashCode();

    public void Apply(ITransaction tx, IDb basis)
    {
        foreach (var entity in owned)
        {
            foreach (var change in OwnedEntityDiff.Between(basis.Datoms(entity), revertDb.Datoms(entity)))
                tx.Add(entity, change.A, change.Tag, change.Value, change.IsRetract);
        }
    }
}
