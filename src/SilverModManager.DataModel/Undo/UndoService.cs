using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts;
using NexusMods.HyperDuck;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.ValueSerializers;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Undo;

/// <summary>
/// Service for undoing loadout changes, getting restore points, etc. 
/// </summary>
public class UndoService
{
    private readonly IConnection _conn;
    private readonly ILogger<UndoService> _logger;

    /// <summary>
    /// DI constructor 
    /// </summary>
    public UndoService(IConnection connection, ILogger<UndoService> logger)
    {
        _conn = connection;
        _logger = logger;
    }

    /// <summary>
    /// Get a query of all the valid restore points (revisions) for the given loadout.
    /// </summary>
    public List<LoadoutRevisionWithStats> RevisionsFor(EntityId loadout)
    {
        var revisions = _conn.Query<(EntityId TxId, DateTimeOffset Timestamp)>($"SELECT Id, Timestamp FROM undo.LoadoutRevisionsWithMetadata({_conn}, {loadout})")
            .Select(row => LoadoutStats(_conn, new LoadoutRevision(loadout, row.TxId, row.Timestamp)))
            .ToList();
        return revisions;
    }
    
    /// <summary>
    /// This will be expanded in the future to diff loadout states, but for now just grab the mod count
    /// </summary>
    private static LoadoutRevisionWithStats LoadoutStats(IConnection conn, LoadoutRevision revision)
    {
        var newDb = conn.AsOf(TxId.From(revision.TxEntity.Value));
        var result = newDb.Connection.Query<long>($"SELECT COUNT(*) FROM mdb_LibraryLinkedLoadoutItem(Db=>{newDb}) WHERE Loadout = {revision.EntityId}").First();
        return new LoadoutRevisionWithStats(revision, (int)result); 
    }
    
    /// <summary>
    /// Reverts the entities the loadout owns to how they were at the given revision. What the loadout owns
    /// is what <c>loadouts.TrackedEntitiesForLoadout</c> returns, taken both now and at the revision, so
    /// items added since are deleted and items removed since come back under their original ids.
    /// Nothing else is written: not the loadout row, and not anything an owned entity merely refers to
    /// (library items, collection metadata, other loadouts, the install's disk state).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The revision has no transaction, the loadout no longer exists, or it is not a regular loadout.
    /// Nothing is written in these cases.
    /// </exception>
    public async Task<RevertResult> RevertTo(LoadoutRevision revision)
    {
        var loadoutId = revision.EntityId;
        if (revision.TxEntity.Value == 0)
            throw new InvalidOperationException("The revision does not point at a transaction");

        var loadout = Loadout.Load(_conn.Db, loadoutId);
        if (!loadout.IsValid())
            throw new InvalidOperationException($"Loadout {loadoutId} no longer exists");
        if (loadout.LoadoutKind != LoadoutKind.Default)
            throw new InvalidOperationException($"Loadout {loadoutId} is a {loadout.LoadoutKind} loadout and can't be reverted");

        var revertDb = _conn.AsOf(TxId.From(revision.TxEntity.Value));

        // Both sets are computed here because a HyperDuck query can't run inside a transaction function.
        // An entity created between this query and the commit is in neither set and survives the revert.
        var owned = OwnedBy(_conn.Db, loadoutId);
        owned.UnionWith(OwnedBy(revertDb, loadoutId));

        using var tx = _conn.BeginTransaction();
        tx.Add(new RevertOwnedEntitiesTxFunc(loadoutId, owned.ToArray(), revertDb));

        // Mark the transaction as a snapshot
        tx.Add(EntityId.From(TxId.Tmp.Value), LoadoutSnapshot.Snapshot, loadoutId);

        var commitResult = await tx.Commit();

        var residual = owned
            .SelectMany(entity => OwnedEntityDiff.Mismatches(commitResult.Db.Datoms(entity), revertDb.Datoms(entity))
                .Select(attribute => (Entity: entity, Attribute: attribute)))
            .ToList();

        if (residual.Count > 0)
        {
            foreach (var (entity, attribute) in residual)
                _logger.LogError("Entity {Entity} still differs from the revision on attribute {Attribute} after the revert", entity, attribute);
        }

        var dangling = DanglingReferencesAsserted(commitResult, owned);
        foreach (var (entity, attribute, target) in dangling)
            _logger.LogWarning("Reverting {Loadout} restored a reference from {Entity} ({Attribute}) to {Target}, which no longer exists", loadoutId, entity, _conn.AttributeCache.GetSymbol(attribute), target);

        return new RevertResult(dangling, residual);
    }

    /// <summary>
    /// The references the revert transaction asserted on owned entities whose target is neither owned nor
    /// present any more. Reading them off the transaction's own datoms keeps references that were already
    /// dangling before the revert out of the list.
    /// </summary>
    private static List<(EntityId Entity, AttributeId Attribute, EntityId Target)> DanglingReferencesAsserted(ICommitResult commitResult, HashSet<EntityId> owned)
    {
        var db = commitResult.Db;
        var dangling = new List<(EntityId, AttributeId, EntityId)>();
        foreach (var datom in db.Datoms(commitResult.NewTx))
        {
            if (datom.IsRetract || datom.Prefix.ValueTag != ValueTag.Reference || !owned.Contains(datom.E))
                continue;

            var target = EntityIdSerializer.Read(datom.ValueSpan);
            if (owned.Contains(target) || db.Datoms(target).Count > 0)
                continue;

            dangling.Add((datom.E, datom.A, target));
        }
        return dangling;
    }

    private HashSet<EntityId> OwnedBy(IDb db, EntityId loadoutId)
    {
        return _conn
            .Query<EntityId>($"SELECT DISTINCT Id FROM loadouts.TrackedEntitiesForLoadout({db}, {loadoutId}) WHERE Id != {loadoutId}")
            .ToHashSet();
    }
}
