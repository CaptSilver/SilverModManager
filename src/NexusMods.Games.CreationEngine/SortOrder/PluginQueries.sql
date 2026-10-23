-- namespace: NexusMods.Games.CreationEngine.SortOrder
-- Hash stores and loadout tables are read with the macros of the data model project.

CREATE SCHEMA IF NOT EXISTS plugins;

-- Every plugin file directly in the data folder of a loadout, one row per file name (ignoring case).
-- Three sources feed it: loadout items (mod files), the game's own files, which are not loadout items and only
-- exist as rows of file_hashes.loadout_files, and GameInstalledFile rows, the plugins the game put there itself
-- (Creations). IsGameInstalled marks the third source; its IsEnabled is the "Installed by the game" group's switch.
--
-- Precedence for one name: an enabled mod copy first, then a game-installed copy, then the rest by enabled state
-- and newest group. The game-installed copy beats the vanilla row in either group state: a depot-shipped Creation
-- has both, the vanilla row is always enabled, and letting it win while the group is off would show an unlocked,
-- active plugin that the plugin list writer then unstars. A mod copy and a game-installed copy of one name never
-- coexist, because a reconcile leaves a path a mod supplies out of the game-installed content.
-- A game file the loadout deletes is dropped.
CREATE MACRO plugins.LoadoutPlugins(db, loadoutId, gameLocationId, dataFolder) AS TABLE
WITH candidates AS (
    SELECT
        regexp_extract(file.TargetPath.Item3, '[^/]+$') AS Name,
        enabledState.Hash AS Hash,
        enabledState.IsEnabled AS IsEnabled,
        groupItem.Name AS ModName,
        groupItem.Id AS ModGroupId,
        FALSE AS IsGameInstalled
    FROM mdb_LoadoutItemWithTargetPath(Db=>db) AS file
    JOIN synchronizer.LeafLoadoutItems(db) AS enabledState ON file.Id = enabledState.Id
    JOIN mdb_LoadoutItemGroup(Db=>db) AS groupItem ON file.Parent = groupItem.Id
    WHERE file.TargetPath.Item1 = loadoutId
        AND file.TargetPath.Item2 = gameLocationId
        AND enabledState.Hash IS NOT NULL
        AND NOT enabledState.IsDeleted
        AND lower(regexp_replace(file.TargetPath.Item3, '/?[^/]+$', '')) = lower(dataFolder)
        AND lower(regexp_extract(file.TargetPath.Item3, '\.[^./]+$')) IN ('.esm', '.esp', '.esl', '.omwaddon', '.omwgame', '.omwscripts')
    UNION ALL
    SELECT
        regexp_extract(gameFile.Path, '[^/]+$') AS Name,
        gameFile.Hash AS Hash,
        TRUE AS IsEnabled,
        installation.Name AS ModName,
        CAST(NULL AS UBIGINT) AS ModGroupId,
        FALSE AS IsGameInstalled
    FROM file_hashes.loadout_files(db) AS gameFile
    JOIN mdb_Loadout(Db=>db) AS loadout ON gameFile.Loadout = loadout.Id
    JOIN mdb_GameInstallMetadata(Db=>db) AS installation ON loadout.Installation = installation.Id
    WHERE gameFile.Loadout = loadoutId
        AND lower(regexp_replace(gameFile.Path, '/?[^/]+$', '')) = lower(dataFolder)
        AND lower(regexp_extract(gameFile.Path, '\.[^./]+$')) IN ('.esm', '.esp', '.esl', '.omwaddon', '.omwgame', '.omwscripts')
        -- A vanilla plugin the loadout deletes is gone from the game folder, so it is not offered.
        AND NOT EXISTS (
            SELECT 1
            FROM synchronizer.WinningLeafLoadoutItem(db) AS winner
            WHERE winner.Loadout = loadoutId
                AND winner.TargetPath.Item2 = gameLocationId
                AND winner.IsDeleted
                AND lower(winner.TargetPath.Item3) = lower(gameFile.Path)
        )
    UNION ALL
    SELECT
        regexp_extract(installedFile.Path.Item3, '[^/]+$') AS Name,
        installedFile.Hash AS Hash,
        groupItem.Disabled = FALSE AS IsEnabled,
        item.Name AS ModName,
        item.Id AS ModGroupId,
        TRUE AS IsGameInstalled
    FROM mdb_GameInstalledFile(Db=>db) AS installedFile
    JOIN mdb_LoadoutItem(Db=>db) AS item ON installedFile.Parent = item.Id
    JOIN mdb_LoadoutItem(Db=>db) AS groupItem ON item.Parent = groupItem.Id
    -- No deletion check here: a reconcile drops a recognised path a mod's DeletedFile covers and deletes reified
    -- deletions at recognised paths, so a game-installed row never coexists with a deletion.
    WHERE installedFile.Loadout = loadoutId
        AND installedFile.Path.Item2 = gameLocationId
        AND lower(regexp_replace(installedFile.Path.Item3, '/?[^/]+$', '')) = lower(dataFolder)
        AND lower(regexp_extract(installedFile.Path.Item3, '\.[^./]+$')) IN ('.esm', '.esp', '.esl', '.omwaddon', '.omwgame', '.omwscripts')
)
SELECT Name, Hash, IsEnabled, ModName, ModGroupId, IsGameInstalled
FROM (
    SELECT
        candidates.*,
        ROW_NUMBER() OVER (
            PARTITION BY lower(candidates.Name)
            ORDER BY
                CASE
                    WHEN candidates.IsGameInstalled THEN 1
                    WHEN candidates.ModGroupId IS NOT NULL AND candidates.IsEnabled THEN 2
                    ELSE 0
                END DESC,
                candidates.IsEnabled DESC,
                candidates.ModGroupId DESC NULLS LAST
        ) AS ranking
    FROM candidates
) ranked
WHERE ranking = 1;


-- Every persisted row of a plugin sort order, whether or not the loadout still has the plugin.
CREATE MACRO plugins.SortOrderItems(db, sortOrderId) AS TABLE
SELECT s.PluginName, s.SortIndex, s.IsEnabled, s.Id
FROM mdb_PluginSortOrderItem(Db=>db) s
WHERE s.ParentSortOrder = sortOrderId
ORDER BY s.SortIndex;


-- The rows of a plugin sort order that have loadout data: the plugin is still in the loadout or the game folder.
-- IsEnabled is the sort order's own switch, LoadoutIsEnabled is whether the owning mod (or the game-installed
-- group) is enabled.
CREATE MACRO plugins.LiveSortOrderItems(db, sortOrderId, loadoutId, gameLocationId, dataFolder) AS TABLE
SELECT
    s.PluginName,
    s.SortIndex,
    s.IsEnabled,
    s.Id,
    p.Hash,
    p.IsEnabled AS LoadoutIsEnabled,
    p.ModName,
    p.ModGroupId,
    p.IsGameInstalled
FROM mdb_PluginSortOrderItem(Db=>db) s
INNER JOIN plugins.LoadoutPlugins(db, loadoutId, gameLocationId, dataFolder) p ON lower(s.PluginName) = lower(p.Name)
WHERE s.ParentSortOrder = sortOrderId
ORDER BY s.SortIndex;


-- The complement of LiveSortOrderItems: rows whose plugin no longer exists in the loadout or the game folder.
CREATE MACRO plugins.Tombstones(db, sortOrderId, loadoutId, gameLocationId, dataFolder) AS TABLE
SELECT s.PluginName, s.SortIndex, s.IsEnabled, s.Id
FROM mdb_PluginSortOrderItem(Db=>db) s
WHERE s.ParentSortOrder = sortOrderId
    AND NOT EXISTS (
        SELECT 1
        FROM plugins.LoadoutPlugins(db, loadoutId, gameLocationId, dataFolder) p
        WHERE lower(s.PluginName) = lower(p.Name)
    )
ORDER BY s.SortIndex;


-- The notice recording that the app replaced the game's plugin list, if the sort order has one.
CREATE MACRO plugins.Notices(db, sortOrderId) AS TABLE
SELECT n.Id, n.Summary
FROM mdb_PluginListNotice(Db=>db) n
WHERE n.SortOrder = sortOrderId;
