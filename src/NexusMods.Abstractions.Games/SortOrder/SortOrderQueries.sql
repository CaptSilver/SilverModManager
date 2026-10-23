-- namespace: NexusMods.Abstractions.Games

CREATE SCHEMA IF NOT EXISTS sortorder;

-- Returns a table of all LoadoutItemWithTargetPath in for a given GameId
-- TODO: listen to changes to the game's own vanilla files too
CREATE MACRO sortorder.TrackLoadoutItemChanges(db, gameId) AS TABLE
SELECT
    item.Id as ItemId,
    item.Parent as GroupId,
    itemGroup.Parent as CollectionId,
    item.Loadout as LoadoutId
FROM mdb_LoadoutItemWithTargetPath(Db=>db) item
JOIN mdb_LoadoutItemGroup(Db=>db) itemGroup on item.Parent = itemGroup.Id
JOIN mdb_Loadout(Db=>db) loadout on item.Loadout = loadout.Id
JOIN mdb_GameInstallMetadata(Db=>db) as installation on loadout.Installation = installation.Id
WHERE installation.Game = gameId
UNION ALL
-- Files the game installed itself. Their item is the row's group; the GameInstalledGroup above it is not a
-- collection group, so CollectionId is 0 and the manager reconciles the loadout's order, not a collection's.
SELECT
    gameFile.Id as ItemId,
    gameFile.Parent as GroupId,
    CAST(0 AS UBIGINT) as CollectionId,
    gameFile.Loadout as LoadoutId
FROM mdb_GameInstalledFile(Db=>db) gameFile
JOIN mdb_Loadout(Db=>db) loadout on gameFile.Loadout = loadout.Id
JOIN mdb_GameInstallMetadata(Db=>db) as installation on loadout.Installation = installation.Id
WHERE installation.Game = gameId;
