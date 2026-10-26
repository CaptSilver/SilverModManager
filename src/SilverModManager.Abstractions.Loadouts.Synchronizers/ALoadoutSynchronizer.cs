using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CommunityToolkit.HighPerformance.Buffers;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.GC;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Files.Diff;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Abstractions.Loadouts.Sorting;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.IndexSegments;
using SilverModManager.MnemonicDB.Abstractions.Query;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using Reloaded.Memory.Extensions;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

using DiskState = Entities<DiskStateEntry.ReadOnly>;

/// <summary>
/// Base class for loadout synchronizers, provides some common functionality. Does not have to be user,
/// but reduces a lot of boilerplate, and is highly recommended.
/// </summary>
public partial class ALoadoutSynchronizer : ILoadoutSynchronizer
{
    private readonly ScopedAsyncLock _lock = new();
    private readonly IFileStore _fileStore;

    protected readonly ILogger Logger;
    private readonly ISorter _sorter;
    private readonly IGarbageCollectorRunner _garbageCollectorRunner;
    private readonly ISynchronizerService _synchronizerService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoadoutManager _loadoutManager;
    private readonly IGameLocationsService _gameLocationsService;
    private readonly IGameRegistry _gameRegistry;

    private readonly IReadOnlyDictionary<DeploymentBackendId, IDeploymentBackend> _backends;
    private readonly IDeploymentPlanStore _planStore;
    private readonly IDeploymentBackendSelector _backendSelector;
    private readonly ILooseStagingStore _stagingStore;

    private readonly StringPool _fileNamePool = new();

    /// <summary>
    /// Connection.
    /// </summary>
    protected readonly IConnection Connection;

    private readonly IJobMonitor _jobMonitor;
    private readonly IFileHashesService _fileHashService;

    /// <summary>
    /// Loadout synchronizer base constructor.
    /// </summary>
    protected ALoadoutSynchronizer(
        IServiceProvider serviceProvider,
        ILogger logger,
        IFileStore fileStore,
        ISorter sorter,
        IConnection conn,
        IFileHashesService fileHashService,
        IGarbageCollectorRunner garbageCollectorRunner)
    {
        _serviceProvider = serviceProvider;
        _synchronizerService = serviceProvider.GetRequiredService<ISynchronizerService>();
        _jobMonitor = serviceProvider.GetRequiredService<IJobMonitor>();
        _loadoutManager = serviceProvider.GetRequiredService<ILoadoutManager>();
        _gameLocationsService = serviceProvider.GetRequiredService<IGameLocationsService>();
        _gameRegistry = serviceProvider.GetRequiredService<IGameRegistry>();

        // Resolved from DI rather than built here: games create a synchronizer per access.
        _backends = serviceProvider.GetServices<IDeploymentBackend>().ToDictionary(b => b.Id);
        _planStore = serviceProvider.GetRequiredService<IDeploymentPlanStore>();
        _backendSelector = serviceProvider.GetRequiredService<IDeploymentBackendSelector>();
        _stagingStore = serviceProvider.GetRequiredService<ILooseStagingStore>();

        _fileHashService = fileHashService;

        Logger = logger;
        _fileStore = fileStore;
        _sorter = sorter;
        Connection = conn;
        _garbageCollectorRunner = garbageCollectorRunner;
    }

    /// <summary>
    /// Helper constructor that takes only a service provider, and resolves the dependencies from it.
    /// </summary>
    /// <param name="provider"></param>
    protected ALoadoutSynchronizer(IServiceProvider provider) : this(
        provider,
        provider.GetRequiredService<ILogger<ALoadoutSynchronizer>>(),
        provider.GetRequiredService<IFileStore>(),
        provider.GetRequiredService<ISorter>(),
        provider.GetRequiredService<IConnection>(),
        provider.GetRequiredService<IFileHashesService>(),
        provider.GetRequiredService<IGarbageCollectorRunner>()
    ) { }

#region ILoadoutSynchronizer Implementation

    /// <summary>
    /// Gets or creates the override group.
    /// </summary>
    public LoadoutOverridesGroupId GetOrCreateOverridesGroup(ITransaction tx, Loadout.ReadOnly loadout)
    {
        if (LoadoutOverridesGroup.FindByOverridesFor(loadout.Db, loadout.Id).TryGetFirst(out var found))
            return found;

        var newOverrides = new LoadoutOverridesGroup.New(tx, out var id)
        {
            OverridesForId = loadout,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id)
                {
                    Name = "Overrides",
                    LoadoutId = loadout.Id,
                },
            },
        };

        return newOverrides.Id;
    }

    private class LoadoutItemGroupComparer : IEqualityComparer<LoadoutItemGroup.ReadOnly>, IAlternateEqualityComparer<EntityId, LoadoutItemGroup.ReadOnly>
    {
        public static readonly IEqualityComparer<LoadoutItemGroup.ReadOnly> Instance = new LoadoutItemGroupComparer();

        public bool Equals(LoadoutItemGroup.ReadOnly x, LoadoutItemGroup.ReadOnly y) => x.Id.Equals(y.Id);
        public int GetHashCode(LoadoutItemGroup.ReadOnly item) => item.Id.GetHashCode();
        public bool Equals(EntityId alternate, LoadoutItemGroup.ReadOnly other) => other.Id.Equals(alternate);
        public int GetHashCode(EntityId alternate) => alternate.GetHashCode();
        public LoadoutItemGroup.ReadOnly Create(EntityId alternate) => throw new NotSupportedException();
    }

    public Dictionary<GamePath, SyncNode> BuildSyncTree<T>(T latestDiskState, T previousDiskState, Loadout.ReadOnly loadout) where T : IEnumerable<PathPartPair>
    {
        Dictionary<GamePath, SyncNode> syncTree = new();

        foreach (var tuple in WinningFilesQuery(Connection.Db, loadout))
        {
            var itemType = ToItemType(tuple.ItemType);

            // NOTE(erri120): deleted files are not added to the sync tree
            if (itemType == LoadoutSourceItemType.Deleted) continue;

            var gamePath = new GamePath(tuple.Location, tuple.Path);
            if (gamePath == default(GamePath)) throw new Exception($"Item of type `{itemType}` with ID `{tuple.Id}` has no valid game path!");

            // The disk side of the tree never contains filtered paths, so a loadout item on one has to be left
            // out too: otherwise the sync would write the file and could never take it off disk again. A mod
            // flagged to allow protected paths is the exception, its files are tracked on disk once deployed.
            if (GamePathFilter.ShouldFilter(gamePath) && IsSkippedAsProtected(LoadoutItem.Load(Connection.Db, tuple.Id), gamePath))
            {
                LogSkippedFilteredItem(tuple.Id, gamePath);
                continue;
            }

            ref var syncTreeEntry = ref CollectionsMarshal.GetValueRefOrAddDefault(syncTree, gamePath, out var exists);
            Debug.Assert(!exists, "query should not return duplicate items");

            if (exists)
            {
                Logger.LogWarning("Duplicate file for `{Path}`: {Item}", gamePath, tuple);
                continue;
            }

            var loadoutPart = itemType switch
            {
                LoadoutSourceItemType.Game => new SyncNodePart
                {
                    Hash = tuple.Hash,
                    Size = tuple.Size,
                    LastModifiedTicks = 0,
                },
                LoadoutSourceItemType.Loadout => new SyncNodePart
                {
                    EntityId = tuple.Id,
                    Hash = tuple.Hash,
                    Size = tuple.Size,
                    LastModifiedTicks = 0,
                },
                LoadoutSourceItemType.Intrinsic => default(SyncNodePart),
                LoadoutSourceItemType.Deleted => throw new UnreachableException("Deleted files should've been filtered out"),
            };

            syncTreeEntry = new SyncNode
            {
                SourceItemType = itemType,
                Loadout = loadoutPart,
            };
        }

        MergeStates(latestDiskState, previousDiskState, syncTree);
        AdoptGameInstalledFiles(syncTree, loadout);
        LeaveUserProtectedFilesAlone(syncTree, loadout);
        return syncTree;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Shared Proton caches are skipped even for a flagged item: the flag exists for save files, and a cache
    /// changes on every launch.
    /// </remarks>
    public bool IsSkippedAsProtected(LoadoutItem.ReadOnly item, GamePath path)
    {
        if (!GamePathFilter.ShouldFilter(path)) return false;
        if (IsSharedProtonCache(path)) return true;

        while (item.IsValid())
        {
            if (item.IsProtectedPathsAllowed) return false;
            if (!item.HasParent()) return true;
            item = item.Parent.AsLoadoutItem();
        }

        return true;
    }

    /// <summary>
    /// Takes files on protected paths out of the tree when deploying or deleting them would touch something
    /// that is not the loadout's to touch.
    /// </summary>
    /// <remarks>
    /// Protected paths are not indexed unless the loadout deployed to them, so a file there that the disk
    /// state does not know is the user's own and is never overwritten. A deployed file that no longer matches the
    /// loadout's copy is the user's too (the game rewrites saves as it runs): it must be neither ingested into
    /// the loadout nor overwritten with the mod's version. When there is a previous state to compare against,
    /// <see cref="ProcessSyncTree"/> makes that call instead, since it can also tell an untouched file from a
    /// rewritten one when the mod has been updated.
    /// </remarks>
    private void LeaveUserProtectedFilesAlone(Dictionary<GamePath, SyncNode> syncTree, Loadout.ReadOnly loadout)
    {
        List<GamePath>? toRemove = null;

        foreach (var (path, node) in syncTree)
        {
            if (!node.HaveLoadout || !GamePathFilter.ShouldFilter(path)) continue;

            if (node.HaveLoadout && !node.HaveDisk && !node.HavePrevious)
            {
                var onDisk = loadout.InstallationInstance.Locations.ToAbsolutePath(path);
                if (!onDisk.FileExists) continue;
                Logger.LogWarning("Not deploying `{Path}`: a file of that name is already there and was not put there by the loadout", path);
            }
            else if (node.HaveDisk && !node.HavePrevious && node.Disk.Hash != node.Loadout.Hash)
            {
                Logger.LogWarning("Leaving `{Path}` alone: it differs from the loadout's copy, so the game or the user has changed it since", path);
            }
            else
            {
                continue;
            }

            (toRemove ??= []).Add(path);
        }

        if (toRemove is null) return;
        foreach (var path in toRemove) syncTree.Remove(path);
    }

    private void LogSkippedFilteredItem(EntityId itemId, GamePath gamePath)
    {
        var item = LoadoutItem.Load(Connection.Db, itemId);
        var group = item.IsValid() && item.HasParent() ? item.Parent.AsLoadoutItem().Name : "(no group)";
        Logger.LogWarning("Skipping `{Path}` from `{Group}`: the synchronizer ignores this path", gamePath, group);
    }

    /// <inheritdoc />
    public void MergeStates(IEnumerable<PathPartPair> currentState, IEnumerable<PathPartPair> previousTree, Dictionary<GamePath, SyncNode> loadoutItems)
    {
        foreach (var node in previousTree)
        {
            ref var existing = ref CollectionsMarshal.GetValueRefOrAddDefault(loadoutItems, node.Path, out var exists);
            if (exists)
            {
                existing.Previous = node.Part;
            }
            else
            {
                existing = new SyncNode
                {
                    Previous = node.Part,
                };
            }
        }
        
        foreach (var node in currentState)
        {
            ref var existing = ref CollectionsMarshal.GetValueRefOrAddDefault(loadoutItems, node.Path, out var exists);
            if (exists)
            {
                existing.Disk = node.Part;
            }
            else
            {
                existing = new SyncNode
                {
                    Disk = node.Part,
                };
            }
        }
    }

    /// <inheritdoc />
    public async Task<Dictionary<GamePath, SyncNode>> BuildSyncTree(Loadout.ReadOnly loadout, SynchronizeLoadoutJob? job = null)
    {
        var metadata = await ReindexState(loadout.InstallationInstance, job);
        return BuildSyncTreeFromIndex(loadout, metadata);
    }

    /// <summary>
    /// Builds the tree from a disk state that is already indexed. Split from the public overload so an Apply can
    /// reconcile and prepare the loadout between the reindex and the tree.
    /// </summary>
    private Dictionary<GamePath, SyncNode> BuildSyncTreeFromIndex(Loadout.ReadOnly loadout, GameInstallMetadata.ReadOnly metadata)
    {
        var currentItems = GetDiskStateForGame(metadata);
        var prevItems = ForgetRelocatedFiles(((ILoadoutSynchronizer)this).GetPreviouslyAppliedDiskState(metadata), currentItems);

        return BuildSyncTree(currentItems, prevItems, loadout);
    }

    /// <summary>
    /// Locations whose folder can change under an installation between two syncs, for example when a game that was
    /// found in a Proton prefix is found as its native build. Nothing the loadout deployed there is on disk any more
    /// when that happens, which would read as the user deleting every one of those files.
    /// </summary>
    protected virtual IReadOnlySet<LocationId> RelocatableLocations => FrozenSet<LocationId>.Empty;

    /// <summary>
    /// Takes the previously applied files of a <see cref="RelocatableLocations">relocatable</see> location out of the
    /// previous state when none of them is on disk. The loadout then deploys them again; nothing is deleted.
    /// A location with even one of its files still on disk is not treated as moved, so a file the user deleted
    /// stays deleted.
    /// </summary>
    private List<PathPartPair> ForgetRelocatedFiles(List<PathPartPair> previous, List<PathPartPair> current)
    {
        var relocatable = RelocatableLocations;
        if (relocatable.Count == 0 || previous.Count == 0) return previous;

        var onDisk = current.Select(pair => pair.Path).ToHashSet();
        var moved = relocatable
            .Where(location => previous.Any(pair => pair.Path.LocationId == location)
                               && !previous.Any(pair => pair.Path.LocationId == location && onDisk.Contains(pair.Path)))
            .ToHashSet();
        if (moved.Count == 0) return previous;

        Logger.LogInformation("None of the files last deployed to {Locations} are on disk, so the folder is taken to have moved and they will be deployed again", string.Join(", ", moved));
        return previous.Where(pair => !moved.Contains(pair.Path.LocationId)).ToList();
    }

    /// <summary>
    /// This is a highly optimized way to load all the disk state for a game. It's a sorted merge
    /// join over all the required attributes for the results
    /// </summary>
    public unsafe List<PathPartPair> GetDiskStateForGame(Sdk.Games.GameInstallMetadata.ReadOnly metadata)
    {
        var db = metadata.Db;
        var pairs = new List<PathPartPair>();
        var mainAttrId = db.AttributeCache.GetAttributeId(DiskStateEntry.GameId.Id);
        var pathAttrId = db.AttributeCache.GetAttributeId(DiskStateEntry.Path.Id);
        var hashAttrId = db.AttributeCache.GetAttributeId(DiskStateEntry.Hash.Id);
        var sizeAttrId = db.AttributeCache.GetAttributeId(DiskStateEntry.Size.Id);
        var lastModifiedAttrId = db.AttributeCache.GetAttributeId(DiskStateEntry.LastModified.Id);
        
        // We start with a single reference iterator, that points to the game data we are trying to access
        // Since this data will return results sorted by E (entry Id) we can merge join to any other data 
        // that is sorted in the same order
        using var iterator = db.LightweightDatoms(SliceDescriptor.Create(mainAttrId, metadata));
        
        // Now we have iterators for each field to load
        using var pathIterator = db.LightweightDatoms(SliceDescriptor.Create(pathAttrId));
        using var hashIterator = db.LightweightDatoms(SliceDescriptor.Create(hashAttrId));
        using var sizeIterator = db.LightweightDatoms(SliceDescriptor.Create(sizeAttrId));
        using var lastModifiedIterator = db.LightweightDatoms(SliceDescriptor.Create(lastModifiedAttrId));
        
        // For each entry in the main iterator
        while (iterator.MoveNext())
        {
            // Fast-forward the other iterators to the same entry
            pathIterator.FastForwardTo(iterator.KeyPrefix.E);
            hashIterator.FastForwardTo(iterator.KeyPrefix.E);
            sizeIterator.FastForwardTo(iterator.KeyPrefix.E);
            lastModifiedIterator.FastForwardTo(iterator.KeyPrefix.E);
            
            // Get the location id for the path
            var locationId = MemoryMarshal.Read<LocationId>(pathIterator.ValueSpan.SliceFast(sizeof(EntityId)));
            var pathSpan = pathIterator.ValueSpan.SliceFast(sizeof(EntityId) + sizeof(LocationId));
            // The number of paths in a loadout don't often change much, so we'll put them all through a cache pool, which will
            // allow us to not have to create UTF16 strings on every load of the data
            var pathStr = _fileNamePool.GetOrAdd(pathSpan, Encoding.UTF8);
            var gamePath = new GamePath(locationId, RelativePath.CreateUnsafe(pathStr));

            var pathPartPair = new PathPartPair
            {
                Path = gamePath,
                Part = new SyncNodePart
                {
                    EntityId = iterator.KeyPrefix.E,
                    Hash = MemoryMarshal.Read<Hash>(hashIterator.ValueSpan),
                    Size = MemoryMarshal.Read<Size>(sizeIterator.ValueSpan),
                    LastModifiedTicks = MemoryMarshal.Read<long>(lastModifiedIterator.ValueSpan),
                },
            };
            pairs.Add(pathPartPair);
        }
        return pairs;
    }

    /// <summary>
    /// Converts Mnemonic db disk state entries to path part pairs.
    /// </summary>
    /// <param name="entries"></param>
    /// <returns></returns>
    private IEnumerable<PathPartPair> DiskStateToPathPartPair<T>(T entries) 
        where T : IEnumerable<DiskStateEntry.ReadOnly>
    {
         
        
        foreach (var entry in entries)
        {
            yield return new PathPartPair
            {
                Path = entry.Path,
                Part = new SyncNodePart
                {
                    EntityId = entry.Id,
                    Hash = entry.Hash,
                    Size = entry.Size,
                    LastModifiedTicks = entry.LastModified.UtcTicks,
                },
            };
        }
    }

    /// <summary>
    /// Lets a game change what the generic mapping decided, once every node's <see cref="SyncNode.Actions"/> is
    /// set. A game uses it to keep a loadout file off disk without that reading as the user deleting it.
    /// </summary>
    /// <remarks>
    /// <see cref="ProcessSyncTree"/> also runs for <see cref="ShouldSynchronize"/> and
    /// <see cref="LoadoutToDiskDiff"/>, which have no loadout in hand and run on every refresh, so an override
    /// has to stay cheap and must not read file bytes. Assign a node's actions as a whole rather than clearing
    /// flags: a node left with no action must be <see cref="Actions.DoNothing"/> (bit 0), because a node whose
    /// flags were merely cleared is 0, which <see cref="ShouldSynchronize"/> counts as pending work forever.
    /// Nodes for intrinsic files are added by <see cref="BuildSyncTree{T}"/> with <c>Loadout</c> left at its
    /// default and a <see cref="SyncNode.SourceItemType"/> of <see cref="LoadoutSourceItemType.Intrinsic"/>;
    /// an override can tell them from loadout files by that.
    /// </remarks>
    protected virtual void AdjustActions(Dictionary<GamePath, SyncNode> tree) { }

    /// <summary>
    /// Lets a game refuse to apply a loadout before anything is written. It runs at the start of
    /// <see cref="Synchronize"/>, ahead of the locator id update and the re-index (both write to the database)
    /// and ahead of the loadout switch, which would otherwise deactivate the previous loadout and reset the game
    /// before <see cref="PreflightActions"/> got the chance to refuse. Throw to refuse.
    /// </summary>
    protected virtual Task PreflightLoadout(Loadout.ReadOnly loadout) => Task.CompletedTask;

    /// <summary>
    /// Lets a game refuse a set of actions before the first one runs, for example when it cannot produce a
    /// generated file. Runs in both <see cref="RunActions(Dictionary{GamePath, SyncNode}, Loadout.ReadOnly, SynchronizeLoadoutJob?)"/>
    /// and the game installation overload (which passes no loadout), after the never-written paths are taken
    /// off the tree and before any write. An exception propagates and leaves disk and database untouched.
    /// </summary>
    protected virtual Task PreflightActions(Dictionary<GamePath, SyncNode> tree, Loadout.ReadOnly? loadout) => Task.CompletedTask;

    /// <inheritdoc />
    public void ProcessSyncTree(Dictionary<GamePath, SyncNode> tree)
    {
        foreach (var path in tree.Keys)
        {
            // TODO: sucks that we have to do a lookup here, but we have no way to get the ref to the value otherwise
            ref var item = ref CollectionsMarshal.GetValueRefOrNullRef(tree, path);
            
            var signature = SignatureBuilder.Build(
                diskHash: item.HaveDisk ? item.Disk.Hash : Optional<Hash>.None,
                prevHash: item.HavePrevious ? item.Previous.Hash : Optional<Hash>.None,
                loadoutHash: item.HaveLoadout && item.Loadout.Hash != Hash.Zero ? item.Loadout.Hash : Optional<Hash>.None,
                diskArchived: item.HaveDisk && HaveArchive(item.Disk.Hash),
                prevArchived: item.HavePrevious && HaveArchive(item.Previous.Hash),
                loadoutArchived: item.Loadout.Hash != Hash.Zero && HaveArchive(item.Loadout.Hash),
                pathIsIgnored: IsIgnoredBackupPath(path),
                item.SourceItemType);

            item.Signature = signature;
            item.Actions = ActionMapping.MapActions(signature);

            // A deployed file on a protected path that no longer matches what the last sync wrote has been
            // rewritten by the game. It is the player's now: whether or not the loadout still provides it, the
            // generic rules would delete it or write the mod's copy over it. Dropping the disk state entry also
            // keeps the verdict: once the entry's hash follows the rewrite, the rewrite would look like a change
            // the loadout has not caught up with.
            if (item.HaveDisk && item.HavePrevious && item.Disk.Hash != item.Previous.Hash && GamePathFilter.ShouldFilter(path))
                item.Actions = Actions.ForgetDiskState;
        }

        AdjustActions(tree);
    }

    /// <inheritdoc />
    public async Task<Loadout.ReadOnly> RunActions(Dictionary<GamePath, SyncNode> syncTree, Loadout.ReadOnly loadout, SynchronizeLoadoutJob? job = null)
    {
        await Preflight(loadout.InstallationInstance);
        RefuseTraversalPaths(syncTree);
        KeepNeverWrittenPathsOffDisk(syncTree);
        await PreflightActions(syncTree, loadout);
        ProbeWritableRoots(syncTree, loadout.InstallationInstance.Locations);

        using var _ = await _lock.LockAsync();
        using var tx = Connection.BeginTransaction();
        var gameMetadataId = loadout.InstallationId;

        // Held from before the chain probes the store directory until the links are recorded: a sweep in
        // between cannot see the files this apply is staging and would delete them.
        using var stagingHold = _stagingStore.Hold();
        var plan = _planStore.EnsureComplete(tx, Connection.Db, loadout.InstallationInstance, gameMetadataId, _backendSelector);
        var groups = GroupByBackend(plan, syncTree);
        PreflightRecordedRoots(loadout.InstallationInstance, plan, syncTree);
        await EnforceBackupLimit(syncTree);
        var foldersWithDeletedFiles = new List<IReadOnlyCollection<GamePath>>();
        IReadOnlyList<(GamePath Path, DateTimeOffset Mtime)> plannedTimes = loadout.InstallationInstance.Game is ITimestampOrderedGame timestamped
            ? await timestamped.PluginTimestamps(loadout, syncTree)
            : [];
        var context = new DeploymentContext
        {
            Synchronizer = this,
            Installation = loadout.InstallationInstance,
            MetadataId = gameMetadataId,
            SyncTree = syncTree,
            Loadout = loadout,
            Transaction = tx,
            Job = job,
            PlannedModifiedTimes = plannedTimes.ToDictionary(t => t.Path, t => t.Mtime),
        };

        foreach (var action in ActionsInOrder)
        {
            switch (action)
            {
                case Actions.DoNothing:
                    break;

                case Actions.BackupFile:
                    job?.SetStatus(SyncPhases.BackingUp);
                    foreach (var group in groups) await group.Backend.Backup(context, group.Nodes);
                    break;

                case Actions.IngestFromDisk:
                    job?.SetStatus(SyncPhases.IngestingChanges);
                    foreach (var group in groups) group.Backend.Ingest(context, group.Nodes);
                    break;
                
                case Actions.AdaptLoadout:
                    job?.SetStatus(SyncPhases.UpdatingLoadout);
                    foreach (var group in groups) await group.Backend.AdaptLoadout(context, group.Nodes);
                    break;

                case Actions.DeleteFromDisk:
                    job?.SetStatus(SyncPhases.Deleting);
                    foreach (var group in groups) foldersWithDeletedFiles.Add(group.Backend.Delete(context, group.Nodes));
                    break;

                case Actions.ExtractToDisk:
                    job?.SetStatus(SyncPhases.Deploying);
                    foreach (var group in groups) await group.Backend.Extract(context, group.Nodes);
                    break;
                
                case Actions.WriteIntrinsic:
                    job?.SetStatus(SyncPhases.WritingIntrinsics);
                    foreach (var group in groups) await group.Backend.WriteIntrinsics(context, group.Nodes);
                    break;

                case Actions.AddReifiedDelete:
                    job?.SetStatus(SyncPhases.UpdatingDeletedFiles);
                    foreach (var group in groups) group.Backend.AddReifiedDeletes(context, group.Nodes);
                    break;

                case Actions.WarnOfUnableToExtract:
                    WarnOfUnableToExtract(syncTree);
                    break;

                case Actions.WarnOfConflict:
                    WarnOfConflict(syncTree);
                    break;

                case Actions.ForgetDiskState:
                    ActionForgetDiskState(syncTree, tx, gameMetadataId);
                    break;
                
                default:
                    throw new InvalidOperationException($"Unknown action: {action}");
            }
        }

        // After the loop so every file is in place: the ones extracted got their time as they were written, the
        // ones already there get theirs now.
        if (plannedTimes.Count > 0)
        {
            foreach (var group in groups)
                await group.Backend.StampTimestamps(context, plannedTimes.Where(t => group.Nodes.ContainsKey(t.Path)).ToList());
        }

        job?.SetStatus(SyncPhases.RecordingChanges);
        
        tx.Add(gameMetadataId, Sdk.Games.GameInstallMetadata.LastSyncedLoadout, loadout.Id);
        tx.Add(gameMetadataId, Sdk.Games.GameInstallMetadata.LastSyncedLoadoutTransaction, EntityId.From(tx.ThisTxId.Value));
        tx.Add(gameMetadataId, Sdk.Games.GameInstallMetadata.LastScannedDiskStateTransaction, EntityId.From(tx.ThisTxId.Value));
        tx.Add(loadout.Id, Loadout.LastAppliedDateTime, DateTime.UtcNow);
        await tx.Commit();

        loadout = loadout.Rebase();
        var newState = DiskStateEntry.FindByGame(loadout.Db, loadout.Installation);

        // Clean up empty directories
        for (var i = 0; i < groups.Count; i++)
        {
            if (i < foldersWithDeletedFiles.Count && foldersWithDeletedFiles[i].Count > 0)
                groups[i].Backend.CleanDirectories(context, foldersWithDeletedFiles[i], newState);
        }

        // The collector sweeps the staging store, which waits for every hold.
        stagingHold.Dispose();

        job?.SetStatus(SyncPhases.CleaningUp);
        await _garbageCollectorRunner.RunAsync();


        return loadout;
    }

    /// <summary>
    /// Updates the locator IDs on the loadout if the game has been updated by the store.
    /// This should be called before building the sync tree.
    /// </summary>
    private async ValueTask<Loadout.ReadOnly> UpdateLocatorIds(Loadout.ReadOnly loadout)
    {
        var locator = loadout.InstallationInstance.LocatorResult.Locator;
        if (!locator.TryLocate(loadout.Game, out var gameLocatorResult))
        {
            // NOTE(erri120): It would be very odd if we re-query the game, and it's not installed anymore
            Logger.LogCritical("Found no installation of the game `{Store}`/`{Game}` anymore!", loadout.Installation.Store, loadout.Game.DisplayName);
            return loadout;
        }

        var metadataLocatorIds = gameLocatorResult.LocatorIds;
        var newLocatorIds = metadataLocatorIds.Distinct().ToArray();

        if (newLocatorIds.Length != metadataLocatorIds.Length)
            Logger.LogWarning("Found duplicate locator IDs `{LocatorIds}` on gameLocatorResult for game `{Game}` while updating locator IDs", metadataLocatorIds, loadout.InstallationInstance.Game.DisplayName);

        var locatorsToAdd = newLocatorIds.Except(loadout.LocatorIds).ToArray();
        var locatorsToRemove = loadout.LocatorIds.Except(newLocatorIds).ToArray();

        // No reason to change the loadout if the version is the same
        if (locatorsToAdd.Length == 0 && locatorsToRemove.Length == 0)
            return loadout;

        if (Logger.IsEnabled(LogLevel.Information))
        {
            var sCurrent = loadout.LocatorIds.Select(x => x.Value).ToArray();
            var sToAdd = locatorsToAdd.Select(x => x.Value).ToArray();
            var sToRemove = locatorsToRemove.Select(x => x.Value).ToArray();
            Logger.LogInformation("Locator IDs changed Current=`{CurrentIds}` ToAdd=`{ToAdd}` ToRemove=`{ToRemove}`", sCurrent, sToAdd, sToRemove);
        }

        using var tx = Connection.BeginTransaction();

        if (_fileHashService.TryGetVanityVersion((gameLocatorResult.Store, newLocatorIds), out var vanityVersion))
        {
            tx.Add(loadout, Loadout.GameVersion, vanityVersion);
        }
        else
        {
            tx.Add(loadout, Loadout.GameVersion, VanityVersion.DefaultValue);
            Logger.LogWarning("Found no vanity version for locator IDs `{LocatorIds}` (`{Store}`)", newLocatorIds, gameLocatorResult.Store);
        }

        foreach (var id in locatorsToRemove)
            tx.Retract(loadout, Loadout.LocatorIds, id);
        foreach (var id in locatorsToAdd)
            tx.Add(loadout, Loadout.LocatorIds, id);

        var result = await tx.Commit();
        return loadout.Rebase(result.Db);
    }

    private async ValueTask<Loadout.ReadOnly> ReprocessOverrides(Loadout.ReadOnly loadout)
    {
        // Make a lookup set of the new files based on current locator IDs
        var versionFiles = _fileHashService
            .GetGameFiles((loadout.Installation.Store, loadout.LocatorIds.ToArray()))
            .Select(file => file.Path)
            .ToHashSet();

        // Find all files in the overrides that match a path in the new files
        var toDelete = from grp in LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemGroup().OfTypeLoadoutOverridesGroup()
            from item in grp.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath()
            let path = (GamePath)item.TargetPath
            where versionFiles.Contains(path)
            select item;

        // No files to process, return early
        if (!toDelete.Any())
            return loadout;

        using var tx = Connection.BeginTransaction();
        var gameMetadataId = loadout.InstallationId;

        // Delete all the matching override files
        foreach (var file in toDelete)
        {
            tx.Delete(file, recursive: false);

            // The backed up file is being 'promoted' to a game file, which needs
            // to be rooted explicitly in case the user uses a feature like 'undo'
            // to roll back a game version on a store (like Xbox/Epic) which does
            // not support downloading non-current version(s).
            if (!file.TryGetAsLoadoutFile(out var loadoutFile))
                continue;

            _ = new GameBackedUpFile.New(tx)
            {
                Hash = loadoutFile.Hash,
                GameInstallId = gameMetadataId,
            };
        }

        var result = await tx.Commit();
        return loadout.Rebase(result.Db);
    }

    /// <summary>
    /// Alternative to <see cref="RunActions"/> that ignores changes and optionally clears the last sync loadout metadata
    /// </summary>
    public async Task RunActions(Dictionary<GamePath, SyncNode> syncTree, GameInstallation gameInstallation)
    {
        await Preflight(gameInstallation);
        RefuseTraversalPaths(syncTree);
        KeepNeverWrittenPathsOffDisk(syncTree);
        await PreflightActions(syncTree, null);
        ProbeWritableRoots(syncTree, gameInstallation.Locations);

        using var _ = await _lock.LockAsync();
        using var tx = Connection.BeginTransaction();

        var metadata = _gameRegistry.ForceGetMetadata(gameInstallation);

        // See the loadout overload. Recorded roots are not preflighted here: a root that no longer accepts
        // its backend still has to be reset through that backend before it can be moved to another one.
        using var stagingHold = _stagingStore.Hold();
        var plan = _planStore.EnsureComplete(tx, Connection.Db, gameInstallation, metadata, _backendSelector);
        var groups = GroupByBackend(plan, syncTree);
        await EnforceBackupLimit(syncTree);
        var foldersWithDeletedFiles = new List<IReadOnlyCollection<GamePath>>();
        var context = new DeploymentContext
        {
            Synchronizer = this,
            Installation = gameInstallation,
            MetadataId = metadata,
            SyncTree = syncTree,
            Transaction = tx,
        };

        foreach (var action in ActionsInOrder)
        {
            switch (action)
            {
                case Actions.DoNothing:
                    break;

                case Actions.BackupFile:
                    foreach (var group in groups) await group.Backend.Backup(context, group.Nodes);
                    break;
                
                case Actions.AdaptLoadout:
                    if (ApplicationConstants.IsDebug && syncTree.Any(n => n.Value.Actions.HasFlag(Actions.AdaptLoadout)))
                        throw new InvalidOperationException("Cannot adapt loadout when not in a loadout context");
                    break;
                
                case Actions.WriteIntrinsic:
                    if (ApplicationConstants.IsDebug && syncTree.Any(n => n.Value.Actions.HasFlag(Actions.WriteIntrinsic)))
                        throw new InvalidOperationException("Cannot write intrinsic files when not in a loadout context");
                    break;

                case Actions.IngestFromDisk:
                    if (ApplicationConstants.IsDebug && syncTree.Any(n => n.Value.Actions.HasFlag(Actions.IngestFromDisk)))
                        throw new InvalidOperationException("Cannot ingest files from disk when not in a loadout context");
                    break;

                case Actions.DeleteFromDisk:
                    foreach (var group in groups) foldersWithDeletedFiles.Add(group.Backend.Delete(context, group.Nodes));
                    break;

                case Actions.ExtractToDisk:
                    foreach (var group in groups) await group.Backend.Extract(context, group.Nodes);
                    break;

                case Actions.AddReifiedDelete:
                    if (ApplicationConstants.IsDebug && syncTree.Any(n => n.Value.Actions.HasFlag(Actions.AddReifiedDelete)))
                        throw new InvalidOperationException("Cannot add reified deletes when not in a loadout context");
                    break;

                case Actions.WarnOfUnableToExtract:
                    WarnOfUnableToExtract(syncTree);
                    break;

                case Actions.WarnOfConflict:
                    WarnOfConflict(syncTree);
                    break;

                case Actions.ForgetDiskState:
                    ActionForgetDiskState(syncTree, tx, metadata);
                    break;

                default:
                    throw new InvalidOperationException($"Unknown action: {action}");
            }
        }

        if (metadata.Contains(Sdk.Games.GameInstallMetadata.LastSyncedLoadout))
        {
            tx.Retract(metadata, Sdk.Games.GameInstallMetadata.LastSyncedLoadout, (EntityId)metadata.LastSyncedLoadout);
            tx.Retract(metadata, Sdk.Games.GameInstallMetadata.LastSyncedLoadoutTransaction, (EntityId)metadata.LastSyncedLoadoutTransaction);
        }

        tx.Add(metadata, Sdk.Games.GameInstallMetadata.LastScannedDiskStateTransaction, EntityId.From(tx.ThisTxId.Value));

        var result = await tx.Commit();

        var newMetadata = metadata.Rebase(result.Db);

        // Clean up empty directories
        var newDiskState = DiskStateEntry.FindByGame(newMetadata.Db, newMetadata);
        for (var i = 0; i < groups.Count; i++)
        {
            if (i < foldersWithDeletedFiles.Count && foldersWithDeletedFiles[i].Count > 0)
                groups[i].Backend.CleanDirectories(context, foldersWithDeletedFiles[i], newDiskState);
        }
    }

    /// <summary>
    /// Refuses the apply when a root recorded with a backend other than copy no longer accepts it, before
    /// any file is written. The game having moved to another drive, or the staging store having moved, would
    /// otherwise surface halfway through extraction as a failed link.
    /// </summary>
    /// <remarks>
    /// Only roots the tree writes into are checked, so a sync with nothing to write touches no disk, as in
    /// <see cref="ProbeWritableRoots"/>. Deleting works whatever the backend can no longer do.
    /// </remarks>
    private void PreflightRecordedRoots(GameInstallation installation, DeploymentPlan plan, Dictionary<GamePath, SyncNode> syncTree)
    {
        if (RecordedRootPreflight.IsSuspended) return;

        const Actions writes = Actions.ExtractToDisk | Actions.WriteIntrinsic;
        foreach (var root in plan.Roots)
        {
            if (root.Backend == DeploymentBackendId.Copy) continue;
            if (!syncTree.Any(x => (x.Value.Actions & writes) != 0 && plan.BackendFor(x.Key) == root.Backend && IsInside(root.Root, x.Key))) continue;

            var preflight = _backends[root.Backend].Preflight(installation, root.Root);
            if (preflight.Accepted) continue;

            throw new SynchronizerPreflightException(
                $"{installation.Game.DisplayName} is set to deploy '{root.Root}' with {root.Backend}, which does not work there any more: {preflight.Reason}. " +
                $"Nothing was changed. Run 'silvermod deployment --game \"{installation.Game.DisplayName}\" --recommended' to move it to a backend that works, then apply again."
            );
        }

        static bool IsInside(GamePath root, GamePath path) =>
            root.LocationId == path.LocationId && (root.Path == RelativePath.Empty || path.Path == root.Path || path.Path.InFolder(root.Path));
    }

    /// <summary>
    /// We'll limit backups to 5GB, for now we should never see much more than this
    /// of modified game files.
    /// </summary>
    private static Size MaximumBackupSize => Size.GB * 5;

    /// <summary>
    /// Fails before anything is written when the files about to be backed up, over every backend's share of the
    /// tree together, exceed <see cref="MaximumBackupSize"/>.
    /// </summary>
    private async Task EnforceBackupLimit(Dictionary<GamePath, SyncNode> syncTree)
    {
        var sizes = new ConcurrentBag<Size>();
        await Parallel.ForEachAsync(syncTree, async (item, _) =>
        {
            var node = item.Value;
            if (!node.Actions.HasFlag(Actions.BackupFile)) return;
            if (await _fileStore.HaveFile(node.Disk.Hash)) return;
            sizes.Add(node.Disk.Size);
        });

        var totalSize = sizes.Sum(static x => x);
        if (totalSize <= MaximumBackupSize) return;

        var nodeSignatures = syncTree
            .Where(static x => x.Value.Actions.HasFlag(Actions.BackupFile))
            .Select(static x => x.Value.Signature)
            .GroupBy(signature => signature)
            .Select(static grp => new { Signature = grp.Key, FileCount = grp.Count() })
            .ToList();

        Logger.LogError(
            """
            Cannot backup {FileCount} files with total size {TotalSize}, which exceeds maximum of {MaximumSize}.
            Node signatures:
            {Signatures}
            """,
            sizes.Count,
            totalSize,
            MaximumBackupSize,
            string.Join(Environment.NewLine, nodeSignatures.Select(sig => $"  - {sig.Signature}: {sig.FileCount} files"))
        );

        throw new Exception($"Cannot backup files, total size is {totalSize}, which is larger than the maximum of {MaximumBackupSize}");
    }

    /// <summary>
    /// The part of the sync tree one backend carries out.
    /// </summary>
    private sealed record BackendGroup(IDeploymentBackend Backend, IReadOnlyDictionary<GamePath, SyncNode> Nodes);

    /// <summary>
    /// Splits the tree by the backend the plan routes each path to, copy first and then by id, so phases run
    /// in the same order every time. When one backend owns everything it gets the original dictionary.
    /// </summary>
    /// <remarks>
    /// Every backend the plan names must be registered, even one with no nodes in this tree: a plan row pointing at
    /// a missing backend means the root's files are managed by something this run cannot undo. The backup size cap
    /// applies per group once a plan splits the tree.
    /// </remarks>
    private List<BackendGroup> GroupByBackend(DeploymentPlan plan, Dictionary<GamePath, SyncNode> syncTree)
    {
        foreach (var root in plan.Roots)
        {
            if (_backends.ContainsKey(root.Backend)) continue;
            throw new InvalidOperationException(
                $"Deployment backend '{root.Backend}' of root '{root.Root}' is not registered. " +
                "Switch the root back with ISynchronizerService.ChangeDeploymentBackend."
            );
        }

        var routed = new Dictionary<DeploymentBackendId, Dictionary<GamePath, SyncNode>>();
        foreach (var (path, node) in syncTree)
        {
            var id = plan.BackendFor(path);
            if (!routed.TryGetValue(id, out var nodes)) routed[id] = nodes = new Dictionary<GamePath, SyncNode>();
            nodes.Add(path, node);
        }

        if (routed.Count <= 1)
        {
            var only = routed.Count == 1 ? routed.Keys.Single() : DeploymentBackendId.Copy;
            return [new BackendGroup(_backends[only], syncTree)];
        }

        return routed
            .OrderBy(kv => kv.Key == DeploymentBackendId.Copy ? 0 : 1)
            .ThenBy(kv => kv.Key.Value, StringComparer.Ordinal)
            .Select(kv => new BackendGroup(_backends[kv.Key], kv.Value))
            .ToList();
    }

    private void WarnOfConflict(Dictionary<GamePath, SyncNode> tree)
    {
        
        foreach (var (path, node) in tree)
        {
            if (!node.Actions.HasFlag(Actions.WarnOfConflict))
                continue;
            Logger.LogWarning("Conflict in {Path}", path);
        }
    }

    private void WarnOfUnableToExtract(Dictionary<GamePath, SyncNode> groupings)
    {
        foreach (var (path, node) in groupings)
        {
            if (!node.Actions.HasFlag(Actions.WarnOfUnableToExtract))
                continue;
            Logger.LogWarning("Unable to extract {Path}", path);
        }
    }

    private void ActionForgetDiskState(Dictionary<GamePath, SyncNode> syncTree, ITransaction tx, GameInstallMetadataId gameMetadataId)
    {
        foreach (var (path, node) in syncTree)
        {
            if (!node.Actions.HasFlag(Actions.ForgetDiskState)) continue;

            Logger.LogWarning("Leaving `{Path}` on disk: it differs from what the loadout deployed, so it is the player's and no longer tracked", path);
            DiskStateEntries.Retract(tx, gameMetadataId, path, node);
        }
    }

    public record struct AddedEntry
    {
        public required LoadoutItem.New LoadoutItem { get; init; }
        public required LoadoutItemWithTargetPath.New LoadoutItemWithTargetPath { get; init; }
        public required LoadoutFile.New LoadoutFileEntry { get; init; }
    }

    /// <inheritdoc />
    public virtual async Task<Loadout.ReadOnly> Synchronize(Loadout.ReadOnly loadout, SynchronizeLoadoutJob? job = null)
    {
        // Refuse before the locator ids are updated and the game folder is re-indexed: both write to the
        // database, and neither can mean anything when the prefix they read from is gone.
        await Preflight(loadout.InstallationInstance);

        loadout = loadout.Rebase();

        // After the rebase so a game judges the loadout as it is now, not the snapshot the caller held.
        await PreflightLoadout(loadout);
        
        // Update locator IDs before building the sync tree
        loadout = await UpdateLocatorIds(loadout);
        
        // If we are swapping loadouts, then we need to synchronize the previous loadout first to ingest
        // any changes, then we can apply the new loadout.
        if (Sdk.Games.GameInstallMetadata.LastSyncedLoadout.TryGetValue(loadout.Installation, out var lastAppliedId) && lastAppliedId != loadout.Id)
        {
            var prevLoadout = Loadout.Load(loadout.Db, lastAppliedId);
            if (prevLoadout.IsValid())
            {
                await _loadoutManager.DeactivateCurrentLoadout(loadout.InstallationInstance);
                await _loadoutManager.ActivateLoadout(loadout);
                return loadout.Rebase();
            }
        }

        job?.SetStatus(SyncPhases.Collecting);
        var metadata = await ReindexState(loadout.InstallationInstance, job);

        // Reconcile sees the fresh disk state, and the game's preparation sees the group it writes.
        loadout = await ReconcileGameInstalledContent(loadout, metadata);
        loadout = await PrepareLoadoutForSync(loadout);

        var tree = BuildSyncTreeFromIndex(loadout, metadata);
        ProcessSyncTree(tree);
        loadout = await RunActions(tree, loadout, job);

        // Move any override files that now match game files after sync
        loadout = await ReprocessOverrides(loadout);
        return loadout;
    }

    public async Task<GameInstallMetadata.ReadOnly> RescanFiles(GameInstallation gameInstallation)
    {
        // Make sure the file hashes are up to date
        await _fileHashService.GetFileHashesDb();
        return await ReindexState(gameInstallation);
    }

    /// <summary>
    /// All actions, in execution order.
    /// </summary>
    private static readonly Actions[] ActionsInOrder = Enum.GetValues<Actions>().OrderBy(a => (ushort)a).ToArray();

    /// <summary>
    /// Returns true if the given hash has been archived.
    /// </summary>
    protected bool HaveArchive(Hash hash)
    {
        return _fileStore.HaveFile(hash).Result;
    }

    /// <summary>
    /// Returns true if the loadout state doesn't match the last scanned disk state.
    /// </summary>
    public bool ShouldSynchronize(Loadout.ReadOnly loadout, IEnumerable<PathPartPair> previousDiskState, IEnumerable<PathPartPair> lastScannedDiskState)
    {
        var syncTree = BuildSyncTree(lastScannedDiskState, previousDiskState, loadout);
        // Process the sync tree to get the actions populated in the nodes
        ProcessSyncTree(syncTree);
        
        return syncTree.Any(n => n.Value.Actions != Actions.DoNothing);
    }
    
    /// <inheritdoc />
    public FileDiffTree LoadoutToDiskDiff(Loadout.ReadOnly loadout, List<PathPartPair> previousDiskState, List<PathPartPair> lastScannedDiskState)
    {
        var syncTree = BuildSyncTree(lastScannedDiskState, previousDiskState, loadout);
        // Process the sync tree to get the actions populated in the nodes
        ProcessSyncTree(syncTree);

        List<KeyValuePair<GamePath, DiskDiffEntry>> diffs = [];

        foreach (var (path, node) in syncTree)
        {
            var syncNode = node;
            var actions = syncNode.Actions;
            DiskDiffEntry entry;
            
            if (actions.HasFlag(Actions.DoNothing))
            {
                entry = new DiskDiffEntry
                {
                    Hash = node.Loadout.Hash,
                    Size = node.Loadout.Size,
                    ChangeType = FileChangeType.None,
                    GamePath = path,
                };
                diffs.Add(KeyValuePair.Create(path, entry));
            }
            else if (actions.HasFlag(Actions.WarnOfUnableToExtract))
            {
                entry = new DiskDiffEntry
                {
                    Hash = node.Loadout.Hash,
                    Size = node.Loadout.Size,
                    ChangeType = FileChangeType.Added,
                    GamePath = path,
                };
            }
            else if (actions.HasFlag(Actions.ExtractToDisk))
            {
                entry = new DiskDiffEntry
                {
                    Hash = node.Loadout.Hash,
                    Size = node.Loadout.Size,
                    // If paired with a delete action, this is a modified file not a new one
                    ChangeType = actions.HasFlag(Actions.DeleteFromDisk) ? FileChangeType.Modified : FileChangeType.Added,
                    GamePath = path,
                };
            }
            else if (actions.HasFlag(Actions.DeleteFromDisk))
            {
                entry = new DiskDiffEntry
                {
                    Hash = node.Disk.Hash,
                    Size = node.Disk.Size,
                    ChangeType = FileChangeType.Removed,
                    GamePath = path,
                };
            }
            else if (actions.HasFlag(Actions.IngestFromDisk))
            {
                // File is already on disk and will not be changed
                entry = new DiskDiffEntry
                {
                    Hash = node.Disk.Hash,
                    Size = node.Disk.Size,
                    ChangeType = FileChangeType.None,
                    GamePath = path,
                };
            }
            else if (actions.HasFlag(Actions.AddReifiedDelete))
            {
                // File is not on disk and will not end up on disk, so don't show it
                continue;
            }
            else
            {
                // This really should become some sort of error state
                entry = new DiskDiffEntry
                {
                    Hash = Hash.Zero,
                    Size = Size.Zero,
                    ChangeType = FileChangeType.None,
                    GamePath = path,
                };
            }
            
            diffs.Add(KeyValuePair.Create(path, entry));
        }

        return FileDiffTree.Create(diffs);
    }

    /// <summary>
    /// Reindex the state of the game, running a transaction if changes are found
    /// </summary>
    public async Task<GameInstallMetadata.ReadOnly> ReindexState(GameInstallation installation, SynchronizeLoadoutJob? job = null)
    {
        using var _ = await _lock.LockAsync();

        var metadata = _gameRegistry.ForceGetMetadata(installation);
        using var tx = Connection.BeginTransaction();

        // Index the state
        var changed = await ReindexState(metadata, installation, tx, job);

        if (!metadata.Contains(Sdk.Games.GameInstallMetadata.InitialDiskStateTransaction))
        {
            // No initial state, so set this transaction as the initial state
            changed = true;
            tx.Add(metadata.Id, Sdk.Games.GameInstallMetadata.InitialDiskStateTransaction, EntityId.From(TxId.Tmp.Value));
        }

        if (changed)
        {
            tx.Add(metadata, Sdk.Games.GameInstallMetadata.LastScannedDiskStateTransactionId, EntityId.From(TxId.Tmp.Value));
            await tx.Commit();
        }

        return GameInstallMetadata.Load(Connection.Db, metadata);
    }

    /// <inheritdoc />
    public async Task RescanExternalChanges(Loadout.ReadOnly loadout)
    {
        var metadata = await ReindexState(loadout.InstallationInstance);

        loadout = loadout.Rebase();
        if (!LoadoutOverridesGroup.FindByOverridesFor(loadout.Db, loadout.Id).TryGetFirst(out var overrides)) return;

        // A sync would put these on its delete path too, but it would also deploy the loadout. Needing a previous
        // entry keeps a file that was never on disk, such as one still waiting to be deployed, in the list.
        var gone = BuildSyncTreeFromIndex(loadout, metadata).Values
            .Where(node => !node.HaveDisk && node.HavePrevious && node.Loadout.HaveEntityId && IsInGroup(loadout.Db, node.Loadout.EntityId, overrides.Id))
            .Select(node => node.Loadout.EntityId)
            .ToArray();
        if (gone.Length == 0) return;

        using var _ = await _lock.LockAsync();
        using var tx = Connection.BeginTransaction();
        foreach (var id in gone) tx.Delete(id, recursive: false);
        await tx.Commit();
    }

    private FrozenDictionary<GamePath, DiskStateEntry.ReadOnly> GetDiskState(GameInstallMetadata.ReadOnly gameInstallMetadata)
    {
        var entities = DiskStateEntry.FindByGame(gameInstallMetadata.Db, gameInstallMetadata);
        var result = new Dictionary<GamePath, DiskStateEntry.ReadOnly>(capacity: entities.Count);

        foreach (var entity in entities)
        {
            GamePath gamePath = entity.Path;
            ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(result, gamePath, out var isDuplicate);
            if (isDuplicate)
            {
                Logger.LogWarning("Duplicate path in disk state: `{Path}`", gamePath);
            }

            entry = entity;
        }

        return result.ToFrozenDictionary();
    }

    /// <summary>
    /// A protected path only has a disk state entry if the loadout deployed to it. Those files stay indexed so
    /// the synchronizer can see them change or disappear; every other file on a protected path stays unseen.
    /// </summary>
    private static bool IsTrackedProtectedFile(FrozenDictionary<GamePath, DiskStateEntry.ReadOnly> previousState, GamePath path)
    {
        return !IsSharedProtonCache(path) && previousState.ContainsKey(path);
    }

    /// <summary>
    /// Reindex the state of the game
    /// </summary>
    private async Task<bool> ReindexState(GameInstallMetadata.ReadOnly metadata, GameInstallation installation, ITransaction tx, SynchronizeLoadoutJob? job)
    {
        var previousState = GetDiskState(metadata);

        var indexGameResult = await _gameLocationsService.IndexGame(
            installation: installation,
            previousDiskState: previousState,
            filter: Synchronizers.GamePathFilters.Create(path => GamePathFilter.ShouldFilter(path) && !IsTrackedProtectedFile(previousState, path)),
            cancellationToken: CancellationToken.None,
            progress: job is null ? null : (done, total) => job.ReportProgress(SyncPhases.Collecting, done, total)
        );

        foreach (var (gamePath, result) in indexGameResult.NewFiles)
        {
            _ = new DiskStateEntry.New(tx, tx.TempId(DiskStateEntry.EntryPartition))
            {
                Path = gamePath.ToGamePathParentTuple(metadata.Id),
                Hash = result.Hash,
                Size = result.Size,
                LastModified = result.LastModified,
                GameId = metadata.Id,
            };
        }

        foreach (var (gamePath, result) in indexGameResult.ModifiedFiles)
        {
            var didFind = previousState.TryGetValue(gamePath, out var previousDiskStateEntry);
            Debug.Assert(didFind, "modified file should be in previous state");

            tx.Add(previousDiskStateEntry.Id, DiskStateEntry.Size, result.Size);
            tx.Add(previousDiskStateEntry.Id, DiskStateEntry.Hash, result.Hash);
            tx.Add(previousDiskStateEntry.Id, DiskStateEntry.LastModified, result.LastModified);
        }

        foreach (var gamePath in indexGameResult.RemovedFiles)
        {
            var didFind = previousState.TryGetValue(gamePath, out var previousDiskStateEntry);
            Debug.Assert(didFind, "modified file should be in previous state");

            tx.Delete(previousDiskStateEntry.Id, recursive: false);
        }

        var hasChanged = indexGameResult.NewFiles.Count != 0 || indexGameResult.ModifiedFiles.Count != 0 || indexGameResult.RemovedFiles.Count != 0;
        if (!hasChanged) return false;

        tx.Add(metadata.Id, GameInstallMetadata.LastScannedDiskStateTransaction, EntityId.From(TxId.Tmp.Value));
        return true;
    }

    public async ValueTask BuildProcessRun(Loadout.ReadOnly loadout, GameInstallMetadata.ReadOnly state, CancellationToken cancellationToken)
    {
        // The loadout-switch path reaches the hook only here: Synchronize hands the switch to the loadout
        // manager, which ends in this method.
        loadout = await ReconcileGameInstalledContent(loadout, state);
        loadout = await PrepareLoadoutForSync(loadout);
        var diskStateEntries = DiskStateEntry.FindByGame(state.Db, state);
        var tree = BuildSyncTree(DiskStateToPathPartPair(diskStateEntries), DiskStateToPathPartPair(diskStateEntries), loadout);
        ProcessSyncTree(tree);
        await RunActions(tree, loadout);
    }

    /// <summary>
    /// Runs before a sync tree is built for an Apply or a loadout switch, so a game can bring the loadout up to
    /// date first (for example by seeding data from a game file). It may commit to the database and must return
    /// the loadout at the revision the tree should be built on. It is not called from the public
    /// <c>BuildSyncTree(loadout, job)</c>, which diagnostics and the CLI use and which must never write.
    /// </summary>
    protected virtual ValueTask<Loadout.ReadOnly> PrepareLoadoutForSync(Loadout.ReadOnly loadout) => ValueTask.FromResult(loadout);

    /// <inheritdoc />
    public virtual bool IsIgnoredBackupPath(GamePath path) => false;

    /// <inheritdoc />
    public virtual bool CanMoveOverrides(Loadout.ReadOnly loadout, GamePath path) => true;

    /// <inheritdoc />
    public virtual bool IsGameWrittenPath(GamePath path) => GameWrittenFiles.IsLogOrCrashFile(path);

    private IGamePathFilter? _composedFilter;

    /// <inheritdoc />
    /// <remarks>
    /// The base rule joined with <see cref="GameSpecificPathFilter"/>. It is composed on first use rather than in
    /// an initializer because an initializer cannot call the virtual member.
    /// </remarks>
    public IGamePathFilter GamePathFilter => _composedFilter ??= Synchronizers.GamePathFilters.Any(BaseGamePathFilter, GameSpecificPathFilter);

    /// <summary>
    /// The rule every game gets: save files are never indexed, backed up, overwritten or deleted, whichever
    /// folder the game's <see cref="LocationId.Saves"/> location resolves to. A game's own rules cover only the
    /// save folders its author knew about, and a reset or unmanage would otherwise delete the rest.
    /// </summary>
    /// <remarks>
    /// Proton's shared shader caches are filtered too, see <see cref="IsSharedProtonCache"/>.
    /// </remarks>
    private static readonly IGamePathFilter BaseGamePathFilter = Synchronizers.GamePathFilters.Create(path => path.LocationId == LocationId.Saves || IsSharedProtonCache(path) || IsWriteProbe(path));

    /// <summary>
    /// Whether the path is a shader cache that Proton (vkd3d-proton, DXVK) drops next to the running game.
    /// </summary>
    /// <remarks>
    /// Proton writes these into the game's working directory, so they show up in <see cref="LocationId.Game"/>
    /// or wherever the game runs from. They change on every launch, so indexing them would report an external
    /// change and back up a new copy after each play session. The match is on the file name alone, so it holds
    /// for any location.
    /// </remarks>
    private static bool IsSharedProtonCache(GamePath path)
    {
        var name = path.FileName;
        return name.ToString().StartsWith("vkd3d-proton.cache", StringComparison.Ordinal)
               || name.Extension == DxvkCacheExtension;
    }

    private static readonly Extension DxvkCacheExtension = new(".dxvk-cache");

    /// <summary>
    /// Whether the path is a file <see cref="ProbeWritableRoots"/> or a link backend's preflight creates (the
    /// latter add a suffix to the name). They are deleted again right away, but a crash in between would
    /// leave one behind, and it must never be indexed as the player's file.
    /// </summary>
    private static bool IsWriteProbe(GamePath path) => path.FileName.ToString().StartsWith(ProtonMessages.ProbeFileName, StringComparison.Ordinal);

    /// <inheritdoc />
    public Task Preflight(GameInstallation installation)
    {
        // Only the prefix path is checked, never Steam's configuration: guessing another prefix would write
        // into a folder the player did not mean, so an unclear case is a message instead.
        if (installation.LocatorResult.WinePrefixPath is { } prefix && !ProtonPrefix.IsReady(prefix))
            return Task.FromException(new SynchronizerPreflightException(ProtonMessages.MissingPrefix(installation.Game.DisplayName, prefix)));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Checks that every location the tree is about to write into can be written, before anything is.
    /// </summary>
    /// <remarks>
    /// A write that fails halfway leaves some files extracted and the database not yet updated, which the next
    /// sync reads as external changes. A tree without write actions probes nothing, so a no-op sync never
    /// touches the disk or bumps a folder's modification time. Locations the filter rejects are skipped:
    /// the synchronizer never writes there.
    /// </remarks>
    private void ProbeWritableRoots(Dictionary<GamePath, SyncNode> syncTree, GameLocations locations)
    {
        const Actions writes = Actions.ExtractToDisk | Actions.WriteIntrinsic | Actions.DeleteFromDisk;

        HashSet<LocationId> probed = [];
        foreach (var (path, node) in syncTree)
        {
            if ((node.Actions & writes) == 0) continue;
            if (!probed.Add(path.LocationId)) continue;
            if (GamePathFilter.ShouldFilter(new GamePath(path.LocationId, RelativePath.Empty))) continue;

            // A root that does not exist yet (My Games/<game>) is created by the first extract, so what has to
            // be writable is the closest folder that does exist.
            var folder = locations[path.LocationId].Path;
            while (!folder.DirectoryExists() && folder.Parent != folder) folder = folder.Parent;

            var probe = folder.Combine(ProtonMessages.ProbeFileName);
            try
            {
                probe.Create().Dispose();
                probe.Delete();
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                throw new SynchronizerPreflightException(ProtonMessages.UnwritableFolder(folder));
            }
        }
    }

    /// <summary>
    /// Whether the synchronizer must leave the file at the given path exactly as it is: no write, delete, backup or
    /// ingest, whichever entry point runs the actions.
    /// </summary>
    /// <remarks>
    /// A loadout file that would be deployed to such a path makes the sync fail before anything runs. Every other
    /// action on the path is dropped from the sync tree, which leaves the disk state as indexed.
    /// <see cref="IsIgnoredBackupPath"/> is no substitute: it only decides whether a file is backed up, a new file
    /// on an ignored path is still ingested into the loadout.
    /// </remarks>
    protected virtual bool IsNeverWritten(GamePath path) => false;

    private const Actions DiskAndLoadoutWrites = Actions.BackupFile | Actions.IngestFromDisk | Actions.AdaptLoadout | Actions.DeleteFromDisk | Actions.ExtractToDisk;

    private void KeepNeverWrittenPathsOffDisk(Dictionary<GamePath, SyncNode> syncTree)
    {
        List<GamePath>? cleared = null;

        foreach (var path in syncTree.Keys.ToArray())
        {
            if (!IsNeverWritten(path)) continue;

            ref var node = ref CollectionsMarshal.GetValueRefOrNullRef(syncTree, path);
            var fromLoadoutItem = node.SourceItemType is LoadoutSourceItemType.Loadout or LoadoutSourceItemType.Intrinsic;
            if (fromLoadoutItem && (node.Actions & (Actions.ExtractToDisk | Actions.WriteIntrinsic | Actions.WarnOfUnableToExtract)) != 0)
                throw new SynchronizerPreflightException(NeverWrittenMessage(path, node));

            if ((node.Actions & DiskAndLoadoutWrites) == 0) continue;

            var remaining = node.Actions & ~DiskAndLoadoutWrites;
            // The warning belongs to the extract that was just dropped.
            remaining &= ~Actions.WarnOfUnableToExtract;
            node.Actions = remaining == 0 ? Actions.DoNothing : remaining;
            (cleared ??= []).Add(path);
        }

        if (cleared is null) return;
        const int listed = 10;
        Logger.LogInformation("Leaving {Count} path(s) the synchronizer never writes as they are: {Paths}{More}",
            cleared.Count, string.Join(", ", cleared.Take(listed)), cleared.Count > listed ? $" and {cleared.Count - listed} more" : "");
    }

    /// <summary>
    /// Whether the path could address something outside its location: a "." or ".." segment, a segment that is
    /// a drive, or a rooted path. RelativePath keeps "." and ".." as written and accepts ':' inside a path, and
    /// the paths are joined onto a location folder when written, so a mod could otherwise place files anywhere
    /// the player can write.
    /// </summary>
    internal static bool IsTraversalPath(GamePath path)
    {
        var text = path.Path.ToString();
        if (text.StartsWith('/') || text.StartsWith('\\')) return true;

        foreach (var segment in text.Split('/', '\\'))
        {
            if (segment is "." or ".." || segment.Contains(':')) return true;
        }
        return false;
    }

    private void RefuseTraversalPaths(Dictionary<GamePath, SyncNode> syncTree)
    {
        foreach (var (path, node) in syncTree)
        {
            if (node.SourceItemType is not (LoadoutSourceItemType.Loadout or LoadoutSourceItemType.Intrinsic)) continue;
            if (!IsTraversalPath(path)) continue;

            throw new SynchronizerPreflightException(
                $"Cannot apply: {DescribeProvider(node)} provides '{path.Path}', whose '.', '..' or drive segment could reach outside the {path.LocationId} location. " +
                "Every sync fails until that mod is removed from the loadout.");
        }
    }

    private string DescribeProvider(SyncNode node)
    {
        var provider = "an intrinsic file of the game";
        if (node.HaveLoadout && node.Loadout.HaveEntityId)
        {
            var item = LoadoutItem.Load(Connection.Db, node.Loadout.EntityId);
            if (item.IsValid() && item.HasParent()) provider = $"the mod '{item.Parent.AsLoadoutItem().Name}'";
        }
        return provider;
    }

    private string NeverWrittenMessage(GamePath path, SyncNode node)
    {
        var provider = DescribeProvider(node);

        return $"Cannot apply: {provider} provides '{path.Path}', but files in the {path.LocationId} location are never written for this game. " +
               "Every sync fails until that mod is removed from the loadout.";
    }

    /// <summary>
    /// Whether to ignore the file at the given path when indexing, in addition to the rule every game gets.
    /// </summary>
    /// <remarks>
    /// Files ignored by this method will not be included in the sync tree. Prefer not including
    /// the path in the first place instead of using this method.
    /// </remarks>
    protected virtual IGamePathFilter GameSpecificPathFilter { get; } = Synchronizers.GamePathFilters.Empty;

    /// <summary>
    /// Gets a set of files intrinsic to this game. Such as mod order files, preference files, etc.
    /// These files will not be backed up and will not be included in the loadout directly. Instead, they are
    /// generated at sync time by calling the implementations of the files themselves. 
    /// </summary>
    public virtual Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout)
    {
        return new();
    }

    /// <inheritdoc />
    public void EnsureBuildCovered(GameInstallation installation, LocatorId[] locatorIds)
    {
        if (_fileHashService.CoversInstalledBuild((installation.LocatorResult.Store, locatorIds))) return;

        throw new SynchronizerPreflightException(
            $"{installation.Game.DisplayName}'s installed build is not in the app's game file data yet, so the app cannot tell the game's own files from added ones and will not clean the folder. Existing files are kept as External Changes.");
    }

    public async Task ResetToOriginalGameState(GameInstallation installation, LocatorId[] locatorIds)
    {
        // Same reasoning as in Synchronize: the re-index below writes before RunActions would refuse.
        await Preflight(installation);
        EnsureBuildCovered(installation, locatorIds);

        var gameState = _fileHashService.GetGameFiles((installation.LocatorResult.Store, locatorIds));
        var metadata = await ReindexState(installation);

        var diskStateEntries = DiskStateEntry.FindByGame(metadata.Db, metadata);

        List<PathPartPair> diskState = [];

        foreach (var diskFile in diskStateEntries)
        {
            diskState.Add(new PathPartPair(diskFile.Path, new SyncNodePart
            {   
                EntityId = diskFile.Id,
                Hash = diskFile.Hash,
                Size = diskFile.Size,
                LastModifiedTicks = diskFile.LastModified.UtcTicks,
            }));
        }

        Dictionary<GamePath, SyncNode> desiredState = new();

        foreach (var gameFile in gameState)
        {
            var part = new SyncNodePart
            {
                Hash = gameFile.Hash,
                Size = gameFile.Size,
                LastModifiedTicks = 0,
            };
            var syncNode = new SyncNode
            {
                Loadout = part,
                SourceItemType = LoadoutSourceItemType.Game,
            };
            desiredState.Add(gameFile.Path, syncNode);
        }

        var forgotten = AddDesiredStateOutsideGame(desiredState, metadata, diskState);
        await KeepGameInstalledContentThroughReset(installation, metadata, diskState, desiredState);

        // Has to run before MergeStates: whether the rendered file's bytes are in the file store is read while
        // the tree is processed, and a node without an archive would be mapped to a delete.
        await RenderIntrinsicFilesForReset(metadata, desiredState);

        // Merge the states into a tree. Passing in the current state as the current and previous state. 
        // This fakes the synchronizer into thinking that there are no changes on disk and we only want to do a
        // hard reset to the desired state.
        MergeStates(diskState, diskState, desiredState);
        
        // Process the tree
        ProcessSyncTree(desiredState);

        // Tracked saves the game rewrote stay on disk, but nothing may track them any more.
        foreach (var path in forgotten) CollectionsMarshal.GetValueRefOrNullRef(desiredState, path).Actions = Actions.ForgetDiskState;

        // Run the groupings
        await RunActions(desiredState, installation);
    }

    /// <summary>
    /// Fills in the desired state for every indexed file outside the <see cref="LocationId.Game"/> location.
    /// </summary>
    /// <remarks>
    /// Game files have a known vanilla state, so a reset can hard-set them. Anything else (configs,
    /// preferences) is the user's: without an entry here the reset would see "no desired file" and delete it.
    /// Only what the last synced loadout deployed is undone. A deployed file goes back to its copy from before
    /// the game was managed, or is removed if there was none. Files from the loadout's overrides, and files the
    /// loadout never touched, are kept as they are.
    /// </remarks>
    /// <returns>
    /// The tracked files on protected paths that the reset leaves alone because they no longer match what the
    /// loadout deployed (the game rewrote them). They are the player's, so they stay on disk and lose their
    /// disk state entry.
    /// </returns>
    private List<GamePath> AddDesiredStateOutsideGame(Dictionary<GamePath, SyncNode> desiredState, GameInstallMetadata.ReadOnly metadata, List<PathPartPair> diskState)
    {
        List<GamePath> forgotten = [];
        var deployed = GetFilesDeployedByLastSyncedLoadout(metadata, diskState);
        var initialState = GameInstallMetadata.InitialDiskStateTransaction.TryGetValue(metadata, out var initialTx)
            ? ((ILoadoutSynchronizer)this).GetDiskStateForGameAsOf(metadata, TxId.From(initialTx.Value))
                .ToDictionary(p => p.Path, p => p.Part)
            : new Dictionary<GamePath, SyncNodePart>();

        foreach (var (path, diskPart) in diskState)
        {
            if (path.LocationId == LocationId.Game) continue;

            if (!deployed.Contains(path))
            {
                desiredState.Add(path, new SyncNode { Loadout = diskPart, SourceItemType = LoadoutSourceItemType.Game });

                // Only a deploy puts a protected path in the disk state. One that no longer counts as deployed
                // (usually because the game rewrote it) is kept, so stop tracking it rather than leave it indexed.
                if (GamePathFilter.ShouldFilter(path)) forgotten.Add(path);
                continue;
            }

            if (!initialState.TryGetValue(path, out var initialPart))
            {
                // The loadout created this file: nothing to restore, so it has no desired state and is deleted.
                continue;
            }

            if (!HaveArchive(initialPart.Hash))
            {
                Logger.LogWarning("Initial copy of `{Path}` is missing from the file store, deleting it instead of restoring", path);
                continue;
            }

            desiredState.Add(path, new SyncNode
            {
                Loadout = new SyncNodePart { Hash = initialPart.Hash, Size = initialPart.Size, LastModifiedTicks = 0 },
                SourceItemType = LoadoutSourceItemType.Game,
            });
        }

        return forgotten;
    }

    /// <summary>
    /// The indexed files outside <see cref="LocationId.Game"/> that the last synced loadout put on disk from a
    /// mod or an intrinsic file. Files that came from the loadout's overrides were ingested from the user's own
    /// files, so they are not included.
    /// </summary>
    private HashSet<GamePath> GetFilesDeployedByLastSyncedLoadout(GameInstallMetadata.ReadOnly metadata, List<PathPartPair> diskState)
    {
        var result = new HashSet<GamePath>();
        if (!GameInstallMetadata.LastSyncedLoadout.TryGetValue(metadata, out var lastSynced)) return result;

        var loadout = Loadout.Load(metadata.Db, lastSynced);
        if (!loadout.IsValid()) return result;

        var overridesGroup = LoadoutOverridesGroup.FindByOverridesFor(loadout.Db, loadout.Id).TryGetFirst(out var found)
            ? found.Id
            : default(EntityId);

        // The tree is only read for what the loadout provides, so the disk state doubles as the previous state.
        foreach (var (path, node) in BuildSyncTree(diskState, diskState, loadout))
        {
            if (path.LocationId == LocationId.Game || !node.HaveDisk) continue;

            var isDeployed = node.SourceItemType switch
            {
                LoadoutSourceItemType.Intrinsic => true,
                // A save on a protected path that the game rewrote is no longer what the loadout deployed.
                LoadoutSourceItemType.Loadout => node.Loadout.HaveEntityId
                    && !IsInGroup(loadout.Db, node.Loadout.EntityId, overridesGroup)
                    && !(GamePathFilter.ShouldFilter(path) && node.Disk.Hash != node.Loadout.Hash),
                _ => false,
            };

            if (isDeployed) result.Add(path);
        }

        return result;
    }

    private static bool IsInGroup(IDb db, EntityId itemId, EntityId groupId)
    {
        var item = LoadoutItem.Load(db, itemId);
        return item.IsValid() && item.HasParent() && item.ParentId.Value == groupId;
    }
}

#endregion
