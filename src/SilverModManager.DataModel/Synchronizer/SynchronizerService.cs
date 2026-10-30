using System.Reactive.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Files.Diff;
using SilverModManager.Abstractions.Loadouts.Exceptions;
using SilverModManager.Abstractions.Loadouts.Ids;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using DynamicData.Kernel;
using SilverModManager.Sdk;
using NexusMods.Paths;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using ReactiveUI;
using R3;
using GameInstallMetadata = SilverModManager.Sdk.Games.GameInstallMetadata;

namespace SilverModManager.DataModel.Synchronizer;

/// <inheritdoc />
public class SynchronizerService : ISynchronizerService
{
    private readonly ILogger<SynchronizerService> _logger;
    private readonly IConnection _conn;
    private readonly IGameRegistry _gameRegistry;
    private readonly IJobMonitor _jobMonitor;
    private readonly IServiceProvider _serviceProvider;
    private readonly Dictionary<GameInstallMetadataId, SynchronizerState> _gameStates;
    private readonly Dictionary<LoadoutId, SynchronizerState> _loadoutStates;
    private readonly Lazy<ILoadoutManager> _loadoutManager;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly object _lock = new();

    /// <summary>
    /// DI Constructor
    /// </summary>
    public SynchronizerService(IConnection conn, ILogger<SynchronizerService> logger, IGameRegistry gameRegistry, IFileHashesService fileHashesService, IJobMonitor jobMonitor, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _conn = conn;
        _jobMonitor = jobMonitor;
        _gameRegistry = gameRegistry;
        _gameStates = GameInstallMetadata.All(conn.Db).ToDictionary(e => e.GameInstallMetadataId, _ => new SynchronizerState());
        _loadoutStates = Loadout.All(conn.Db).ToDictionary(e => e.LoadoutId, _ => new SynchronizerState());
        _fileHashesService = fileHashesService;
        _serviceProvider = serviceProvider;
        _loadoutManager = new Lazy<ILoadoutManager>(serviceProvider.GetRequiredService<ILoadoutManager>);
    }

    /// <inheritdoc />
    public FileDiffTree GetApplyDiffTree(LoadoutId loadoutId)
    {
        var db = _conn.Db;
        var loadout = Loadout.Load(db, loadoutId);
        var synchronizer = ((IGame)loadout.Game).Synchronizer;
        var metaData = loadout.Installation;
        var hasPreviousLoadout = GameInstallMetadata.LastSyncedLoadoutTransaction.TryGetValue(metaData, out var lastId);

        var lastScannedDiskState = synchronizer.GetLastScannedDiskState(metaData);
        var previousDiskState = hasPreviousLoadout ? synchronizer.GetPreviouslyAppliedDiskState(metaData) : lastScannedDiskState;
        
        return synchronizer.LoadoutToDiskDiff(loadout, previousDiskState, lastScannedDiskState);
    }

    public IJobTask<ProcessLoadoutChangesJob, bool> GetShouldSynchronize(LoadoutId loadoutId)
    {
        return _jobMonitor.Begin(new ProcessLoadoutChangesJob(loadoutId),
            ctx =>
            {
                var db = _conn.Db;
                var loadout = Loadout.Load(db, loadoutId);
                if (!loadout.IsValid())
                    return ValueTask.FromResult(false);
                var synchronizer = loadout.InstallationInstance.GetGame().Synchronizer;
                var metaData = loadout.Installation;
                var hasPreviousLoadout = GameInstallMetadata.LastSyncedLoadoutTransaction.TryGetValue(metaData, out var lastId);

                var lastScannedDiskState = synchronizer.GetDiskStateForGame(metaData);
                var previousDiskState = hasPreviousLoadout ? synchronizer.GetLastScannedDiskState(metaData) : lastScannedDiskState;
        
                return ValueTask.FromResult(synchronizer.ShouldSynchronize(loadout, previousDiskState, lastScannedDiskState));
            });
    }
    
    /// <inheritdoc />
    public Task Synchronize(LoadoutId loadoutId, BackupOverflowChoice? largeFiles = null, CancellationToken cancellationToken = default)
        => SynchronizeCore(loadoutId, refuseWhileRunning: true, reselectBackends: true, largeFiles, cancellationToken);

    /// <inheritdoc />
    public async Task CleanGameFolder(LoadoutId loadoutId, BackupOverflowChoice? largeFiles = null, CancellationToken cancellationToken = default)
    {
        using var origin = SyncOrigin.Begin(SyncTrigger.ResetGameFolder);
        await SynchronizeCore(loadoutId, refuseWhileRunning: true, reselectBackends: true, largeFiles, cancellationToken, cleanExternalChanges: true, operation: BackupOverflowOperation.Reset);
    }

    /// <summary>
    /// Synchronizes on behalf of a launch session, which holds the running-game state itself and so
    /// must not be refused by it. Everything else (semaphore, state locks) is the same as <see cref="Synchronize"/>,
    /// except that the deployment plan is read and never changed: a launch applies what the person last applied.
    /// </summary>
    internal Task SynchronizeFromLaunchSession(LoadoutId loadoutId) => SynchronizeCore(loadoutId, refuseWhileRunning: false, reselectBackends: false, largeFiles: null, CancellationToken.None, operation: BackupOverflowOperation.Launch);

    private async Task SynchronizeCore(LoadoutId loadoutId, bool refuseWhileRunning, bool reselectBackends, BackupOverflowChoice? largeFiles, CancellationToken callerToken, bool cleanExternalChanges = false, BackupOverflowOperation operation = BackupOverflowOperation.Apply)
    {
        await _jobMonitor.Begin(new SynchronizeLoadoutJob(loadoutId),
            async ctx =>
            {
                var job = ctx.Definition;

                // The caller's token too: cancelling a create job has to end a sync of its own that sits at the
                // backup-cap question, and that sync is a job of its own that the create job only awaits.
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken, callerToken);
                await _semaphore.WaitAsync(linked.Token);
                try
                {
                    var loadout = Loadout.Load(_conn.Db, loadoutId);
                    if (!loadout.IsValid())
                    {
                        return Unit.Default;
                    }
                    if (refuseWhileRunning) RunningGameGuard.ThrowIfRunning(_serviceProvider, loadout);

                    var loadoutState = GetOrAddLoadoutState(loadoutId);
                    using var _ = loadoutState.WithLock();

                    var gameState = GetOrAddGameState(loadout.InstallationId);
                    using var _2 = gameState.WithLock();

                    // One answer to the backup-cap question covers the backend move and the apply that follows it.
                    using var answer = BackupOverflowAnswer.Begin(operation);

                    if (reselectBackends) await ReselectUnchosenBackends(loadout.InstallationInstance, largeFiles, linked.Token);

                    if (!cleanExternalChanges)
                    {
                        await loadout.InstallationInstance.GetGame().Synchronizer.Synchronize(loadout, job, largeFiles, linked.Token);
                    }
                    else
                    {
                        await CleanExternalChanges(loadout, job, largeFiles, linked.Token);
                    }
                }
                catch (SynchronizerPreflightException e) when (e.Action is null)
                {
                    // Stamped here, inside the job, so every entry point (apply, launch sessions, the CLI) carries it
                    // and it is set before the job fails and its awaiter runs.
                    e.Action = new OpenHealthCheck(loadoutId);
                    throw;
                }
                finally
                {
                    _semaphore.Release();
                }
                
                return Unit.Default;
            }
        );
    }

    /// <summary>
    /// Deletes the loadout's External Changes and applies it. The rows go first because the apply measures what it
    /// would back up from the loadout as it is after the clean, but a refused question, a cancelled wait or a failure
    /// before the apply started writing is no reason to lose them, so they are put back then.
    /// </summary>
    private async Task CleanExternalChanges(Loadout.ReadOnly loadout, SynchronizeLoadoutJob job, BackupOverflowChoice? largeFiles, CancellationToken cancellationToken)
    {
        var synchronizer = loadout.InstallationInstance.GetGame().Synchronizer;
        synchronizer.EnsureBuildCovered(loadout.InstallationInstance, loadout.InstallationInstance.LocatorResult.LocatorIds.Distinct().ToArray());

        var removed = await RemoveExternalChanges(loadout);
        try
        {
            await synchronizer.Synchronize(Loadout.Load(_conn.Db, loadout.LoadoutId), job, largeFiles, cancellationToken);
        }
        catch (Exception e) when (e is BackupOverflowException or SynchronizerPreflightException or OperationCanceledException || !ApplyBeganWriting(job))
        {
            await RestoreExternalChanges(removed);
            throw;
        }
    }

    /// <summary>
    /// Whether the apply got past collecting, indexing and reading the files to back up, to the phases that archive
    /// files and change the folder. An apply that failed before that, such as on an unreadable disk or a file that
    /// changed while it was read, changed nothing, so the rows it was measured against are still the loadout's.
    /// </summary>
    private static bool ApplyBeganWriting(SynchronizeLoadoutJob job)
        => job.Progress.Value.Phase is { } phase && phase != SyncPhases.Collecting && phase != SyncPhases.ReadingBackups && phase != SyncPhases.WaitingForAnswer;

    private async Task<List<(EntityId Entity, AttributeId Attribute, ValueTag Tag, byte[] Value)>> RemoveExternalChanges(Loadout.ReadOnly loadout)
    {
        loadout = loadout.Rebase();
        var removed = new List<(EntityId, AttributeId, ValueTag, byte[])>();

        using var tx = _conn.BeginTransaction();
        if (LoadoutOverridesGroup.FindByOverridesFor(loadout.Db, loadout.Id).TryGetFirst(out var overridesGroup))
        {
            foreach (var entry in overridesGroup.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath())
            {
                foreach (var datom in loadout.Db.Get(entry.Id))
                    removed.Add((datom.E, datom.A, datom.Prefix.ValueTag, datom.ValueSpan.ToArray()));

                tx.Delete(entry.Id, recursive: false);
            }
        }

        await tx.Commit();
        return removed;
    }

    private async Task RestoreExternalChanges(List<(EntityId Entity, AttributeId Attribute, ValueTag Tag, byte[] Value)> removed)
    {
        if (removed.Count == 0) return;

        using var tx = _conn.BeginTransaction();
        foreach (var (entity, attribute, tag, value) in removed) tx.Add(entity, attribute, tag, value);
        await tx.Commit();
    }

    /// <inheritdoc />
    public async Task UnManage(GameInstallation installation, bool runGc = true, bool cleanGameFolder = true, BackupOverflowChoice? largeFiles = null)
    {
        using var origin = SyncOrigin.Begin(SyncTrigger.Unmanage);
        await _semaphore.WaitAsync();
        try
        {
            if (!_gameRegistry.TryGetMetadata(installation, out var metadata)) return;
            await _loadoutManager.Value.UnManage(installation, runGc, cleanGameFolder, largeFiles: largeFiles);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> ChangeDeploymentBackend(GameInstallation installation, GamePath root, DeploymentBackendId backend, bool chosen, BackupOverflowChoice? largeFiles = null, CancellationToken cancellationToken = default)
    {
        using var origin = SyncOrigin.Begin(SyncTrigger.DeploymentMethodSwitch);
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            RunningGameGuard.ThrowIfRunning(_serviceProvider, installation);

            using var _ = GetOrAddGameState(_gameRegistry.ForceGetMetadata(installation).GameInstallMetadataId).WithLock();
            return await MoveRoots(installation, [new RootMove(root, backend, chosen)], reapplyLastLoadout: true, largeFiles, cancellationToken);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private sealed record RootMove(GamePath Root, DeploymentBackendId Backend, bool Chosen);

    /// <summary>
    /// Moves every root whose row was recorded by the selection chain, and not picked by a person, to what the
    /// chain picks for it now. Runs at the start of an apply, with the semaphore and the game's state lock held,
    /// so the apply that follows deploys with the new backends.
    /// </summary>
    private async Task ReselectUnchosenBackends(GameInstallation installation, BackupOverflowChoice? largeFiles, CancellationToken cancellationToken)
    {
        var metadataId = _gameRegistry.ForceGetMetadata(installation).GameInstallMetadataId;
        var plan = _serviceProvider.GetRequiredService<IDeploymentPlanStore>().Load(_conn.Db, metadataId);
        var selector = _serviceProvider.GetRequiredService<IDeploymentBackendSelector>();

        var moves = new List<RootMove>();
        // The chain probes by writing files into the store directory, which a sweep must not meet half way.
        using (_serviceProvider.GetRequiredService<ILooseStagingStore>().Hold())
        {
            foreach (var row in plan.Roots.Where(r => !r.Chosen))
            {
                var choice = selector.Choose(installation, row.Root);
                if (choice.Backend == row.Backend) continue;

                _logger.LogInformation("Moving deployment root {Root} of {Game} from {OldBackend} to {NewBackend}: the recorded backend was not picked by hand and the selection chain now chooses differently ({Reason})",
                    row.Root, installation.Game.DisplayName, row.Backend, choice.Backend, choice.Reason);
                moves.Add(new RootMove(row.Root, choice.Backend, Chosen: false));
            }
        }

        if (moves.Count > 0 && _loadoutManager.Value.GetCurrentlyActiveLoadout(installation).HasValue)
        {
            // A move resets the game with the old backend first, and a reset is only safe when the vanilla file list
            // covers the build. The game stays on the backend it has; the apply goes ahead with it.
            try
            {
                var synchronizer = installation.GetGame().Synchronizer;
                synchronizer.EnsureBuildCovered(installation, installation.LocatorResult.LocatorIds.Distinct().ToArray());
            }
            catch (SynchronizerPreflightException e)
            {
                _logger.LogWarning("Not moving the deployment roots of {Game} to another backend: {Reason}", installation.Game.DisplayName, e.Message);
                return;
            }
        }

        // The apply this is part of puts the files back.
        if (moves.Count > 0) await MoveRoots(installation, moves, reapplyLastLoadout: false, largeFiles, cancellationToken);
    }

    /// <summary>
    /// Resets the game with the backends it is deployed with, records the new backends and, when asked, applies
    /// the last applied loadout again. The caller holds the semaphore and the game's state lock.
    /// </summary>
    /// <returns>
    /// False when every root already uses its backend, so nothing was redeployed. A person's pick of the backend a
    /// root already has is still recorded as chosen.
    /// </returns>
    private async Task<bool> MoveRoots(GameInstallation installation, IReadOnlyList<RootMove> requested, bool reapplyLastLoadout, BackupOverflowChoice? largeFiles, CancellationToken cancellationToken)
    {
        var metadata = _gameRegistry.ForceGetMetadata(installation);
        var metadataId = metadata.GameInstallMetadataId;
        var plans = _serviceProvider.GetRequiredService<IDeploymentPlanStore>();
        var plan = plans.Load(_conn.Db, metadataId);
        var moves = requested.Where(m => plan.BackendFor(m.Root) != m.Backend).ToArray();

        // Picking the backend a root already has deploys nothing differently, but the pick must still stick:
        // without the mark the next apply would hand the root back to the selection chain.
        var pins = requested
            .Where(m => m.Chosen && plan.BackendFor(m.Root) == m.Backend && !plan.Roots.Any(r => r.Root == m.Root && r.Chosen))
            .ToArray();
        if (moves.Length == 0)
        {
            if (pins.Length > 0) await WriteRows(plans, metadataId, pins);
            return false;
        }

        // The reset below retracts LastSyncedLoadout, so it has to be read first.
        var hadLoadout = GameInstallMetadata.LastSyncedLoadout.TryGetValue(metadata, out var lastLoadoutId);

        // The reset and the apply after it are the same operation to the player, so one answer covers them.
        using var answer = BackupOverflowAnswer.Begin(BackupOverflowOperation.Apply);

        var registered = _serviceProvider.GetServices<IDeploymentBackend>().Select(b => b.Id).ToHashSet();
        var unknown = plan.Roots.Where(r => !registered.Contains(r.Backend)).ToArray();
        if (unknown.Length == 0)
        {
            // Resets with the plan as it stands, so the backend being replaced removes what it deployed. The
            // root may have been moved here because its backend stopped working, so the apply this starts with
            // must not refuse for that reason.
            using (RecordedRootPreflight.Suspend())
            {
                await _loadoutManager.Value.DeactivateCurrentLoadout(installation, cancellationToken, largeFiles);
            }
        }
        else
        {
            // A backend this build doesn't know can't run its reset; trying would fail on every attempt and
            // leave the install stuck on the stale row.
            foreach (var entry in unknown)
            {
                _logger.LogWarning("Deployment root {Root} names unregistered backend {Backend}, skipping the reset before switching to {NewBackend}", entry.Root, entry.Backend, moves[0].Backend);
            }
        }

        await WriteRows(plans, metadataId, [..moves, ..pins]);

        if (hadLoadout && reapplyLastLoadout)
        {
            // Anything loaded before the reset is bound to the old db. Not through Synchronize(LoadoutId): that
            // waits on the semaphore the caller already holds.
            var loadout = Loadout.Load(_conn.Db, lastLoadoutId);
            await loadout.InstallationInstance.GetGame().Synchronizer.Synchronize(loadout, largeFiles: largeFiles, cancellationToken: cancellationToken);
        }

        return true;
    }

    private async Task WriteRows(IDeploymentPlanStore plans, GameInstallMetadataId metadataId, IEnumerable<RootMove> rows)
    {
        using var tx = _conn.BeginTransaction();
        foreach (var row in rows)
        {
            plans.Write(tx, _conn.Db, metadataId, row.Root, row.Backend, row.Chosen);
        }
        await tx.Commit();
    }

    private SynchronizerState GetOrAddLoadoutState(LoadoutId loadoutId)
    {
        lock (_lock)
        {
            if (!_loadoutStates.TryGetValue(loadoutId, out var state))
            {
                _loadoutStates[loadoutId] = state = new SynchronizerState();
            }

            return state;
        }
    }
    
    private SynchronizerState GetOrAddGameState(GameInstallMetadataId metadataId)
    {
        lock (_lock)
        {
            if (!_gameStates.TryGetValue(metadataId, out var state))
            {
                _gameStates[metadataId] = state = new SynchronizerState();
            }

            return state;
        }
    }

    /// <inheritdoc />
    public bool TryGetLastAppliedLoadout(GameInstallation gameInstallation, out Loadout.ReadOnly loadout)
    {
        var metadata = _gameRegistry.ForceGetMetadata(gameInstallation);

        if (GameInstallMetadata.LastSyncedLoadout.TryGetValue(metadata, out var lastId))
        {
            loadout = Loadout.Load(_conn.Db, lastId);
            return true;
        }
        
        loadout = default(Loadout.ReadOnly);
        return false;
    }

    /// <inheritdoc />
    public IObservable<LoadoutWithTxId> LastAppliedRevisionFor(GameInstallation gameInstallation)
    {
        return GameInstallMetadata.Observe(_conn, _gameRegistry.ForceGetMetadata(gameInstallation))
            .Select(metadata =>
                {
                    if (GameInstallMetadata.LastSyncedLoadout.TryGetValue(metadata, out var lastId) 
                        && GameInstallMetadata.LastSyncedLoadoutTransaction.TryGetValue(metadata, out var txId))
                    {
                        return new LoadoutWithTxId(lastId, TxId.From(txId.Value));
                    }

                    return default(LoadoutWithTxId);
                }
            );
    }

    
    /// <inheritdoc />
    public IObservable<GameSynchronizerState> StatusForGame(GameInstallMetadataId gameInstallId)
    {
        var gameState = GetOrAddGameState(gameInstallId);
        return gameState.ObservableForProperty(s => s.Busy, beforeChange: false, skipInitial: false)
            .Select(e => e.Value ? GameSynchronizerState.Busy : GameSynchronizerState.Idle);
    } 
    
    private readonly Dictionary<LoadoutId, Observable<LoadoutSynchronizerState>> _statusObservables = new();
    private readonly SemaphoreSlim _statusSemaphore = new(1, 1);
    private readonly IFileHashesService _fileHashesService;

    /// <inheritdoc />
    public async Task<IObservable<LoadoutSynchronizerState>> StatusForLoadout(LoadoutId loadoutId)
    {
        await _statusSemaphore.WaitAsync();
        try
        {
            // This observable may perform heavy diffing operation, so it needs to be shared between all subscribers
            if (_statusObservables.TryGetValue(loadoutId, out var observable)) return observable.AsSystemObservable();

            observable = CreateStatusObservable(loadoutId);
            _statusObservables[loadoutId] = observable;
            return observable.AsSystemObservable();
        }
        finally
        {
            _statusSemaphore.Release();
        }
    }

    private Observable<LoadoutSynchronizerState> CreateStatusObservable(LoadoutId loadoutId)
    {
        var loadout = Loadout.Load(_conn.Db, loadoutId);

        // Without a game there is no installation to watch and no synchronizer to diff with. Keep following
        // the loadout so the status is re-evaluated, but report it as unavailable instead of failing the stream.
        if (!loadout.TryGetGame(out _))
        {
            _logger.LogWarning("Loadout `{LoadoutName}` ({LoadoutId}) is on a game this build does not know, its status is unavailable", loadout.Name, loadoutId);
            return LoadoutQueries2.RevisionsWithChildUpdates(_conn, loadoutId)
                .ToObservable()
                .Select(_ => LoadoutSynchronizerState.Unavailable)
                .Replay(1)
                .RefCount();
        }

        var loadoutState = GetOrAddLoadoutState(loadoutId);

        var isBusy = loadoutState.ObservePropertyChanged(l => l.Busy);

        var lastApplied = LastAppliedRevisionFor(loadout.InstallationInstance)
            .ToObservable();

        var revisions = LoadoutQueries2.RevisionsWithChildUpdates(_conn, loadoutId)
            .ToObservable()
            // Use DB transaction, since child updates are not part of the loadout
            .Select(rev => (loadout: rev, revDbTx: _conn.Db.BasisTxId));

        var statusFailureLogged = 0;
        var statusObservable = isBusy.CombineLatest(lastApplied,
                revisions,
                (busy, last, rev) => (busy, last, rev.loadout, rev.revDbTx)
            )
            
            .DistinctUntilChanged()
            .SelectAwait(
                async (tuple, cancellationToken) =>
                {
                    // To make sure the DB is loaded, before we start diffing
                    await _fileHashesService.GetFileHashesDb();
                 
                    var (busy, last, rev, revDbTx) = tuple;
                    
                    // if the loadout is not found, it means it was deleted
                    if (!rev.IsValid())
                        return LoadoutSynchronizerState.OtherLoadoutSynced;
                    
                    if (busy)
                        return LoadoutSynchronizerState.Pending;

                    if (last.Id != loadoutId && last != default(LoadoutWithTxId))
                        return LoadoutSynchronizerState.OtherLoadoutSynced;

                    // Last DB revision is the same in the applied loadout
                    if (last.Id == rev.LoadoutId && revDbTx == last.Tx)
                        return LoadoutSynchronizerState.Current;

                    // Potentially long operation, run on thread pool
                    bool diffFound;
                    try
                    {
                        diffFound = await Task.Run(async () =>
                        {
                            _logger.LogTrace("Checking for changes in loadout {LoadoutId}", loadoutId);
                            var diffFound = await GetShouldSynchronize(loadoutId);
                            _logger.LogTrace("Changes found in loadout {LoadoutId}: {DiffFound}", loadoutId, diffFound);
                            return diffFound;
                        }, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        // An unhandled exception here would end the stream, and from there reach the UI thread
                        // and take the process down. Every later revision would fail the same way, so only the
                        // first failure is an error.
                        if (Interlocked.Exchange(ref statusFailureLogged, 1) == 0)
                            _logger.LogError(e, "Failed to compute the status of loadout `{LoadoutName}` ({LoadoutId})", rev.Name, loadoutId);
                        else
                            _logger.LogDebug(e, "Status of loadout `{LoadoutName}` ({LoadoutId}) is still unavailable", rev.Name, loadoutId);
                        return LoadoutSynchronizerState.Unavailable;
                    }

                    return diffFound ? LoadoutSynchronizerState.NeedsSync : LoadoutSynchronizerState.Current;
                },
                awaitOperation: AwaitOperation.ThrottleFirstLast
            )
            .Replay(1)
            .RefCount();

        return statusObservable;
    }
}
