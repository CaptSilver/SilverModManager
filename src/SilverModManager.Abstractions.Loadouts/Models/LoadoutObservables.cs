using DynamicData;
using NexusMods.HyperDuck;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts;

public class LoadoutQueries2
{
    /// <summary>
    /// Returns all mutable collection groups in a loadout.
    /// </summary>
    public static Query<(EntityId GroupId, string Name)> MutableCollections(IConnection connection, LoadoutId id) =>
        connection.Query<(EntityId, string)>($"SELECT Id, Name FROM mdb_CollectionGroup(Db=>{connection}) WHERE IsReadOnly = false AND Loadout = {id} ORDER BY Id");

    /// <summary>
    /// Returns an IObservable of a loadout with the given id, refreshing it whenever a child entity is updated.
    /// </summary>
    public static IObservable<Loadout.ReadOnly> RevisionsWithChildUpdates(IConnection connection, LoadoutId id)
    {
        return connection.Query<(EntityId LoadoutId, ulong Max, long Count)>($"""
                SELECT {id}, MAX(d.T), COUNT(d.E) FROM 
                loadouts.TrackedEntitiesForLoadout({connection}, {id}) ents
                LEFT JOIN mdb_Datoms() d ON d.E = ents.Id
                """)
            .Observe(x => x.LoadoutId)
            .QueryWhenChanged(_ => Loadout.Load(connection.Db, id));
    }

    /// <summary>
    /// What the loadout's diagnostics are computed from: the newest transaction that touched one of its inputs and
    /// how many datoms they hold. The inputs are everything an apply reads (<c>loadouts.SyncInputsForLoadout</c>)
    /// plus the game installation entity, which holds the disk scan marker, the last applied loadout and the move
    /// record. The revision queries announce again on every transaction on the connection, including ones that wrote
    /// to other loadouts, so a consumer that should act only when this loadout's inputs changed compares these
    /// stamps instead of the announcements. The installation is joined through its own table so a write to it
    /// re-evaluates the query.
    /// </summary>
    public static IObservable<(ulong LastTx, long Datoms)> RevisionStamps(IConnection connection, LoadoutId id)
    {
        return connection.Query<(EntityId LoadoutId, ulong Max, long Count)>($"""
                SELECT {id}, MAX(d.T), COUNT(d.E) FROM
                (
                    SELECT Id FROM loadouts.SyncInputsForLoadout({connection}, {id})
                    UNION ALL
                    SELECT installation.Id FROM mdb_Loadout(Db=>{connection}) loadout
                        JOIN mdb_GameInstallMetadata(Db=>{connection}) installation ON loadout.Installation = installation.Id
                        WHERE loadout.Id = {id}
                ) ents
                LEFT JOIN mdb_Datoms() d ON d.E = ents.Id
                """)
            .Observe(x => x.LoadoutId)
            .QueryWhenChanged(query => query.Items.Select(row => (row.Max, row.Count)).FirstOrDefault());
    }

    /// <summary>
    /// Like <see cref="RevisionsWithChildUpdates"/>, but over everything an apply reads
    /// (<c>loadouts.SyncInputsForLoadout</c>), so it also fires when a game-installed claim changes.
    /// </summary>
    public static IObservable<Loadout.ReadOnly> SyncInputRevisions(IConnection connection, LoadoutId id)
    {
        return connection.Query<(EntityId LoadoutId, ulong Max, long Count)>($"""
                SELECT {id}, MAX(d.T), COUNT(d.E) FROM 
                loadouts.SyncInputsForLoadout({connection}, {id}) ents
                LEFT JOIN mdb_Datoms() d ON d.E = ents.Id
                """)
            .Observe(x => x.LoadoutId)
            .QueryWhenChanged(_ => Loadout.Load(connection.Db, id));
    }
}
