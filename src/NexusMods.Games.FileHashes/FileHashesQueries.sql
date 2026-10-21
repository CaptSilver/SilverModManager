-- namespace: NexusMods.Games.FileHashes
CREATE SCHEMA IF NOT EXISTS file_hashes;

-- ENUM of all the store names
CREATE TYPE file_hashes.Stores AS ENUM ('Unknown', 'GOG', 'Steam', 'EA Desktop', 'Epic Games Store', 'Origin', 'Xbox Game Pass', 'Manually Added');

-- Find all the gog builds that match the given game's files, and rank them by the number of files that match
CREATE MACRO file_hashes.resolve_gog_build(GameMetadataId, DefaultLanguage := 'en-US') AS TABLE
SELECT build.BuildId, ANY_VALUE(build.ProductId) AS BuildProductId, COUNT(*) matching_files, ANY_VALUE(build."version"), list_distinct(LIST(depot.ProductId)) ProductIds
FROM MDB_DISKSTATEENTRY() entry
         LEFT JOIN MDB_HASHRELATION(DBName=>"hashes") hashrel on entry.Hash = hashRel.xxHash3
         LEFT JOIN MDB_PATHHASHRELATION(DBName=>"hashes") pathrel on pathrel.Path = entry.Path.Item3 AND pathrel.Hash = hashrel.Id
         LEFT JOIN (SELECT Id, unnest(Files) FileId FROM MDB_GOGMANIFEST(DBName=>"hashes")) manifest on pathRel.Id = manifest.FileId
         LEFT JOIN MDB_GOGDEPOT(DBName=>"hashes") depot on depot.Manifest = Manifest.Id
         LEFT JOIN (SELECT Id, unnest(depots) depot, ProductId, buildId, "version" FROM MDB_GOGBUILD(DBName=>"hashes")) build on depot.Id = build.Depot
WHERE entry.Game = GameMetadataId
AND DefaultLanguage in depot.Languages
GROUP BY build.BuildId
ORDER BY COUNT(*) DESC;

-- Find all the steam manifests that match the given game's files, and rank them by the number of files that match
CREATE MACRO file_hashes.resolve_steam_manifests(GameMetadataId) AS TABLE
SELECT ANY_VALUE(steam.depotId) DepotId, COUNT(*) matching_count, ANY_VALUE(steam.AppId) AppId, ANY_VALUE(steam.ManifestId)
FROM MDB_DISKSTATEENTRY() entry
         LEFT JOIN MDB_HASHRELATION(DBName=>"hashes") hashrel on entry.Hash = hashRel.xxHash3
         LEFT JOIN MDB_PATHHASHRELATION(DBName=>"hashes") pathrel on pathrel.Path = entry.Path.Item3 AND pathrel.Hash = hashrel.Id
         LEFT JOIN (SELECT AppId, ManifestId, DepotId, unnest(Files) File FROM MDB_STEAMMANIFEST(DBName=>"hashes")) steam on steam.File = pathrel.Id
WHERE entry.Game = GameMetadataId
GROUP BY steam.ManifestId
ORDER BY COUNT(*) DESC;

-- Find all the depots (LocatorIds) for a given game. This will be the most matching depot for every AppId found in a given game folder
CREATE MACRO file_hashes.resolve_steam_depots(GameMetadataId) AS TABLE 
SELECT arg_max(ManifestId, matching_count) DepotId 
FROM file_hashes.resolve_steam_manifests(GameMetadataId) manifests
GROUP BY manifests.AppId
Having DepotId is not null;

-- gets all the loadouts, locatorids, and stores
CREATE MACRO file_hashes.loadout_locatorids(db) AS TABLE
SELECT install.Store::file_hashes.Stores Store, loadout.id Loadout, unnest(locatorIds) AS LocatorId
FROM MDB_LOADOUT(Db=>db) loadout
         LEFT JOIN MDB_GAMEINSTALLMETADATA(Db=>db) install on loadout.Installation = install.id;  
    
-- gets the ids of the path rows of game files for steam loadouts, as the hash store called `store` holds them
CREATE OR REPLACE MACRO file_hashes.steam_loadout_files(db, store) AS TABLE
SELECT files.Loadout, files.FileId PathId FROM
    (SELECT Loadout, ManifestId, unnest(Files) FileId
     FROM file_hashes.loadout_locatorids(db) locators
              LEFT JOIN MDB_STEAMMANIFEST(DbName=>store) manifest ON manifest.ManifestId = locators.LocatorID::UBIGINT
     WHERE locators.Store = 'Steam') files;


-- gets the ids of the path rows of game files for gog loadouts, as the hash store called `store` holds them
CREATE OR REPLACE MACRO file_hashes.gog_loadout_files(db, store) AS TABLE
WITH 
  -- GOG locatorIds can contain a mix of BuildIds and DLC ProductIds
  locatorIds AS (SELECT Loadout, LocatorId::UBIGINT LocatorId
                 FROM file_hashes.loadout_locatorids(db) locators
                 WHERE locators.Store = 'GOG'), 
  builds AS (SELECT Loadout, BuildId, ProductId BuildProductId, unnest(build.Depots) DepotId
             FROM MDB_GOGBUILD(DBName=>store) build
             INNER JOIN locatorIds ON build.BuildId = locatorIds.LocatorId),
  validDepots AS (SELECT Id, ProductId, Manifest ManifestId 
                  FROM MDB_GOGDEPOT(DBName=>store)
                  WHERE Languages == [] OR 'en-US' in Languages),
  buildDepots AS (SELECT builds.Loadout, builds.DepotId, validDepots.ProductId DepotProductId, builds.BuildProductId, validDepots.ManifestId
                  FROM builds
                  JOIN validDepots on validDepots.Id = builds.DepotId),
  manifests AS (-- Depots for the base game product
                SELECT buildDepots.Loadout, buildDepots.ManifestId 
                FROM buildDepots 
                WHERE buildDepots.DepotProductId = buildDepots.BuildProductId
                UNION
                -- Depots for DLC products
                SELECT buildDepots.Loadout, buildDepots.ManifestId
                FROM buildDepots 
                JOIN locatorIds dlcProducts ON dlcProducts.Loadout = buildDepots.Loadout AND buildDepots.DepotProductId = dlcProducts.LocatorId),
  files AS (SELECT Loadout, unnest(manifest.Files) File
            FROM manifests
            LEFT JOIN MDB_GOGMANIFEST(DBName=>store) manifest ON manifests.ManifestId = manifest.Id)
SELECT files.Loadout, files.File PathId FROM files;

-- every path row of the hash store called `store`, resolved to its path, hash and size
CREATE OR REPLACE MACRO file_hashes.store_path_hashes(store) AS TABLE
SELECT pathRel.Id, pathRel.Path, hashRel.xxHash3 Hash, hashRel.Size
FROM MDB_PathHashRelation(DBName=>store) pathRel
INNER JOIN MDB_hashrelation(DBName=>store) hashRel ON pathRel.Hash = hashRel.Id;

-- gets the paths and hashes of game files in loadouts from the one hash store called `store`. Row ids only mean
-- something inside their own store, so each store resolves its rows to values here and only the values leave.
CREATE OR REPLACE MACRO file_hashes.hash_store_loadout_files(db, store) AS TABLE
WITH 
       relations AS (SELECT Id, Path, Hash, Size FROM file_hashes.store_path_hashes(store)),
       files AS (SELECT Loadout, PathId FROM file_hashes.gog_loadout_files(db, store)
              UNION
              SELECT Loadout, PathId FROM file_hashes.steam_loadout_files(db, store))
SELECT files.Loadout, relations.Path, relations.Hash, relations.Size FROM files
INNER JOIN relations ON files.PathId = relations.Id;

-- The local baseline of each loadout's installation. The local store cannot reference the main store's
-- entities, so the baseline carries the installation as values and is matched on (Store, Path).
CREATE OR REPLACE MACRO file_hashes.loadout_baselines(db) AS TABLE
SELECT loadout.id Loadout, baseline.Id BaselineId
FROM MDB_LOADOUT(Db=>db) loadout
         INNER JOIN MDB_GAMEINSTALLMETADATA(Db=>db) install ON loadout.Installation = install.id
         INNER JOIN MDB_LOCALBASELINE(DbName=>'local_hashes') baseline ON baseline.Store = install.Store AND baseline.GamePath = install.Path;

-- gets the paths and hashes of the files each loadout's local baseline classified as vanilla
CREATE OR REPLACE MACRO file_hashes.local_baseline_files(db) AS TABLE
WITH
       files AS (SELECT Loadout, unnest(baseline.Files) PathId
                 FROM file_hashes.loadout_baselines(db) baselines
                          INNER JOIN MDB_LOCALBASELINE(DbName=>'local_hashes') baseline ON baseline.Id = baselines.BaselineId)
SELECT files.Loadout, relations.Path, relations.Hash, relations.Size FROM files
INNER JOIN file_hashes.store_path_hashes('local_hashes') relations ON files.PathId = relations.Id;

-- the paths a loadout's local baseline has an opinion on: the ones it found vanilla and the ones it found modified
CREATE OR REPLACE MACRO file_hashes.local_baseline_paths(db) AS TABLE
SELECT files.Loadout, relations.Path
FROM (SELECT Loadout, unnest(baseline.Files) PathId
      FROM file_hashes.loadout_baselines(db) baselines
               INNER JOIN MDB_LOCALBASELINE(DbName=>'local_hashes') baseline ON baseline.Id = baselines.BaselineId) files
         INNER JOIN file_hashes.store_path_hashes('local_hashes') relations ON files.PathId = relations.Id
UNION
SELECT baselines.Loadout, nonVanilla.Path
FROM file_hashes.loadout_baselines(db) baselines
         INNER JOIN MDB_NONVANILLAFILE(DbName=>'local_hashes') nonVanilla ON nonVanilla.Baseline = baselines.BaselineId;

-- gets all the paths and hashes for game files in loadouts, from the downloaded hash store, the local one
-- and the loadout's local baseline. Where the baseline classified a path, only its own row survives, so a
-- file it found modified has no row and a hash taken from disk is not contradicted by the downloaded store.
-- Paths compare as strings, case-sensitively.
CREATE MACRO file_hashes.loadout_files(db) AS TABLE
WITH
       stored AS (SELECT Loadout, Path, Hash, Size FROM file_hashes.hash_store_loadout_files(db, 'hashes')
                  UNION
                  SELECT Loadout, Path, Hash, Size FROM file_hashes.hash_store_loadout_files(db, 'local_hashes')),
       classified AS (SELECT Loadout, Path FROM file_hashes.local_baseline_paths(db))
SELECT Loadout, Path, Hash, Size FROM stored
WHERE NOT EXISTS (SELECT 1 FROM classified WHERE classified.Loadout = stored.Loadout AND classified.Path = stored.Path)
UNION
SELECT Loadout, Path, Hash, Size FROM file_hashes.local_baseline_files(db);
