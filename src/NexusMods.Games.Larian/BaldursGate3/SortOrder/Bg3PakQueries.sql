-- namespace: NexusMods.Games.Larian.BaldursGate3.SortOrder
-- Hash stores and loadout tables are read with the macros of the data model project.

CREATE SCHEMA IF NOT EXISTS bg3;

-- Every pak directly in the Mods folder of a loadout, one row per file name (ignoring case).
-- Two sources feed it: loadout items (mod files), and the paks the game put there itself, which are
-- GameInstalledFile rows. The loadout's copy of a name wins over the game's, an enabled copy over a disabled
-- one, and among equals the newest group. The game's copy ranks last on purpose: it is the one the app does not
-- control, so a pak the user installed under the same name has to be the row the order refers to.
CREATE MACRO bg3.LoadoutPaks(db, loadoutId, modsLocationId) AS TABLE
WITH candidates AS (
    SELECT
        file.TargetPath.Item3 AS Name,
        enabledState.Hash AS Hash,
        enabledState.IsEnabled AS IsEnabled,
        groupItem.Name AS ModName,
        groupItem.Id AS ModGroupId,
        FALSE AS IsGameInstalled
    FROM mdb_LoadoutItemWithTargetPath(Db=>db) AS file
    JOIN synchronizer.LeafLoadoutItems(db) AS enabledState ON file.Id = enabledState.Id
    JOIN mdb_LoadoutItemGroup(Db=>db) AS groupItem ON file.Parent = groupItem.Id
    WHERE file.TargetPath.Item1 = loadoutId
        AND file.TargetPath.Item2 = modsLocationId
        AND enabledState.Hash IS NOT NULL
        AND NOT enabledState.IsDeleted
        AND file.TargetPath.Item3 NOT LIKE '%/%'
        AND lower(file.TargetPath.Item3) LIKE '%.pak'
    UNION ALL
    SELECT
        gameFile.Path.Item3 AS Name,
        gameFile.Hash AS Hash,
        groupItem.Disabled = FALSE AS IsEnabled,
        item.Name AS ModName,
        item.Id AS ModGroupId,
        TRUE AS IsGameInstalled
    FROM mdb_GameInstalledFile(Db=>db) AS gameFile
    JOIN mdb_LoadoutItem(Db=>db) AS item ON gameFile.Parent = item.Id
    JOIN mdb_LoadoutItem(Db=>db) AS groupItem ON item.Parent = groupItem.Id
    WHERE gameFile.Loadout = loadoutId
        AND gameFile.Path.Item2 = modsLocationId
        AND gameFile.Path.Item3 NOT LIKE '%/%'
        AND lower(gameFile.Path.Item3) LIKE '%.pak'
)
SELECT Name, Hash, IsEnabled, ModName, ModGroupId, IsGameInstalled
FROM (
    SELECT
        candidates.*,
        ROW_NUMBER() OVER (
            PARTITION BY lower(candidates.Name)
            ORDER BY candidates.IsGameInstalled ASC, candidates.IsEnabled DESC, candidates.ModGroupId DESC NULLS LAST
        ) AS ranking
    FROM candidates
) ranked
WHERE ranking = 1;


-- Every persisted row of a pak sort order, whether or not the loadout still has the pak.
CREATE MACRO bg3.SortOrderItems(db, sortOrderId) AS TABLE
SELECT s.PakName, s.SortIndex, s.IsEnabled, s.Id
FROM mdb_Bg3PakSortOrderItem(Db=>db) s
WHERE s.ParentSortOrder = sortOrderId
ORDER BY s.SortIndex;


-- The rows of a pak sort order that have loadout data: the pak is still in the loadout or installed by the game.
-- IsEnabled is the sort order's own switch, LoadoutIsEnabled is whether the owning mod (or the game-installed
-- group) is enabled.
CREATE MACRO bg3.LiveSortOrderItems(db, sortOrderId, loadoutId, modsLocationId) AS TABLE
SELECT
    s.PakName,
    s.SortIndex,
    s.IsEnabled,
    s.Id,
    p.Hash,
    p.IsEnabled AS LoadoutIsEnabled,
    p.ModName,
    p.ModGroupId,
    p.IsGameInstalled
FROM mdb_Bg3PakSortOrderItem(Db=>db) s
INNER JOIN bg3.LoadoutPaks(db, loadoutId, modsLocationId) p ON lower(s.PakName) = lower(p.Name)
WHERE s.ParentSortOrder = sortOrderId
ORDER BY s.SortIndex;


-- The complement of LiveSortOrderItems: rows whose pak is no longer in the loadout or installed by the game.
CREATE MACRO bg3.Tombstones(db, sortOrderId, loadoutId, modsLocationId) AS TABLE
SELECT s.PakName, s.SortIndex, s.IsEnabled, s.Id
FROM mdb_Bg3PakSortOrderItem(Db=>db) s
WHERE s.ParentSortOrder = sortOrderId
    AND NOT EXISTS (
        SELECT 1
        FROM bg3.LoadoutPaks(db, loadoutId, modsLocationId) p
        WHERE lower(s.PakName) = lower(p.Name)
    )
ORDER BY s.SortIndex;


-- The notice recording that the app replaced the game's mod list, if the sort order has one. The notice entity
-- also stores the pak lists the app wrote and the reset count, so one with an empty summary is not a notice. While
-- the app holds the list back (the game threw it away twice) the summary belongs to the hold, not to this notice.
CREATE MACRO bg3.Notices(db, sortOrderId) AS TABLE
SELECT n.Id, n.Summary
FROM mdb_Bg3ModListNotice(Db=>db) n
WHERE n.SortOrder = sortOrderId
    AND n.Summary <> ''
    AND COALESCE(n.ResetCount, 0) < 2;

-- Why the app is not writing the game's mod list: the game threw it away twice. Empty when it is writing it.
CREATE MACRO bg3.HeldNotices(db, sortOrderId) AS TABLE
SELECT n.Id, n.Summary
FROM mdb_Bg3ModListNotice(Db=>db) n
WHERE n.SortOrder = sortOrderId
    AND n.Summary <> ''
    AND COALESCE(n.ResetCount, 0) >= 2;
