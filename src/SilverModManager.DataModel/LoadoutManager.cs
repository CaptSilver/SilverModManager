using System.Collections.Frozen;
using SilverModManager.Sdk.Hashes;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DynamicData.Kernel;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.GC;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Exceptions;
using SilverModManager.Abstractions.Loadouts.Sorting;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.DataModel.Sorting;
using SilverModManager.DataModel.Synchronizer;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.DatomIterators;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.Internals;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.MnemonicDB.Abstractions.ValueSerializers;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel;

internal partial class LoadoutManager : ILoadoutManager
{
    private readonly ILogger _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IJobMonitor _jobMonitor;
    private readonly IFileHashesService _fileHashesService;
    private readonly IConnection _connection;
    private readonly ISynchronizerService _synchronizerService;
    private readonly IGarbageCollectorRunner _garbageCollectorRunner;
    private readonly IGameRegistry _gameRegistry;
    private readonly IFileStore _fileStore;

    // Nothing in the database makes a folder's row unique, so the check and the create have to happen as one
    // step: two callers that both miss would each commit a row for the same install.
    private readonly SemaphoreSlim _manageInstallationLock = new(1, 1);

    public LoadoutManager(IServiceProvider serviceProvider)
    {
        _logger = serviceProvider.GetRequiredService<ILogger<LoadoutManager>>();
        _serviceProvider = serviceProvider;
        _jobMonitor = serviceProvider.GetRequiredService<IJobMonitor>();
        _fileHashesService = serviceProvider.GetRequiredService<IFileHashesService>();
        _connection = serviceProvider.GetRequiredService<IConnection>();
        _synchronizerService = serviceProvider.GetRequiredService<ISynchronizerService>();
        _garbageCollectorRunner = serviceProvider.GetRequiredService<IGarbageCollectorRunner>();
        _gameRegistry = serviceProvider.GetRequiredService<IGameRegistry>();
        _fileStore = serviceProvider.GetRequiredService<IFileStore>();
    }

    public async ValueTask<GameInstallMetadata.ReadOnly> ManageInstallation(GameInstallation installation)
    {
        if (_gameRegistry.TryGetMetadata(installation, out var existing)) return existing;

        await _manageInstallationLock.WaitAsync();
        try
        {
            if (_gameRegistry.TryGetMetadata(installation, out var metadata)) return metadata;

            using var metadataTx = _connection.BeginTransaction();
            var newMetadata = new GameInstallMetadata.New(metadataTx)
            {
                Store = installation.LocatorResult.Store,
                Path = installation.Locations[LocationId.Game].Path.ToString(),
                Game = installation.Game.GameId,
                Name = installation.Game.DisplayName,
            };

            // A game without a Nexus id simply has none stored.
            if (installation.Game.NexusModsGameId.HasValue) newMetadata.GameId = installation.Game.NexusModsGameId.Value;

            var metadataResult = await metadataTx.Commit();
            return metadataResult.Remap(newMetadata);
        }
        finally
        {
            _manageInstallationLock.Release();
        }
    }

    public IJobTask<CreateLoadoutJob, Loadout.ReadOnly> CreateLoadout(GameInstallation installation, string? suggestedName = null, BackupOverflowChoice? largeFiles = null)
    {
        return _jobMonitor.Begin(new CreateLoadoutJob(installation, largeFiles), async ctx =>
        {
            // Refuse before any row exists: a half-added game (metadata and loadout, nothing synced) is worse
            // than one that was never added.
            await installation.GetGame().Synchronizer.Preflight(installation);

            // The first synchronize reads the vanilla file list, so it has to exist before the loadout does.
            await _fileHashesService.EnsureLocalBaseline(installation, ctx.CancellationToken);

            // Prime the hash database to make sure it's loaded
            await _fileHashesService.GetFileHashesDb();

            var metadata = await ManageInstallation(installation);
            var shortName = GetNewShortName(_connection.Db, metadata);

            using var tx = _connection.BeginTransaction();

            List<LocatorId> locatorIds = [];
            {
                var metadataLocatorIds = installation.LocatorResult.LocatorIds;
                var distinctLocatorIds = metadataLocatorIds.Distinct().ToArray();

                if (distinctLocatorIds.Length != metadataLocatorIds.Length)
                {
                    _logger.LogWarning("Duplicate locator ids `{LocatorIds}` found in LocatorResultMetadata for {Game} when creating new loadout", metadataLocatorIds, installation.Game.DisplayName);
                }

                locatorIds.AddRange(distinctLocatorIds);
            }

            if (!_fileHashesService.TryGetVanityVersion((installation.LocatorResult.Store, locatorIds.ToArray()), out var version))
                _logger.LogWarning("Unable to find game version for {Game}", installation.Game.DisplayName);

            var loadout = new Loadout.New(tx)
            {
                Name = EnsureUniqueName(_connection.Db, metadata, suggestedName ?? "Loadout " + shortName, self: null),
                ShortName = shortName,
                InstallationId = metadata,
                Revision = 0,
                LoadoutKind = LoadoutKind.Default,
                LocatorIds = locatorIds,
                GameVersion = version,
            };

            // Create the user's default collection
            _ = DefaultCollection.Create(tx, loadout);

            var result = await tx.Commit();
            var remappedLoadout = result.Remap(loadout);

            // If there is no currently synced loadout, then we can ingest the game folder
            if (!GameInstallMetadata.LastSyncedLoadout.TryGetValue(remappedLoadout.Installation, out var lastSyncedLoadoutId))
            {
                await FirstSync(remappedLoadout, largeFiles, ctx.CancellationToken);
                remappedLoadout = remappedLoadout.Rebase();
            }
            else
            {
                // check if the last synced loadout is valid (can apparently happen if the user unmanaged the game and manages it again)
                var lastSyncedLoadout = Loadout.Load(remappedLoadout.Db, lastSyncedLoadoutId);
                if (!lastSyncedLoadout.IsValid())
                {
                    await FirstSync(remappedLoadout, largeFiles, ctx.CancellationToken);
                    remappedLoadout = remappedLoadout.Rebase();
                }
            }

            return remappedLoadout;
        });
    }

    /// <summary>
    /// The sync that ingests the game folder into a loadout that was just created. When it is refused or cancelled the
    /// loadout and the index it left behind are removed again: a loadout with nothing synced is a half-added game, and
    /// the next attempt starts from a stat-only index anyway.
    /// </summary>
    private async Task FirstSync(Loadout.ReadOnly loadout, BackupOverflowChoice? largeFiles, CancellationToken cancellationToken)
    {
        try
        {
            using var answer = BackupOverflowAnswer.Begin(BackupOverflowOperation.Manage);
            using var origin = SyncOrigin.Begin(SyncTrigger.ManageGame);
            await _synchronizerService.Synchronize(loadout.LoadoutId, largeFiles, cancellationToken);
        }
        catch (Exception e) when (e is BackupOverflowException or SynchronizerPreflightException or ExecutableInUseException or OperationCanceledException)
        {
            try
            {
                await DeleteLoadoutRows(loadout.LoadoutId, GarbageCollectorRunMode.DoNotRun, deactivateIfActive: false, CancellationToken.None);

                var metadata = GameInstallMetadata.Load(_connection.Db, loadout.InstallationId);
                using var tx = _connection.BeginTransaction();
                ClearInstallState(tx, metadata);
                await tx.Commit();
            }
            catch (Exception cleanup)
            {
                _logger.LogError(cleanup, "Could not remove loadout {Loadout} after its first sync was refused", loadout.Name);
            }

            throw;
        }
    }

    private static string GetNewShortName(IDb db, GameInstallMetadataId installationId)
    {
        var existingShortNames = Loadout.All(db)
            .Where(l => l.IsVisible() && l.InstallationId == installationId)
            .Select(l => l.ShortName)
            .ToArray();

        var result = LoadoutNameProvider.GetNewShortName(existingShortNames);
        return result;
    }

    public async ValueTask<Loadout.ReadOnly> CopyLoadout(LoadoutId loadoutId, CancellationToken cancellationToken = default)
    {
        var baseDb = _connection.Db;
        var loadout = Loadout.Load(baseDb, loadoutId);

        // Temp space for datom values
        Memory<byte> buffer = GC.AllocateUninitializedArray<byte>(length: 32);

        // Cache some attribute ids
        var cache = baseDb.AttributeCache;
        var nameId = cache.GetAttributeId(Loadout.Name.Id);
        var shortNameId = cache.GetAttributeId(Loadout.ShortName.Id);

        // Create a mapping of old entity ids to new (temp) entity ids
        Dictionary<EntityId, EntityId> entityIdList = new();
        var remapFn = RemapFn;

        using var tx = _connection.BeginTransaction();

        // Add the loadout
        var newLoadoutId = tx.TempId();
        entityIdList[loadout.Id] = newLoadoutId;

        // And each item
        foreach (var item in LoadoutItem.FindByLoadout(loadout.Db, loadout))
        {
            entityIdList[item.Id] = tx.TempId();
        }

        // Priority rows aren't LoadoutItems; without them the copy has no conflict order at all.
        foreach (var priority in LoadoutItemGroupPriority.FindByLoadout(loadout.Db, loadout))
        {
            entityIdList[priority.Id] = tx.TempId();
        }

        // Rules and winner pins belong to the loadout the same way; the remap points their group references
        // and the loadout part of a pin's path at the copy.
        foreach (var rule in LoadoutGroupRule.FindByLoadout(loadout.Db, loadout))
        {
            entityIdList[rule.Id] = tx.TempId();
        }

        foreach (var pin in LoadoutFileWinner.FindByLoadout(loadout.Db, loadout))
        {
            entityIdList[pin.Id] = tx.TempId();
        }

        // The INI baselines go with the copy, so it starts from the same game settings.
        foreach (var iniState in LoadoutIniState.FindByLoadout(loadout.Db, loadout))
        {
            entityIdList[iniState.Id] = tx.TempId();
        }

        // So do the order entries an import dropped; the remap points their container at the copy's group.
        foreach (var dropped in ModOrderDroppedEntry.FindByLoadout(loadout.Db, loadout))
        {
            entityIdList[dropped.Id] = tx.TempId();
        }

        foreach (var (oldId, newId) in entityIdList)
        {
            // Get the original entity
            var entity = baseDb.Get(oldId);

            foreach (var datom in entity)
            {
                if (datom.A == nameId || datom.A == shortNameId) continue;

                if (buffer.Length < datom.ValueSpan.Length)
                    buffer = GC.AllocateUninitializedArray<byte>(datom.ValueSpan.Length);

                datom.ValueSpan.CopyTo(buffer.Span);

                var prefix = new KeyPrefix(newId, datom.A, TxId.Tmp, isRetract: false, datom.Prefix.ValueTag);
                var newDatom = new Datom(prefix, buffer[..datom.ValueSpan.Length]);

                datom.Prefix.ValueTag.Remap(buffer[..datom.ValueSpan.Length].Span, remapFn);

                tx.Add(newDatom);
            }
        }

        // NOTE(erri120): using latest DB to prevent duplicate short names
        var newShortName = GetNewShortName(_connection.Db, loadout.InstallationId);
        var newName = EnsureUniqueName(_connection.Db, loadout.InstallationId, "Loadout " + newShortName, self: null);

        tx.Add(newLoadoutId, Loadout.Name, newName);
        tx.Add(newLoadoutId, Loadout.ShortName, newShortName);

        var result = await tx.Commit();
        var newLoadout = Loadout.Load(result.Db, result[newLoadoutId]);
        return newLoadout;

        // Local function to remap entity ids in the format Attribute.Remap wants
        EntityId RemapFn(EntityId entityId)
        {
            return entityIdList.GetValueOrDefault(entityId, entityId);
        }
    }

    public async ValueTask<Loadout.ReadOnly> RenameLoadout(LoadoutId loadoutId, string newName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);

        var loadout = Loadout.Load(_connection.Db, loadoutId);
        if (!loadout.IsValid()) throw new ArgumentException($"Entity `{loadoutId}` is not a loadout", nameof(loadoutId));

        var uniqueName = EnsureUniqueName(_connection.Db, loadout.InstallationId, newName, self: loadoutId);
        if (uniqueName == loadout.Name) return loadout;

        using var tx = _connection.BeginTransaction();
        tx.Add(loadoutId, Loadout.Name, uniqueName);
        var result = await tx.Commit();
        return Loadout.Load(result.Db, loadoutId);
    }

    /// <summary>
    /// Returns <paramref name="candidate"/> trimmed, with " (2)", " (3)", ... appended until no other visible loadout
    /// of the installation has the same name (compared ignoring case). Existing duplicates are left alone.
    /// </summary>
    private static string EnsureUniqueName(IDb db, GameInstallMetadataId installationId, string candidate, LoadoutId? self)
    {
        var trimmed = candidate.Trim();
        var taken = Loadout.All(db)
            .Where(l => l.IsVisible() && l.InstallationId == installationId && l.LoadoutId != self)
            .Select(l => l.Name.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = trimmed;
        for (var i = 2; taken.Contains(result); i++)
        {
            result = $"{trimmed} ({i})";
        }

        return result;
    }

    public async ValueTask DeleteLoadout(LoadoutId loadoutId, GarbageCollectorRunMode gcRunMode = GarbageCollectorRunMode.DoNotRun, bool deactivateIfActive = true, CancellationToken cancellationToken = default)
    {
        RunningGameGuard.ThrowIfRunning(_serviceProvider, Loadout.Load(_connection.Db, loadoutId));
        await DeleteLoadoutRows(loadoutId, gcRunMode, deactivateIfActive, cancellationToken);
    }

    /// <summary>
    /// <see cref="DeleteLoadout"/> without the running-game check, for removing a loadout that never wrote to the
    /// game folder: the check protects the folder, and a first sync refused because the game is running is the
    /// case that has to clean up while it is still running.
    /// </summary>
    private async ValueTask DeleteLoadoutRows(LoadoutId loadoutId, GarbageCollectorRunMode gcRunMode, bool deactivateIfActive, CancellationToken cancellationToken)
    {
        // Refuse before the loadout is hidden: a refusal later on would leave it hidden but not deleted.
        if (deactivateIfActive)
        {
            var toDelete = Loadout.Load(_connection.Db, loadoutId);
            var deleteMetadata = GameInstallMetadata.Load(_connection.Db, toDelete.InstallationId);
            if (GameInstallMetadata.LastSyncedLoadout.TryGetValue(deleteMetadata, out var activeId) && activeId == loadoutId.Value)
                EnsureBuildCovered(toDelete.InstallationInstance);
        }

        {
            using var tx1 = _connection.BeginTransaction();
            tx1.Add(loadoutId, Loadout.LoadoutKind, LoadoutKind.Deleted);
            await tx1.Commit();
        }

        var loadout = Loadout.Load(_connection.Db, loadoutId);
        Debug.Assert(!loadout.IsVisible(), "loadout shouldn't be visible anymore");

        var metadata = GameInstallMetadata.Load(_connection.Db, loadout.InstallationId);
        if (deactivateIfActive && GameInstallMetadata.LastSyncedLoadout.TryGetValue(metadata, out var lastAppliedLoadout) && lastAppliedLoadout == loadoutId.Value)
        {
            using var origin = SyncOrigin.Begin(SyncTrigger.DeleteLoadout);
            await DeactivateCurrentLoadout(loadout.InstallationInstance, cancellationToken: cancellationToken);
        }

        using var tx = _connection.BeginTransaction();

        tx.Delete(loadoutId, recursive: false);
        foreach (var item in LoadoutItem.FindByLoadout(loadout.Db, loadout))
        {
            tx.Delete(item.Id, recursive: false);
        }

        foreach (var priorityEntity in LoadoutItemGroupPriority.FindByLoadout(loadout.Db, loadout))
        {
            tx.Delete(priorityEntity, recursive: false);
        }

        foreach (var rule in LoadoutGroupRule.FindByLoadout(loadout.Db, loadout))
        {
            tx.Delete(rule, recursive: false);
        }

        foreach (var pin in LoadoutFileWinner.FindByLoadout(loadout.Db, loadout))
        {
            tx.Delete(pin, recursive: false);
        }

        foreach (var iniState in LoadoutIniState.FindByLoadout(loadout.Db, loadout))
        {
            tx.Delete(iniState, recursive: false);
        }

        foreach (var dropped in ModOrderDroppedEntry.FindByLoadout(loadout.Db, loadout))
        {
            tx.Delete(dropped, recursive: false);
        }

        // A claim is its own entity rather than a loadout item, so the loop above misses it.
        foreach (var claim in GameInstalledClaim.FindByLoadout(loadout.Db, loadout))
        {
            tx.Delete(claim, recursive: false);
        }

        await tx.Commit();
        
        // Execute the garbage collector
        await _garbageCollectorRunner.RunWithMode(gcRunMode);
    }

    public async ValueTask ActivateLoadout(LoadoutId loadoutId, CancellationToken cancellationToken = default, BackupOverflowChoice? largeFiles = null)
    {
        var loadout = Loadout.Load(_connection.Db, loadoutId);
        var synchronizer = loadout.InstallationInstance.GetGame().Synchronizer;

        using var answer = BackupOverflowAnswer.Begin(BackupOverflowOperation.Switch);
        var state = await synchronizer.ReindexState(loadout.InstallationInstance);
        await synchronizer.BuildProcessRun(loadout, state, cancellationToken, largeFiles);
    }

    public async ValueTask DeactivateCurrentLoadout(GameInstallation installation, CancellationToken cancellationToken = default, BackupOverflowChoice? largeFiles = null, bool discardBackups = false)
    {
        var metadata = _gameRegistry.ForceGetMetadata(installation);
        var synchronizer = installation.GetGame().Synchronizer;

        if (!metadata.Contains(GameInstallMetadata.LastSyncedLoadout)) return;

        // Before the synchronize below, which writes: a refused reset should leave everything as it was.
        EnsureBuildCovered(installation);

        // The sync and the reset are one operation to the player, so one answer covers both.
        using var answer = BackupOverflowAnswer.Begin(BackupOverflowOperation.Reset);

        // Synchronize the last applied loadout, so we don't lose any changes. A caller that is about to delete the
        // loadouts has no use for them.
        if (!discardBackups)
            await synchronizer.Synchronize(Loadout.Load(_connection.Db, metadata.LastSyncedLoadout), largeFiles: largeFiles, cancellationToken: cancellationToken);

        var metadataLocatorIds = installation.LocatorResult.LocatorIds;
        var locatorIds = metadataLocatorIds.Distinct().ToArray();
        
        if (locatorIds.Length != metadataLocatorIds.Length)
        {
            _logger.LogWarning("Duplicate locator ids `{LocatorIds}` found in LocatorResultMetadata for {Game} when deactivating loadout", metadataLocatorIds, installation.Game.DisplayName);
        }

        await synchronizer.ResetToOriginalGameState(installation, locatorIds, largeFiles, cancellationToken, discardBackups);
    }

    private static void EnsureBuildCovered(GameInstallation installation)
    {
        installation.GetGame().Synchronizer.EnsureBuildCovered(installation, installation.LocatorResult.LocatorIds.Distinct().ToArray());
    }

    public async Task CleanGameFolder(Loadout.ReadOnly loadout, BackupOverflowChoice? largeFiles = null, CancellationToken cancellationToken = default)
    {
        EnsureBuildCovered(loadout.InstallationInstance);

        // Through the service so the clean waits its turn behind other syncs and the question holds the install.
        await _synchronizerService.CleanGameFolder(loadout.LoadoutId, largeFiles, cancellationToken);
    }

    public async Task<UnbackedFiles> CountUnbackedFiles(GameInstallation installation, CancellationToken cancellationToken = default)
    {
        if (!_gameRegistry.TryGetMetadata(installation, out var metadata)) return default;

        var db = _connection.Db;
        var sizeByHash = new Dictionary<Hash, Size>();
        foreach (var loadout in Loadout.FindByInstallation(db, metadata).Where(l => l.IsVisible()))
        {
            foreach (var file in LoadoutItem.FindByLoadout(db, loadout).OfTypeLoadoutItemWithTargetPath().OfTypeLoadoutFile())
            {
                sizeByHash.TryAdd(file.Hash, file.Size);
            }
        }

        var count = 0;
        var total = Size.From(0);
        foreach (var (hash, size) in sizeByHash)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await _fileStore.HaveFile(FileIdentity.XxHash3(hash))) continue;
            count++;
            total += size;
        }

        return new UnbackedFiles(count, total);
    }

    public Task RescanGameFolder(Loadout.ReadOnly loadout)
        => loadout.InstallationInstance.GetGame().Synchronizer.RescanExternalChanges(loadout);

    public Optional<LoadoutId> GetCurrentlyActiveLoadout(GameInstallation installation)
    {
        var metadata = _gameRegistry.ForceGetMetadata(installation);
        if (!GameInstallMetadata.LastSyncedLoadout.TryGetValue(metadata, out var lastAppliedLoadout))
            return Optional<LoadoutId>.None;
        return LoadoutId.From(lastAppliedLoadout);
    }

    public IJobTask<UnmanageGameJob, GameInstallation> UnManage(GameInstallation installation, bool runGc = true, bool cleanGameFolder = true, CancellationToken cancellationToken = default, BackupOverflowChoice? largeFiles = null)
    {
        return _jobMonitor.Begin(new UnmanageGameJob(installation), async ctx =>
        {
            RunningGameGuard.ThrowIfRunning(_serviceProvider, installation);

            var metadata = _gameRegistry.ForceGetMetadata(installation);

            // Every loadout is deleted below, so the reset drops its copies instead of making them.
            if (GetCurrentlyActiveLoadout(installation).HasValue && cleanGameFolder) await DeactivateCurrentLoadout(installation, cancellationToken: cancellationToken, largeFiles: largeFiles, discardBackups: true);
            await ctx.YieldAsync();

            {
                using var tx1 = _connection.BeginTransaction();
                foreach (var loadout in Loadout.FindByInstallation(metadata.Db, metadata))
                {
                    tx1.Add(loadout.Id, Loadout.LoadoutKind, LoadoutKind.Deleted);
                }

                var result = await tx1.Commit();

                metadata = GameInstallMetadata.Load(result.Db, metadata.Id);
                Debug.Assert(Loadout.FindByInstallation(metadata.Db, metadata).All(x => !x.IsVisible()), "all loadouts shouldn't be visible anymore");
            }

            foreach (var loadout in Loadout.FindByInstallation(metadata.Db, metadata))
            {
                _logger.LogInformation("Deleting loadout {Loadout} - {ShortName}", loadout.Name, loadout.ShortName);
                await ctx.YieldAsync();
                await DeleteLoadout(loadout, gcRunMode: GarbageCollectorRunMode.DoNotRun, deactivateIfActive: cleanGameFolder, cancellationToken: cancellationToken);
            }

            // Retract all `GameBakedUpFile` entries to allow for game file backups to be cleaned up from the FileStore
            using var tx = _connection.BeginTransaction();
            foreach (var file in GameBackedUpFile.All(_connection.Db))
            {
                if (file.GameInstallId.Value == metadata)
                    tx.Delete(file, recursive: false);
            }

            metadata = metadata.Rebase();

            // The game's metadata outlives UnManage, so rows left here would hand a game that is managed again
            // the backend decision of the install that was just removed.
            foreach (var entry in DeploymentPlanEntry.FindByInstallation(metadata.Db, metadata))
            {
                tx.Delete(entry, recursive: false);
            }

            ClearInstallState(tx, metadata);
            await tx.Commit();

            if (runGc) _garbageCollectorRunner.Run();
            return installation;
        });
    }

    /// <summary>
    /// Deletes the install's disk state and forgets which loadout was applied and which scans were taken.
    /// </summary>
    /// <remarks>
    /// Decided on the database the transaction lands on, not the row the caller read: a rescan can commit in
    /// between, adding entries and moving the scanned-state id on. Retracting the values the caller saw would
    /// then refuse the whole transaction after the loadouts were already deleted.
    /// </remarks>
    internal static void ClearInstallState(ITransaction tx, GameInstallMetadata.ReadOnly metadata)
        => tx.Add(metadata.Id, static (tx, db, id) =>
        {
            foreach (var entry in DiskStateEntry.FindByGame(db, id))
                tx.Delete(entry, recursive: false);

            tx.RetractCurrent(id, GameInstallMetadata.LastSyncedLoadout);
            tx.RetractCurrent(id, GameInstallMetadata.LastSyncedLoadoutTransaction);
            tx.RetractCurrent(id, GameInstallMetadata.InitialDiskStateTransaction);
            tx.RetractCurrent(id, GameInstallMetadata.LastScannedDiskStateTransaction);
            tx.RetractCurrent(id, GameInstallMetadata.LastApplyLeftFilesUnextracted);
        });

    public async Task<LoadoutItemGroup.ReadOnly> InstallItemWrapper(LoadoutId targetLoadout, Func<ITransaction, Task<LoadoutItemGroupId>> func)
    {
        using var tx = _connection.BeginTransaction();

        var groupId = await func(tx);
        tx.Add(new ApplyGroupRulesTxFunc(targetLoadout, new RuleDelta { NewGroups = [groupId] }));

        var result = await tx.Commit();
        return LoadoutItemGroup.Load(result.Db, groupId);
    }

    public IJobTask<IInstallLoadoutItemJob, InstallLoadoutItemJobResult> InstallItem(
        LibraryItem.ReadOnly libraryItem,
        LoadoutId targetLoadout,
        Optional<LoadoutItemGroupId> parent = default,
        ILibraryItemInstaller? installer = null,
        ILibraryItemInstaller? fallbackInstaller = null)
    {
        return InstallItem(libraryItem, targetLoadout, inputTx: null, newGroups: null, parent: parent, installer: installer, fallbackInstaller: fallbackInstaller);
    }

    /// <param name="inputTx">A transaction the caller owns and commits, or null to use one of its own.</param>
    /// <param name="newGroups">
    /// With an <paramref name="inputTx"/>, the caller builds the one <see cref="ApplyGroupRulesTxFunc"/> for the
    /// loadout (it may also be removing groups, and a second function would write the same rows again). The new
    /// group's id is added to this list and no function is queued here. Null queues the function here.
    /// </param>
    private IJobTask<IInstallLoadoutItemJob, InstallLoadoutItemJobResult> InstallItem(
        LibraryItem.ReadOnly libraryItem,
        LoadoutId targetLoadout,
        ITransaction? inputTx,
        List<LoadoutItemGroupId>? newGroups,
        Optional<LoadoutItemGroupId> parent = default,
        ILibraryItemInstaller? installer = null,
        ILibraryItemInstaller? fallbackInstaller = null)
    {
        var mainTransaction = inputTx is null ? _connection.BeginTransaction() : null;
        var tx = inputTx ?? mainTransaction!;

        var job = InstallLoadoutItemJob.Create(_serviceProvider, libraryItem, targetLoadout, tx, groupId: parent, installer: installer, fallbackInstaller: fallbackInstaller);
        return _jobMonitor.Begin(job, async context =>
        {
            var result = await job.StartAsync(context);
            var targetId = result.GroupTxId;
            if (newGroups is null) tx.Add(new ApplyGroupRulesTxFunc(targetLoadout, new RuleDelta { NewGroups = [targetId] }));
            else newGroups.Add(targetId);

            if (mainTransaction is null) return new InstallLoadoutItemJobResult(null, targetId);

            var commitResult = await mainTransaction.Commit();
            var remapped = commitResult[targetId];
            mainTransaction.Dispose();

            return new InstallLoadoutItemJobResult(LoadoutItemGroup.Load(commitResult.Db, remapped), LoadoutItemGroupId.From(0));
        });
    }

    public async ValueTask RemoveItems(LoadoutItemGroupId[] groupIds)
    {
        using var tx = _connection.BeginTransaction();
        RemoveItems(tx, groupIds);
        await tx.Commit();
    }

    private void RemoveItems(ITransaction tx, LoadoutItemGroupId[] groupIds)
    {
        foreach (var (loadoutId, removed) in DeleteGroups(tx, groupIds))
        {
            tx.Add(new ApplyGroupRulesTxFunc(loadoutId, new RuleDelta { RemovedGroups = [..removed] }));
        }
    }

    /// <summary>
    /// Deletes the groups with their priority rows, rules and pins, and returns what it deleted per loadout. The
    /// caller adds the <see cref="ApplyGroupRulesTxFunc"/> that closes the gaps they leave in the order.
    /// </summary>
    private Dictionary<LoadoutId, List<LoadoutItemGroupId>> DeleteGroups(ITransaction tx, LoadoutItemGroupId[] groupIds)
    {
        var db = _connection.Db;
        var loadouts = new Dictionary<LoadoutId, List<LoadoutItemGroupId>>();

        foreach (var groupId in groupIds)
        {
            var group = LoadoutItemGroup.Load(db, groupId);
            if (!group.IsValid()) continue;

            tx.Delete(groupId, recursive: true);

            var priorities = LoadoutItemGroupPriority.FindByTarget(db, groupId);
            foreach (var priority in priorities)
            {
                tx.Delete(priority.Id, recursive: false);
            }

            var loadoutId = group.AsLoadoutItem().LoadoutId;

            // A rule or pin that names the group would point at nothing once the group is gone. The recursive
            // delete above reaches them through that reference today; like the priority rows, they are retracted
            // here so removal doesn't depend on it.
            foreach (var rule in LoadoutGroupRule.FindByLoadout(db, loadoutId).Where(r => r.SourceId == groupId || r.OtherId == groupId))
                tx.Delete(rule.Id, recursive: false);
            foreach (var pin in LoadoutFileWinner.FindByLoadout(db, loadoutId).Where(p => p.WinnerGroupId == groupId))
                tx.Delete(pin.Id, recursive: false);

            if (!loadouts.TryGetValue(loadoutId, out var list))
            {
                list = new List<LoadoutItemGroupId>(capacity: groupIds.Length);
                loadouts[loadoutId] = list;
            }

            list.Add(groupId);
        }

        return loadouts;
    }

    public async ValueTask RemoveCollection(CollectionGroupId collection)
    {
        var db = _connection.Db;
        using var tx = _connection.BeginTransaction();

        tx.Delete(collection, recursive: true);
        var groupIds = LoadoutItem.FindByParent(db, collection).OfTypeLoadoutItemGroup().Select(x => x.LoadoutItemGroupId).ToArray();
        RemoveItems(tx, groupIds);

        await tx.Commit();
    }

    public async ValueTask<CollectionGroup.ReadOnly> CloneCollection(CollectionGroupId collectionId)
    {
        Span<byte> refScratch = stackalloc byte[8];
        using var writer = new PooledMemoryBufferWriter();

        var basisDb = _connection.Db;

        Dictionary<EntityId, EntityId> remappedIds = new();
        var query = _connection.Query<EntityId>($"""
                                          WITH RECURSIVE ChildLoadoutItems (Id) AS 
                                          (SELECT {collectionId.Value} 
                                          UNION
                                          SELECT Id FROM (SELECT Id, Parent FROM mdb_LoadoutItem(Db=>{basisDb})
                                          UNION ALL
                                          SELECT Id, ParentEntity FROM mdb_SortOrder(Db=>{basisDb})
                                          UNION ALL
                                          SELECT Id, ParentSortOrder FROM mdb_SortOrderItem(Db=>{basisDb}))
                                          WHERE Parent in (SELECT Id FROM ChildLoadoutItems))
                                          SELECT DISTINCT Id FROM ChildLoadoutItems
                                          """);
        using var tx = _connection.BeginTransaction();
        foreach (var itemId in query)
        {
            remappedIds.TryAdd(itemId, tx.TempId());
        }

        foreach (var (oldId, newId) in remappedIds)
        {
            var entity = basisDb.Get(oldId);
            foreach (var datom in entity)
            {
                // Remap the value part of references
                if (datom.Prefix.ValueTag == ValueTag.Reference)
                {
                    var oldRef = EntityId.From(UInt64Serializer.Read(datom.ValueSpan));
                    if (!remappedIds.TryGetValue(oldRef, out var newRef))
                    {
                        tx.Add(newId, datom.A, datom.Prefix.ValueTag, datom.ValueSpan);
                        continue;
                    }
                    MemoryMarshal.Write(refScratch, newRef);
                    tx.Add(newId, datom.A, datom.Prefix.ValueTag, refScratch);
                }
                // It's rare, but the Ref,UShort/String tuple type may include a ref that needs to be remapped
                else if (datom.Prefix.ValueTag == ValueTag.Tuple3_Ref_UShort_Utf8I)
                {
                    var (r, s, str) = Tuple3_Ref_UShort_Utf8I_Serializer.Read(datom.ValueSpan);
                    if (!remappedIds.TryGetValue(r, out var newR))
                    {
                        tx.Add(newId, datom.A, datom.Prefix.ValueTag, datom.ValueSpan);
                        continue;
                    }
                    writer.Reset();
                    var newTuple = (newR, s, str);
                    Tuple3_Ref_UShort_Utf8I_Serializer.Write(newTuple, writer);
                    tx.Add(newId, datom.A, datom.Prefix.ValueTag, writer.GetWrittenSpan());
                }
                // Otherwise just remap the E value
                else
                {
                    tx.Add(newId, datom.A, datom.Prefix.ValueTag, datom.ValueSpan);
                }
            }
        }

        var loadoutId = CollectionGroup.Load(basisDb, collectionId).AsLoadoutItemGroup().AsLoadoutItem().LoadoutId;
        var nextPriority = GetNextPriority(loadoutId, _connection.Db);
        var oldPriorities = LoadoutItemGroupPriority.All(basisDb).Where(priority => priority.Target.AsLoadoutItem().ParentId.Value == collectionId.Value).OrderBy(x => x.Priority).ToArray();

        for (uint i = 0; i < oldPriorities.Length; i++)
        {
            var oldPriority = oldPriorities[i];
            _ = new LoadoutItemGroupPriority.New(tx)
            {
                LoadoutId = loadoutId,
                Priority = ConflictPriority.From(nextPriority.Value + i),
                TargetId = remappedIds[oldPriority.TargetId],
            };
        }

        tx.Add(new RebalancePrioritiesTxFunc(loadoutId, toSkip: []));

        var result = await tx.Commit();
        return CollectionGroup.Load(result.Db, result[remappedIds[collectionId]]);
    }

    public async ValueTask ReplaceItems(LoadoutId loadoutId, LoadoutItemGroupId[] groupsToRemove, LibraryItem.ReadOnly libraryItemToInstall)
    {
        using var tx = _connection.BeginTransaction();
        var removed = DeleteGroups(tx, groupsToRemove);
        var installed = new List<LoadoutItemGroupId>();
        await InstallItem(libraryItemToInstall, loadoutId, inputTx: tx, newGroups: installed);

        // One function per loadout, with both halves in its delta.
        foreach (var affected in removed.Keys.Append(loadoutId).Distinct())
        {
            tx.Add(new ApplyGroupRulesTxFunc(affected, new RuleDelta
            {
                RemovedGroups = [..removed.GetValueOrDefault(affected) ?? []],
                NewGroups = affected == loadoutId ? [..installed] : [],
            }));
        }

        await tx.Commit();
    }

    public ValueTask<ConflictMoveResult> ResolveFileConflicts(LoadoutItemGroupPriorityId[] winnerIds, LoadoutItemGroupPriorityId loserId)
        => MovePriorities(winnerIds, loserId);

    /// <summary>
    /// Moves the rows unless the new order breaks a rule that holds in the current one. A rule the order already
    /// breaks is not the move's doing and does not stop it.
    /// </summary>
    private async ValueTask<ConflictMoveResult> MovePriorities(LoadoutItemGroupPriorityId[] winnerIds, Optional<LoadoutItemGroupPriorityId> loserId)
    {
        var db = _connection.Db;
        var loadoutId = LoadoutItemGroupPriority.Load(db, winnerIds[0]).LoadoutId;

        var current = LoadoutItemGroupPriority
            .FindByLoadout(db, loadoutId)
            .OrderBy(static model => model.Priority)
            .ToList();
        var moved = ResolveFileConflictsTxFunc.Reorder(current, winnerIds, loserId);

        var rules = LoadoutGroupRule.FindByLoadout(db, loadoutId).ToList();
        var groupRules = rules.Select(static rule => new GroupRule(rule.SourceId, rule.OtherId, rule.Kind)).ToList();

        // The order is made of mods, so a rule on a collection is checked as the rules between its mods; a break is
        // still reported against the rule that was stored.
        var expanded = ExpandCollectionRules(db, loadoutId, groupRules);
        var expandedRules = expanded.Select(static e => e.Rule).ToList();
        var alreadyBroken = RuleResolver.FindViolations(current.Select(static row => row.TargetId).ToList(), expandedRules).ToHashSet();
        var broken = RuleResolver.FindViolations(moved.Select(static row => row.TargetId).ToList(), expandedRules)
            .Where(index => !alreadyBroken.Contains(index))
            .Select(static index => (int?)index)
            .FirstOrDefault();
        if (broken is { } violated) return ConflictMoveResult.RuleViolation(rules[expanded[violated].Origin]);

        using var tx = _connection.BeginTransaction();
        tx.Add(new ResolveFileConflictsTxFunc(loadoutId, winnerIds, loserId));
        await tx.Commit();
        return ConflictMoveResult.Moved;
    }

    public async ValueTask ApplyCollectionDownloadRules(NexusCollectionLoadoutGroupId collectionId)
    {
        var collectionRules = _connection.Query<(EntityId SourceId, EntityId OtherId, int RuleType)>(
            $"""
              SELECT Source, Other, RuleType FROM loadouts.CollectionRulesOnItems({_connection})
              WHERE Parent = {collectionId.Value};
              """
        );

        var db = _connection.Db;
        var loadoutId = LoadoutItem.Load(db, collectionId).LoadoutId;

        // The query only returns rules whose two downloads both have an installed group in this collection.
        var wanted = collectionRules
            .Select(static rule => new GroupRule(
                LoadoutItemGroupId.From(rule.SourceId.Value),
                LoadoutItemGroupId.From(rule.OtherId.Value),
                (CollectionDownloadRuleType)rule.RuleType switch
                {
                    CollectionDownloadRuleType.Before => GroupRuleKind.Before,
                    CollectionDownloadRuleType.After => GroupRuleKind.After,
                    _ => throw new UnreachableException(),
                }))
            .Distinct()
            .ToArray();

        var stored = StoredGroupRules(db, loadoutId).ToList();
        var storedSet = stored.ToHashSet();
        var added = wanted.Where(rule => !storedSet.Contains(rule)).ToImmutableArray();
        if (added.IsEmpty) return;

        // Checked before the transaction, as in AddGroupRule: inside it the resolver can only fail the commit, which
        // reads as a crash, and a collection that contradicts itself or the player's rules is a refusal, not a bug.
        var delta = new RuleDelta { Candidates = added };
        var expandedStored = ExpandCollectionRules(db, loadoutId, stored).Select(static e => e.Rule);
        if (RuleResolver.Resolve(PriorityOrder(db, loadoutId), expandedStored, delta).Cycle is { } cycle)
        {
            throw new CollectionRulesCycleException(
                collectionName: LoadoutItem.Load(db, collectionId).Name,
                firstGroupName: LoadoutItem.Load(db, cycle.First).Name,
                secondGroupName: LoadoutItem.Load(db, cycle.Second).Name
            );
        }

        using var tx = _connection.BeginTransaction();
        foreach (var rule in added)
        {
            _ = new LoadoutGroupRule.New(tx)
            {
                LoadoutId = loadoutId,
                SourceId = rule.Source,
                OtherId = rule.Other,
                Kind = rule.Kind,
            };
        }

        // The resolver can't read the rows written above, so the new rules travel with it.
        tx.Add(new ApplyGroupRulesTxFunc(loadoutId, delta));
        await tx.Commit();
    }

    public GroupOrderResult SetGroupOrder(LoadoutId loadoutId, IReadOnlyList<LoadoutItemGroupId> winnerToLoser, ITransaction tx, IReadOnlySet<LoadoutItemGroupId>? newGroups = null)
    {
        if (winnerToLoser.Count == 0) return GroupOrderResult.None;

        var duplicate = winnerToLoser.GroupBy(static group => group).FirstOrDefault(static g => g.Count() > 1);
        if (duplicate is not null) throw new ArgumentException($"Group {duplicate.Key.Value} is listed twice.", nameof(winnerToLoser));

        var db = _connection.Db;
        var blockAscending = winnerToLoser.Reverse().ToList();
        var current = LoadoutItemGroupPriority
            .FindByLoadout(db, loadoutId)
            .Where(static row => row.Target.IsValid())
            .OrderBy(static row => row.Priority)
            .Select(static row => row.TargetId)
            .ToList();
        var unplaced = newGroups is null ? [] : blockAscending.Where(newGroups.Contains).ToImmutableArray();
        var projected = ProjectBlock(current, blockAscending, unplaced);

        // The request is not refused: the resolver in the transaction puts the rules back. The same resolver runs here
        // over the projection to learn which pairs of the requested order end up the other way round because a rule
        // names the pair or a chain of rules through other groups does.
        var rules = LoadoutGroupRule.FindByLoadout(db, loadoutId).ToList();
        var expanded = ExpandCollectionRules(db, loadoutId, rules.Select(static rule => new GroupRule(rule.SourceId, rule.OtherId, rule.Kind)).ToList());
        var expandedRules = expanded.Select(static e => e.Rule).ToList();
        var resolution = RuleResolver.Resolve(projected, expandedRules);
        if (resolution.Cycle is { } cycle)
            throw new InvalidOperationException($"The group rules of loadout {loadoutId.Value} contradict each other: {cycle.First.Value} and {cycle.Second.Value}.");

        var finalPosition = new Dictionary<LoadoutItemGroupId, int>();
        foreach (var group in resolution.Order) finalPosition[group] = finalPosition.Count;

        var projectedPosition = new Dictionary<LoadoutItemGroupId, int>();
        foreach (var group in projected) projectedPosition[group] = projectedPosition.Count;

        var violatedInProjection = RuleResolver.FindViolations(projected, expandedRules).ToHashSet();
        var edgesFrom = RuleEdges(expandedRules);
        var overridden = new List<OverriddenOrder>();
        for (var i = 0; i < winnerToLoser.Count; i++)
        {
            for (var j = i + 1; j < winnerToLoser.Count; j++)
            {
                var (winner, loser) = (winnerToLoser[i], winnerToLoser[j]);
                if (finalPosition[winner] > finalPosition[loser]) continue;

                if (RuleOnPath(edgesFrom, winner, loser, violatedInProjection) is { } forcing)
                {
                    overridden.Add(new OverriddenOrder(rules[expanded[forcing].Origin], winner, loser));
                    continue;
                }

                // No chain of rules puts the loser above the winner: the sort moved the pair to repair a rule broken
                // elsewhere in the projection. Name the broken rule whose repair moved one of the pair.
                if (violatedInProjection.Count == 0)
                    throw new InvalidOperationException($"The order of loadout {loadoutId.Value} was changed with no rule broken.");
                var displacing = DisplacingRule(expandedRules, violatedInProjection, winner, loser, group => projectedPosition[group] != finalPosition[group]);
                overridden.Add(new OverriddenOrder(rules[expanded[displacing].Origin], winner, loser, Displaced: true));
            }
        }

        tx.Add(new ApplyGroupRulesTxFunc(loadoutId, new RuleDelta { BlockAscending = [..blockAscending], UnplacedBlockGroups = unplaced }));
        return new GroupOrderResult(overridden, [..winnerToLoser.OrderByDescending(group => finalPosition[group])]);
    }

    /// <summary>
    /// The lowest-index broken rule that names <paramref name="winner"/> or <paramref name="loser"/> and whose end
    /// there moved, since repairing it is what moved the pair; else the lowest-index broken rule.
    /// </summary>
    private static int DisplacingRule(
        IReadOnlyList<GroupRule> rules, HashSet<int> violated,
        LoadoutItemGroupId winner, LoadoutItemGroupId loser, Func<LoadoutItemGroupId, bool> moved)
    {
        bool MovedEnd(GroupRule rule, LoadoutItemGroupId group) => (rule.Source == group || rule.Other == group) && moved(group);

        foreach (var index in violated.Order())
        {
            if (MovedEnd(rules[index], winner) || MovedEnd(rules[index], loser)) return index;
        }

        return violated.Min();
    }

    /// <summary>The rules as edges from the group that must sit lower to the one that must sit higher, with the rule's index.</summary>
    private static Dictionary<LoadoutItemGroupId, List<(int Rule, LoadoutItemGroupId To)>> RuleEdges(IReadOnlyList<GroupRule> rules)
    {
        var edgesFrom = new Dictionary<LoadoutItemGroupId, List<(int Rule, LoadoutItemGroupId To)>>();
        for (var i = 0; i < rules.Count; i++)
        {
            var (from, to) = rules[i].Kind == GroupRuleKind.After ? (rules[i].Other, rules[i].Source) : (rules[i].Source, rules[i].Other);
            if (!edgesFrom.TryGetValue(from, out var list)) edgesFrom[from] = list = [];
            list.Add((i, to));
        }

        return edgesFrom;
    }

    /// <summary>
    /// The index of the rule that keeps <paramref name="winner"/> from ending up above <paramref name="loser"/>: the
    /// one naming the pair, else the first of the chain of rules from winner to loser that the projection breaks (any
    /// rule of the chain if none is broken there). Null when no chain of rules runs from winner to loser.
    /// </summary>
    private static int? RuleOnPath(
        Dictionary<LoadoutItemGroupId, List<(int Rule, LoadoutItemGroupId To)>> edgesFrom,
        LoadoutItemGroupId winner, LoadoutItemGroupId loser, HashSet<int> violated)
    {
        var cameBy = new Dictionary<LoadoutItemGroupId, (int Rule, LoadoutItemGroupId From)>();
        var queue = new Queue<LoadoutItemGroupId>([winner]);
        while (queue.TryDequeue(out var at) && !cameBy.ContainsKey(loser))
        {
            if (!edgesFrom.TryGetValue(at, out var edges)) continue;
            foreach (var (rule, to) in edges)
            {
                if (to == winner || !cameBy.TryAdd(to, (rule, at))) continue;
                queue.Enqueue(to);
            }
        }

        if (!cameBy.ContainsKey(loser)) return null;

        var path = new List<int>();
        for (var at = loser; at != winner; at = cameBy[at].From) path.Add(cameBy[at].Rule);
        path.Reverse();
        var firstViolated = path.FindIndex(violated.Contains);
        return path[firstViolated >= 0 ? firstViolated : 0];
    }

    /// <summary>
    /// <paramref name="ascending"/> with the groups of <paramref name="blockAscending"/> that have a place in it
    /// re-dealt in the block's order over the places they held; the others keep theirs. A block group without a
    /// place goes in at its listed position (the block grows by one place per such group and what sits above it
    /// shifts up). When no block group has a place at all, the whole block goes above everything. A group in
    /// <paramref name="unplaced"/> counts as having no place, whatever row it has.
    /// </summary>
    internal static List<LoadoutItemGroupId> ProjectBlock(
        IReadOnlyList<LoadoutItemGroupId> ascending, IReadOnlyList<LoadoutItemGroupId> blockAscending, IReadOnlyCollection<LoadoutItemGroupId> unplaced)
    {
        // A block group whose slot is not the block's is placed like one without a row; its old slot goes away.
        if (unplaced.Count > 0)
        {
            var dropped = unplaced.ToHashSet();
            ascending = ascending.Where(group => !dropped.Contains(group)).ToList();
        }

        var inOrder = ascending.ToHashSet();
        var blockSet = blockAscending.ToHashSet();
        var heldCount = blockAscending.Count(inOrder.Contains);
        if (heldCount == 0) return [..ascending, ..blockAscending];

        var result = new List<LoadoutItemGroupId>(ascending.Count + blockAscending.Count - heldCount);
        var next = 0;
        var seen = 0;
        foreach (var group in ascending)
        {
            if (!blockSet.Contains(group))
            {
                result.Add(group);
                continue;
            }

            // Each held place takes block groups up to the next group that also had a place, so a group without
            // one lands right below the place of the group that follows it in the block.
            seen++;
            while (next < blockAscending.Count)
            {
                var candidate = blockAscending[next++];
                result.Add(candidate);
                if (inOrder.Contains(candidate)) break;
            }

            // The last place also takes whatever without a place trails the block.
            if (seen == heldCount)
            {
                while (next < blockAscending.Count) result.Add(blockAscending[next++]);
            }
        }

        return result;
    }

    private static LoadoutItemGroupId[] PriorityOrder(IDb db, LoadoutId loadoutId) => LoadoutItemGroupPriority
        .FindByLoadout(db, loadoutId)
        .OrderBy(static p => p.Priority)
        .Select(static p => p.TargetId)
        .ToArray();

    private static IEnumerable<GroupRule> StoredGroupRules(IDb db, LoadoutId loadoutId) => LoadoutGroupRule
        .FindByLoadout(db, loadoutId)
        .Select(static rule => new GroupRule(rule.SourceId, rule.OtherId, rule.Kind));

    public async ValueTask<AddGroupRuleResult> AddGroupRule(LoadoutId loadoutId, LoadoutItemGroupId source, LoadoutItemGroupId other, GroupRuleKind kind)
    {
        var db = _connection.Db;
        var candidate = new GroupRule(source, other, kind);
        var delta = new RuleDelta { Candidates = [candidate] };

        // The resolver inside the transaction only sees the rows from before it, so the cycle check runs here over
        // the stored rules plus the candidate and a refused rule never reaches the database.
        var order = PriorityOrder(db, loadoutId);
        var collections = CollectionMembers(db, loadoutId);

        // The resolver skips rules naming a group outside the order, so without this check a rule on a group of
        // another loadout, or on a nested group, would be stored and never do anything. A collection stands for the
        // mods it holds in this loadout, so it counts when it holds any.
        if (!order.Contains(source) && !collections.ContainsKey(source)) throw new ArgumentException($"Group {source.Value} has no priority in loadout {loadoutId.Value}.", nameof(source));
        if (!order.Contains(other) && !collections.ContainsKey(other)) throw new ArgumentException($"Group {other.Value} has no priority in loadout {loadoutId.Value}.", nameof(other));

        var storedRules = RuleResolver.ExpandCollections(StoredGroupRules(db, loadoutId).ToList(), collections).Select(static e => e.Rule);
        var expandedDelta = delta with { Candidates = [..RuleResolver.ExpandCollections(delta.Candidates, collections).Select(static e => e.Rule)] };
        if (RuleResolver.Resolve(order, storedRules, expandedDelta).Cycle is { } cycle)
        {
            // The loop is found between mods; a rule on a collection is reported against the collection the player picked.
            var namesCollection = collections.ContainsKey(source) || collections.ContainsKey(other);
            return AddGroupRuleResult.Cyclic(namesCollection ? new RuleCycle(source, other) : cycle);
        }

        using var tx = _connection.BeginTransaction();
        var rule = new LoadoutGroupRule.New(tx)
        {
            LoadoutId = loadoutId,
            SourceId = source,
            OtherId = other,
            Kind = kind,
        };
        tx.Add(new ApplyGroupRulesTxFunc(loadoutId, delta));

        var result = await tx.Commit();
        return AddGroupRuleResult.Added(LoadoutGroupRuleId.From(result[rule.Id]));
    }

    public async ValueTask PinFileWinner(LoadoutId loadoutId, GamePath path, LoadoutItemGroupId winner)
    {
        var db = _connection.Db;
        var group = LoadoutItemGroup.Load(db, winner);
        if (!group.IsValid() || group.AsLoadoutItem().LoadoutId != loadoutId)
            throw new ArgumentException($"Group {winner.Value} is not in loadout {loadoutId.Value}.", nameof(winner));

        using var tx = _connection.BeginTransaction();
        var existing = FindFileWinner(db, loadoutId, path);
        if (existing is { } pin)
        {
            tx.Add(pin.Id, LoadoutFileWinner.WinnerGroup, winner);
        }
        else
        {
            _ = new LoadoutFileWinner.New(tx)
            {
                LoadoutId = loadoutId,
                TargetPath = path.ToGamePathParentTuple(loadoutId.Value),
                WinnerGroupId = winner,
            };
        }

        await tx.Commit();
    }

    public async ValueTask ClearFileWinner(LoadoutId loadoutId, GamePath path)
    {
        if (FindFileWinner(_connection.Db, loadoutId, path) is not { } pin) return;

        using var tx = _connection.BeginTransaction();
        tx.Delete(pin.Id, recursive: false);
        await tx.Commit();
    }

    private static LoadoutFileWinner.ReadOnly? FindFileWinner(IDb db, LoadoutId loadoutId, GamePath path) => LoadoutFileWinner
        .FindByLoadout(db, loadoutId)
        .Where(p => p.TargetPath.Item2 == path.LocationId && p.TargetPath.Item3 == path.Path)
        .Select(static p => (LoadoutFileWinner.ReadOnly?)p)
        .FirstOrDefault();

    public async ValueTask RemoveGroupRule(LoadoutGroupRuleId ruleId)
    {
        using var tx = _connection.BeginTransaction();
        tx.Delete(ruleId, recursive: false);
        await tx.Commit();
    }

    public ValueTask<ConflictMoveResult> LoseAllFileConflicts(LoadoutItemGroupPriorityId[] loserIds)
        => MovePriorities(loserIds, Optional<LoadoutItemGroupPriorityId>.None);

    public ValueTask<ConflictMoveResult> WinAllFileConflicts(LoadoutItemGroupPriorityId[] winnerIds)
    {
        var db = _connection.Db;
        var loser = LoadoutItemGroupPriority
            .FindByLoadout(db, LoadoutItemGroupPriority.Load(db, winnerIds[0]).LoadoutId)
            .OrderByDescending(static model => model.Priority)
            .First();

        return ResolveFileConflicts(winnerIds: winnerIds, loserId: loser.LoadoutItemGroupPriorityId);
    }
}
