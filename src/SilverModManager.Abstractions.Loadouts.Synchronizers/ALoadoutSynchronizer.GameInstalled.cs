using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts.Synchronizers.Deployment;
using NexusMods.Hashing.xxHash3;
using NexusMods.HyperDuck;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Abstractions.Loadouts.Synchronizers;

public partial class ALoadoutSynchronizer
{
    private const string GameInstalledGroupName = "Installed by the game";

    /// <summary>
    /// Finds the content the game installed into its own folder. Null for games that have none.
    /// </summary>
    protected virtual IGameInstalledContentRecognizer? GameInstalledContent => null;

    /// <summary>
    /// Lets a game store its own data about a recognised item. Called inside the reconcile transaction for
    /// every item of the current parse.
    /// </summary>
    protected virtual void WriteGameInstalledItemExtras(ITransaction tx, EntityId itemId, GameInstalledContent content) { }

    /// <summary>
    /// Whether what a game stored through <see cref="WriteGameInstalledItemExtras"/> for an item differs from
    /// the extra of the current parse. Without it, a change that touches only the extra opens no transaction and
    /// the stored copy goes stale.
    /// </summary>
    protected virtual bool GameInstalledExtrasDiffer(IDb db, EntityId itemId, GameInstalledContent content) => false;

    /// <summary>
    /// Brings the loadout's game-installed group in line with what the game has installed right now. Runs
    /// before the sync tree is built, so the tree sees the group's files as the game's and never ingests,
    /// backs up or deletes them.
    /// </summary>
    /// <remarks>
    /// Returns the loadout untouched, without a transaction, when the game has no recognizer, when its records
    /// cannot be read (a torn file must not make every item look removed) and when nothing differs.
    /// </remarks>
    protected async ValueTask<Loadout.ReadOnly> ReconcileGameInstalledContent(Loadout.ReadOnly loadout, GameInstallMetadata.ReadOnly metadata, CancellationToken token = default)
    {
        var recognizer = GameInstalledContent;
        if (recognizer is null) return loadout;

        var diskState = new Dictionary<GamePath, SyncNodePart>();
        foreach (var pair in DiskStateToPathPartPair(DiskStateEntry.FindByGame(metadata.Db, metadata)))
            diskState[pair.Path] = pair.Part;

        var recognition = await recognizer.Recognize(loadout.InstallationInstance, diskState, token);
        if (!recognition.RecordsReadable)
        {
            Logger.LogWarning("The game's own records of installed content could not be read, keeping the {Group} group as it is", GameInstalledGroupName);
            return loadout;
        }

        var db = loadout.Db;
        var desired = ClaimRecognisedContent(db, loadout, recognition.Items, diskState);
        var current = ReadGameInstalledGroup(db, loadout);
        var recordPaths = recognizer.RecordPaths;

        var recognisedPaths = desired.SelectMany(item => item.Files.Keys).ToHashSet();
        var overridesToDelete = FindOverridesRowsOfTheGame(db, loadout, recognisedPaths, recordPaths);
        var removedFiles = current.Files.Where(file => !recognisedPaths.Contains(file.Path)).ToArray();
        var demoted = removedFiles.Where(file => diskState.ContainsKey(file.Path)).ToArray();

        var depotFiles = desired.Count == 0 ? null : GameFilePathsOfTheDepot(db, loadout);
        var itemsDiffer = GameInstalledItemsDiffer(desired, current, depotFiles);

        if (!itemsDiffer && removedFiles.Length == 0 && overridesToDelete.Count == 0)
            return loadout;

        await BackUpDemotedFiles(loadout, demoted, diskState);

        using var tx = Connection.BeginTransaction();
        var groupId = current.GroupId ?? CreateGameInstalledGroup(tx, loadout);

        foreach (var item in current.Items)
        {
            if (desired.All(d => d.Key != item.Key)) tx.Delete(item.Id, recursive: false);
        }

        foreach (var file in removedFiles) tx.Delete(file.Id, recursive: false);

        foreach (var item in desired)
        {
            var itemId = WriteGameInstalledItem(tx, loadout, groupId, item, current, depotFiles);
            WriteGameInstalledItemExtras(tx, itemId, item.Content);
        }

        if (demoted.Length > 0)
        {
            var overrides = GetOrCreateOverridesGroup(tx, loadout);
            foreach (var file in demoted)
            {
                var disk = diskState[file.Path];
                OverridesGroupItems.UpsertFile(tx, loadout, overrides, file.Path, disk.Hash, disk.Size);
            }
        }

        foreach (var row in overridesToDelete) tx.Delete(row, recursive: false);

        var result = await tx.Commit();
        return loadout.Rebase(result.Db);
    }

    /// <summary>One recognised item with the files that are really the game's.</summary>
    private sealed record DesiredGameInstalledItem(GameInstalledContent Content, Dictionary<GamePath, (Hash Hash, Size Size)> Files)
    {
        public (string Source, string SourceId) Key => (Content.Source, Content.SourceId);
    }

    private sealed record CurrentGameInstalledFile(EntityId Id, EntityId ItemId, GamePath Path, Hash Hash, Size Size);

    private sealed record CurrentGameInstalledItem(EntityId Id, (string Source, string SourceId) Key, GameInstalledItem.ReadOnly Model)
    {
        public Dictionary<GamePath, CurrentGameInstalledFile> Files { get; } = new();
    }

    private sealed record CurrentGameInstalledGroup(LoadoutItemGroupId? GroupId, List<CurrentGameInstalledItem> Items)
    {
        public IEnumerable<CurrentGameInstalledFile> Files => Items.SelectMany(item => item.Files.Values);
    }

    /// <summary>
    /// Drops what the loadout already settled: a path the user claimed as their own, and a path a mod provides
    /// (the mod wins it, a delete inside a mod included). An item left with no file is dropped.
    /// </summary>
    private List<DesiredGameInstalledItem> ClaimRecognisedContent(IDb db, Loadout.ReadOnly loadout, IReadOnlyList<GameInstalledContent> items, Dictionary<GamePath, SyncNodePart> diskState)
    {
        var claimed = GameInstalledClaim.FindByLoadout(db, loadout.Id).Select(claim => (GamePath)claim.Path).ToHashSet();
        var taken = new HashSet<GamePath>();
        var result = new List<DesiredGameInstalledItem>();

        foreach (var content in items)
        {
            var files = new Dictionary<GamePath, (Hash Hash, Size Size)>();
            foreach (var path in content.Files)
            {
                if (!diskState.TryGetValue(path, out var disk)) continue;
                if (claimed.Contains(path) || ProvidedByAMod(db, loadout, path)) continue;
                if (!taken.Add(path))
                {
                    Logger.LogWarning("`{Path}` is named by more than one game-installed item, keeping the first", path);
                    continue;
                }

                files[path] = (disk.Hash, disk.Size);
            }

            if (files.Count == 0) continue;
            result.Add(new DesiredGameInstalledItem(content, files));
        }

        return result;
    }

    private static bool ProvidedByAMod(IDb db, Loadout.ReadOnly loadout, GamePath path)
    {
        foreach (var item in LoadoutItemWithTargetPath.FindByTargetPath(db, path.ToGamePathParentTuple(loadout.Id)))
        {
            var loadoutItem = item.AsLoadoutItem();
            if (!loadoutItem.HasParent()) return true;
            if (!LoadoutOverridesGroup.Load(db, loadoutItem.ParentId).IsValid()) return true;
        }

        return false;
    }

    private static CurrentGameInstalledGroup ReadGameInstalledGroup(IDb db, Loadout.ReadOnly loadout)
    {
        var group = GameInstalledGroup.FindByInstalledFor(db, loadout.Id).FirstOrDefault();
        var result = new CurrentGameInstalledGroup(group.IsValid() ? group.AsLoadoutItemGroup().LoadoutItemGroupId : null, []);
        if (!group.IsValid()) return result;

        foreach (var item in group.AsLoadoutItemGroup().Children.OfTypeLoadoutItemGroup().OfTypeGameInstalledItem())
        {
            var current = new CurrentGameInstalledItem(item.Id, (item.Source, item.SourceId), item);
            foreach (var file in item.AsLoadoutItemGroup().Children.OfTypeGameInstalledFile())
                current.Files[file.Path] = new CurrentGameInstalledFile(file.Id, item.Id, file.Path, file.Hash, file.Size);
            result.Items.Add(current);
        }

        return result;
    }

    /// <summary>
    /// The overrides rows (files and reified deletes) at a path the game owns, or at or under one of its record
    /// paths. They were ingested before the game's content was recognised, and the sync tree would otherwise
    /// delete the game's file from disk to make room for them.
    /// </summary>
    private static List<LoadoutItemWithTargetPath.ReadOnly> FindOverridesRowsOfTheGame(IDb db, Loadout.ReadOnly loadout, HashSet<GamePath> recognised, IReadOnlyList<GamePath> recordPaths)
    {
        var rows = new List<LoadoutItemWithTargetPath.ReadOnly>();
        foreach (var group in LoadoutOverridesGroup.FindByOverridesFor(db, loadout.Id))
        {
            foreach (var row in group.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath())
            {
                GamePath path = row.TargetPath;
                if (recognised.Contains(path) || IsAtOrUnder(path, recordPaths)) rows.Add(row);
            }
        }

        return rows;
    }

    private static bool IsAtOrUnder(GamePath path, IReadOnlyList<GamePath> folders)
    {
        foreach (var folder in folders)
        {
            if (path == folder || path.InFolder(folder)) return true;
        }

        return false;
    }

    /// <summary>
    /// The paths of the game's depot manifest for this loadout. Compared by path, since the game rewrites
    /// depot-listed Creations and their hashes drift.
    /// </summary>
    private static HashSet<string> GameFilePathsOfTheDepot(IDb db, Loadout.ReadOnly loadout)
    {
        // TODO: https://github.com/Nexus-Mods/NexusMods.MnemonicDB/issues/183
        var loadoutId = loadout.LoadoutId;
        return db.Connection
            .Query<string>($"SELECT Path FROM file_hashes.loadout_files({db}) WHERE Loadout = {loadoutId}")
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool IsDepotShipped(DesiredGameInstalledItem item, HashSet<string>? depotFiles)
    {
        if (depotFiles is null) return false;
        return item.Files.Keys.All(path => path.LocationId == LocationId.Game && depotFiles.Contains(path.Path.ToString()));
    }

    private static string ConfidenceOf(DesiredGameInstalledItem item, CurrentGameInstalledItem? current)
    {
        // A record that named the item once keeps vouching for it while the file is there, even when a later
        // pass only matches the file name.
        if (current is { Model.Confidence: nameof(GameInstalledConfidence.GameRecord) }) return nameof(GameInstalledConfidence.GameRecord);
        return item.Content.Confidence.ToString();
    }

    private bool GameInstalledItemsDiffer(List<DesiredGameInstalledItem> desired, CurrentGameInstalledGroup current, HashSet<string>? depotFiles)
    {
        if (desired.Count > 0 && current.GroupId is null) return true;
        if (current.Items.Any(item => desired.All(d => d.Key != item.Key))) return true;

        foreach (var item in desired)
        {
            var existing = current.Items.FirstOrDefault(c => c.Key == item.Key);
            if (existing is null) return true;

            var model = existing.Model;
            var version = GameInstalledItem.Version.TryGetValue(model, out var storedVersion) ? storedVersion : null;
            if (model.AsLoadoutItemGroup().AsLoadoutItem().Name != item.Content.Title
                || version != item.Content.Version
                || model.Confidence != ConfidenceOf(item, existing)
                || model.CanBeClaimed != item.Content.CanBeClaimed
                || model.DepotShipped != IsDepotShipped(item, depotFiles))
                return true;

            if (GameInstalledExtrasDiffer(model.Db, existing.Id, item.Content)) return true;

            if (existing.Files.Count != item.Files.Count) return true;
            foreach (var (path, (hash, size)) in item.Files)
            {
                if (!existing.Files.TryGetValue(path, out var file) || file.Hash != hash || file.Size != size)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Makes the reset leave what the game installed itself, and the game's record files, where they are. A
    /// depot copy of a recognised file would otherwise win (the game updated it) and a record file has no
    /// depot row at all, so both would be rewritten or deleted.
    /// </summary>
    private async Task KeepGameInstalledContentThroughReset(GameInstallation installation, GameInstallMetadata.ReadOnly metadata, List<PathPartPair> diskState, Dictionary<GamePath, SyncNode> desiredState)
    {
        var recognizer = GameInstalledContent;
        if (recognizer is null) return;

        var diskParts = new Dictionary<GamePath, SyncNodePart>();
        foreach (var pair in diskState) diskParts[pair.Path] = pair.Part;

        var recognition = await recognizer.Recognize(installation, diskParts, CancellationToken.None);

        IEnumerable<GamePath> recognised;
        if (recognition.RecordsReadable)
        {
            recognised = recognition.Items.SelectMany(item => item.Files);
        }
        else
        {
            // A torn record would make the reset treat every Creation as foreign, so use what the last sync
            // already recorded.
            Logger.LogWarning("The game's own records of installed content could not be read, keeping the content the loadout recorded");
            recognised = GameInstallMetadata.LastSyncedLoadout.TryGetValue(metadata, out var lastSynced)
                ? GameInstalledPaths.For(metadata.Db, LoadoutId.From(lastSynced)).Keys
                : [];
        }

        foreach (var path in recognised)
        {
            // Overwrite rather than add: a depot-listed file the game updated would otherwise keep the depot copy.
            if (path.LocationId == LocationId.Game && diskParts.TryGetValue(path, out var part))
                desiredState[path] = new SyncNode { Loadout = part, SourceItemType = LoadoutSourceItemType.Game };
        }

        // The Game location is skipped by AddDesiredStateOutsideGame and a record file is in no depot.
        foreach (var (path, part) in diskParts)
        {
            if (path.LocationId != LocationId.Game) continue;
            if (!recognizer.RecordPaths.Any(record => path == record || path.InFolder(record))) continue;
            desiredState[path] = new SyncNode { Loadout = part, SourceItemType = LoadoutSourceItemType.Game };
        }
    }

    /// <summary>
    /// Lets each intrinsic file of the last synced loadout say what it should contain once the loadout is gone,
    /// and stores those bytes so the reset can write them.
    /// </summary>
    private async Task RenderIntrinsicFilesForReset(GameInstallMetadata.ReadOnly metadata, Dictionary<GamePath, SyncNode> desiredState)
    {
        if (!GameInstallMetadata.LastSyncedLoadout.TryGetValue(metadata, out var lastSyncedId)) return;

        var lastSynced = Loadout.Load(metadata.Db, lastSyncedId);
        if (!lastSynced.IsValid()) return;

        foreach (var (path, file) in IntrinsicFiles(lastSynced))
        {
            if (file is not IIntrinsicFileWithReset withReset) continue;

            var bytes = await withReset.RenderForReset(lastSynced, desiredState);
            if (bytes is null) continue;

            var hash = bytes.xxHash3();
            var size = Size.FromLong(bytes.Length);
            await _fileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(path.FileName, new MemoryStream(bytes)), hash, size)]);

            desiredState[path] = new SyncNode
            {
                Loadout = new SyncNodePart { Hash = hash, Size = size, LastModifiedTicks = 0 },
                SourceItemType = LoadoutSourceItemType.Game,
            };
        }
    }

    private async Task BackUpDemotedFiles(Loadout.ReadOnly loadout, CurrentGameInstalledFile[] demoted, Dictionary<GamePath, SyncNodePart> diskState)
    {
        var entries = new List<ArchivedFileEntry>();
        foreach (var file in demoted)
        {
            if (IsIgnoredBackupPath(file.Path)) continue;

            var disk = diskState[file.Path];
            if (await _fileStore.HaveFile(disk.Hash)) continue;

            var onDisk = loadout.InstallationInstance.Locations.ToAbsolutePath(file.Path);
            entries.Add(new ArchivedFileEntry(new NativeFileStreamFactory(onDisk), disk.Hash, disk.Size));
        }

        if (entries.Count > 0) await _fileStore.BackupFiles(entries, deduplicate: false);
    }

    /// <summary>
    /// Creates the loadout's empty game-installed group. The caller checks there is none yet.
    /// </summary>
    public static LoadoutItemGroupId CreateGameInstalledGroup(ITransaction tx, Loadout.ReadOnly loadout)
    {
        _ = new GameInstalledGroup.New(tx, out var id)
        {
            InstalledForId = loadout,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id)
                {
                    Name = GameInstalledGroupName,
                    LoadoutId = loadout.Id,
                },
            },
        };

        return LoadoutItemGroupId.From(id);
    }

    /// <summary>
    /// Upserts one item and its files, returning the item's entity id (a temporary one for a new item).
    /// </summary>
    private static EntityId WriteGameInstalledItem(ITransaction tx, Loadout.ReadOnly loadout, LoadoutItemGroupId groupId, DesiredGameInstalledItem item, CurrentGameInstalledGroup current, HashSet<string>? depotFiles)
    {
        var content = item.Content;
        var existing = current.Items.FirstOrDefault(c => c.Key == item.Key);
        var confidence = ConfidenceOf(item, existing);
        var depotShipped = IsDepotShipped(item, depotFiles);

        EntityId itemId;
        if (existing is null)
        {
            var created = new GameInstalledItem.New(tx, out itemId)
            {
                Source = content.Source,
                SourceId = content.SourceId,
                Confidence = confidence,
                CanBeClaimed = content.CanBeClaimed,
                DepotShipped = depotShipped,
                LoadoutItemGroup = new LoadoutItemGroup.New(tx, itemId)
                {
                    IsGroup = true,
                    LoadoutItem = new LoadoutItem.New(tx, itemId)
                    {
                        Name = content.Title,
                        LoadoutId = loadout.Id,
                        ParentId = groupId,
                    },
                },
            };
            if (content.Version is not null) tx.Add(itemId, GameInstalledItem.Version, content.Version);
        }
        else
        {
            itemId = existing.Id;
            tx.Add(itemId, LoadoutItem.Name, content.Title);
            tx.Add(itemId, GameInstalledItem.Confidence, confidence);
            tx.Add(itemId, GameInstalledItem.CanBeClaimed, content.CanBeClaimed);
            tx.Add(itemId, GameInstalledItem.DepotShipped, depotShipped);
            if (content.Version is not null) tx.Add(itemId, GameInstalledItem.Version, content.Version);
            else if (GameInstalledItem.Version.TryGetValue(existing.Model, out var oldVersion)) tx.Retract(itemId, GameInstalledItem.Version, oldVersion);
        }

        foreach (var (path, (hash, size)) in item.Files)
        {
            var oldFile = current.Files.FirstOrDefault(f => f.Path == path);
            if (oldFile is not null && oldFile.ItemId == itemId)
            {
                if (oldFile.Hash != hash) tx.Add(oldFile.Id, GameInstalledFile.Hash, hash);
                if (oldFile.Size != size) tx.Add(oldFile.Id, GameInstalledFile.Size, size);
                continue;
            }

            // The path moved between items, so the old row goes and a new one is added under the right item.
            if (oldFile is not null) tx.Delete(oldFile.Id, recursive: false);
            _ = new GameInstalledFile.New(tx, out var fileId)
            {
                Path = path.ToGamePathParentTuple(loadout.Id),
                Hash = hash,
                Size = size,
                LoadoutItem = new LoadoutItem.New(tx, fileId)
                {
                    Name = path.FileName,
                    LoadoutId = loadout.Id,
                    ParentId = LoadoutItemGroupId.From(itemId),
                },
            };
        }

        return itemId;
    }

    /// <summary>
    /// Retypes the game-installed paths in the tree to game files, so nothing in them is ingested, backed up
    /// or deleted. A loadout file (a mod's, or an overrides row) at the path is left alone.
    /// </summary>
    private void AdoptGameInstalledFiles(Dictionary<GamePath, SyncNode> syncTree, Loadout.ReadOnly loadout)
    {
        var recognizer = GameInstalledContent;
        if (recognizer is null) return;

        // Paths the reconcile recorded: either no loadout side at all, or a game file the game has since changed
        // (a depot-listed Creation it updated).
        foreach (var path in GameInstalledPaths.For(loadout.Db, loadout.Id).Keys)
        {
            ref var node = ref CollectionsMarshal.GetValueRefOrNullRef(syncTree, path);
            if (Unsafe.IsNullRef(ref node) || !node.HaveDisk) continue;
            if (node.HaveLoadout && (node.SourceItemType != LoadoutSourceItemType.Game || node.Loadout.Hash == node.Disk.Hash)) continue;
            AdoptDiskAsGameFile(ref node);
        }

        // The game's record files are filtered from the index, but one that is already indexed keeps being
        // tracked. With its overrides row gone it would be backed up and deleted, so it is retyped too.
        if (recognizer.RecordPaths.Count == 0) return;
        foreach (var path in syncTree.Keys.ToArray())
        {
            if (!IsAtOrUnder(path, recognizer.RecordPaths)) continue;
            ref var node = ref CollectionsMarshal.GetValueRefOrNullRef(syncTree, path);
            if (!node.HaveDisk || node.HaveLoadout) continue;
            AdoptDiskAsGameFile(ref node);
        }
    }

    private static void AdoptDiskAsGameFile(ref SyncNode node)
    {
        node.Loadout = new SyncNodePart
        {
            Hash = node.Disk.Hash,
            Size = node.Disk.Size,
            LastModifiedTicks = 0,
        };
        node.SourceItemType = LoadoutSourceItemType.Game;
    }
}
