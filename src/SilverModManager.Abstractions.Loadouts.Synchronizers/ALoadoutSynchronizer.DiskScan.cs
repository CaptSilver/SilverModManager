using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using CommunityToolkit.HighPerformance.Buffers;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.GC;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Exceptions;
using SilverModManager.Abstractions.Loadouts.Files.Diff;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Sdk.GeneratedCaches;
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
using SilverModManager.Sdk.ForeignDeployments;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using Reloaded.Memory.Extensions;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

using DiskState = Entities<DiskStateEntry.ReadOnly>;

public partial class ALoadoutSynchronizer
{
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
        var identityAttrId = db.AttributeCache.GetAttributeId(DiskStateEntry.Identity.Id);
        var hashAttrId = db.AttributeCache.GetAttributeId(DiskStateEntry.XxHash3.Id);
        var sizeAttrId = db.AttributeCache.GetAttributeId(DiskStateEntry.Size.Id);
        var lastModifiedAttrId = db.AttributeCache.GetAttributeId(DiskStateEntry.LastModified.Id);
        
        // We start with a single reference iterator, that points to the game data we are trying to access
        // Since this data will return results sorted by E (entry Id) we can merge join to any other data 
        // that is sorted in the same order
        using var iterator = db.LightweightDatoms(SliceDescriptor.Create(mainAttrId, metadata));
        
        // Now we have iterators for each field to load
        using var pathIterator = db.LightweightDatoms(SliceDescriptor.Create(pathAttrId));
        // A row written before identities carried their kind has only the xxHash3, and a row of another kind has only
        // the identity, so both are read and merged by entity. An xxHash3 row has both and yields one part.
        using var identityIterator = db.LightweightDatoms(SliceDescriptor.Create(identityAttrId));
        using var hashIterator = db.LightweightDatoms(SliceDescriptor.Create(hashAttrId));
        using var sizeIterator = db.LightweightDatoms(SliceDescriptor.Create(sizeAttrId));
        using var lastModifiedIterator = db.LightweightDatoms(SliceDescriptor.Create(lastModifiedAttrId));
        
        // For each entry in the main iterator
        while (iterator.MoveNext())
        {
            // Fast-forward the other iterators to the same entry. One that lands past it means the entry lacks that
            // attribute, and reading on would lend it the next entry's value.
            var entry = iterator.KeyPrefix.E;
            var hasIdentity = identityIterator.FastForwardTo(entry);
            var hasHash = hashIterator.FastForwardTo(entry);
            if (!pathIterator.FastForwardTo(entry) || !(hasIdentity || hasHash)
                || !sizeIterator.FastForwardTo(entry) || !lastModifiedIterator.FastForwardTo(entry))
            {
                Logger.LogWarning("Skipping disk state entry {Entry} of {Installation}: it is missing its path, hash, size or time", entry, metadata.Id);
                continue;
            }
            
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
                    Identity = hasIdentity
                        ? FileIdentity.Read(identityIterator.ValueSpan)
                        : FileIdentity.XxHash3(MemoryMarshal.Read<Hash>(hashIterator.ValueSpan)),
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
                    Identity = entry.IdentityOf(),
                    Size = entry.Size,
                    LastModifiedTicks = entry.LastModified.UtcTicks,
                },
            };
        }
    }

    public async Task<GameInstallMetadata.ReadOnly> RescanFiles(GameInstallation gameInstallation, CancellationToken cancellationToken = default)
    {
        // Make sure the file hashes are up to date
        await _fileHashService.GetFileHashesDb();
        return await ReindexState(gameInstallation, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Reindex the state of the game. New and changed files are committed as the index settles them, a batch at a
    /// time, with <see cref="Sdk.Games.GameInstallMetadata.LastScannedDiskStateTransaction"/> moved on each time. The
    /// initial disk state is set only with the last commit, so a run that dies midway looks like no run to the
    /// next one, which skips the committed rows as unchanged and hashes the rest.
    /// </summary>
    public async Task<GameInstallMetadata.ReadOnly> ReindexState(GameInstallation installation, SynchronizeLoadoutJob? job = null, CancellationToken cancellationToken = default)
    {
        ThrowIfMounted(installation);
        using var _ = await _lock.LockAsync();
        return await ReindexStateCore(installation, job, cancellationToken);
    }

    // A scan through a live mount reads served mod files as files somebody put there.
    private void ThrowIfMounted(GameInstallation installation)
    {
        if (!_gameRegistry.TryGetMetadata(installation, out var metadata)) return;
        StaleMountGuard.ThrowIfMounted(_backends, _planStore.Load(Connection.Db, metadata.GameInstallMetadataId), installation);
    }

    /// <summary><see cref="ReindexState"/> for a caller that already holds the lock.</summary>
    private async Task<GameInstallMetadata.ReadOnly> ReindexStateCore(GameInstallation installation, SynchronizeLoadoutJob? job, CancellationToken cancellationToken)
    {
        var metadata = _gameRegistry.ForceGetMetadata(installation);
        var previousState = GetDiskState(metadata);

        var indexGameResult = await _gameLocationsService.IndexGame(
            installation: installation,
            previousDiskState: previousState,
            filter: Synchronizers.GamePathFilters.Create(path => GamePathFilter.ShouldFilter(path) && !IsTrackedProtectedFile(previousState, path)),
            cancellationToken: cancellationToken,
            progress: job is null ? null : (done, total) => job.ReportProgress(SyncPhases.Collecting, done, total),
            onBatch: async batch =>
            {
                if (batch.Count == 0) return;
                using var batchTx = Connection.BeginTransaction();
                WriteIndexedFiles(batchTx, metadata, previousState, batch);
                batchTx.Add(metadata.Id, Sdk.Games.GameInstallMetadata.LastScannedDiskStateTransaction, EntityId.From(TxId.Tmp.Value));
                await batchTx.Commit();
            },
            preferred: PreferredCopy(installation, metadata));

        using var tx = Connection.BeginTransaction();
        var changed = indexGameResult.RemovedFiles.Count != 0;
        foreach (var gamePath in indexGameResult.RemovedFiles)
        {
            var didFind = previousState.TryGetValue(gamePath, out var previousDiskStateEntry);
            Debug.Assert(didFind, "removed file should be in previous state");

            tx.Delete(previousDiskStateEntry.Id, recursive: false);
        }

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

        // A sync would put these on its delete path too, but it would also deploy the loadout. Only Overrides rows
        // are dropped here: a Game row has no entity of its own. Needing a previous
        // entry keeps a file that was never on disk, such as one still waiting to be deployed, in the list.
        var gone = BuildSyncTreeFromIndex(loadout, metadata).Values
            .Where(node => node.PlayerDeleted && node.Loadout.HaveEntityId)
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
    /// Sets an existing row's identity and keeps the <see cref="DiskStateEntry.XxHash3"/> mirror true to it: present
    /// and equal for an xxHash3 identity, absent for any other kind.
    /// </summary>
    public static void WriteIdentity(ITransaction tx, DiskStateEntry.ReadOnly entry, FileIdentity identity)
    {
        tx.Add(entry.Id, DiskStateEntry.Identity, identity);
        if (identity.TryXxHash3(out var xxHash3))
            tx.Add(entry.Id, DiskStateEntry.XxHash3, xxHash3);
        else if (DiskStateEntry.XxHash3.TryGetValue(entry, out var stale))
            tx.Retract(entry.Id, DiskStateEntry.XxHash3, stale);
    }

    /// <summary>
    /// Writes the rows for one batch of indexed files: a file with a previous entry updates it, any other is added.
    /// </summary>
    private static void WriteIndexedFiles(ITransaction tx, GameInstallMetadata.ReadOnly metadata, FrozenDictionary<GamePath, DiskStateEntry.ReadOnly> previousState, IReadOnlyList<IndexFileResult> batch)
    {
        foreach (var result in batch)
        {
            if (previousState.TryGetValue(result.Path, out var previousDiskStateEntry))
            {
                tx.Add(previousDiskStateEntry.Id, DiskStateEntry.Size, result.Size);
                WriteIdentity(tx, previousDiskStateEntry, result.Identity);
                tx.Add(previousDiskStateEntry.Id, DiskStateEntry.LastModified, result.LastModified);
                continue;
            }

            _ = new DiskStateEntry.New(tx, tx.TempId(DiskStateEntry.EntryPartition))
            {
                Path = result.Path.ToGamePathParentTuple(metadata.Id),
                Identity = result.Identity,
                XxHash3 = result.Identity.TryXxHash3(out var newHash) ? newHash : null,
                Size = result.Size,
                LastModified = result.LastModified,
                GameId = metadata.Id,
            };
        }
    }
}
