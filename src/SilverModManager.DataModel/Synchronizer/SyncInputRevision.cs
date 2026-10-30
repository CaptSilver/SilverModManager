using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Ids;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using LoadoutSortOrder = SilverModManager.Abstractions.Loadouts.SortOrder;

namespace SilverModManager.DataModel.Synchronizer;

/// <summary>
/// The newest transaction and the number of datoms among the inputs of a loadout's sync. Both are needed: a change
/// asserts a datom and raises <see cref="MaxTx"/>, but removing the only mod that provides some files retracts datoms
/// and asserts none, which only <see cref="Count"/> sees.
/// </summary>
internal readonly record struct SyncInputs(TxId MaxTx, long Count);

internal static class SyncInputRevision
{
    /// <summary>
    /// Reads the inputs of <paramref name="loadoutId"/> as of <paramref name="db"/>: the entities
    /// <c>loadouts.SyncInputsForLoadout</c> names, and whatever else points at one of the loadout's sort orders (a game
    /// hangs its notes about a load order there, and the SQL cannot name a model of a game it does not know).
    /// </summary>
    public static SyncInputs Read(IDb db, LoadoutId loadoutId)
    {
        var entities = new HashSet<EntityId>(db.Connection.Query<EntityId>(
            $"SELECT DISTINCT Id FROM loadouts.SyncInputsForLoadout({db}, {loadoutId})"));

        foreach (var sortOrder in LoadoutSortOrder.FindByLoadout(db, loadoutId))
        {
            foreach (var reference in db.ReferencesTo(sortOrder.Id))
                entities.Add(reference.E);
        }

        var maxTx = default(TxId);
        long count = 0;
        foreach (var entity in entities)
        {
            foreach (var datom in db.Get(entity))
            {
                count++;
                if (datom.T > maxTx) maxTx = datom.T;
            }
        }

        return new SyncInputs(maxTx, count);
    }
}
