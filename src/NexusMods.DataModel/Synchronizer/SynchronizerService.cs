using System.Reactive.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Games.FileHashes;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Abstractions.Loadouts.Files.Diff;
using NexusMods.Abstractions.Loadouts.Exceptions;
using NexusMods.Abstractions.Loadouts.Ids;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.Loadouts.Synchronizers.Deployment;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Loadouts;
using ReactiveUI;
using R3;
using GameInstallMetadata = NexusMods.Sdk.Games.GameInstallMetadata;

namespace NexusMods.DataModel.Synchronizer;

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
    public Task Synchronize(LoadoutId loadoutId) => SynchronizeCore(loadoutId, refuseWhileRunning: true, reselectBackends: true);

    /// <summary>
    /// Synchronizes on behalf of a launch session, which holds the running-game state itself and so
    /// must not be refused by it. Everything else (semaphore, state locks) is the same as <see cref="Synchronize"/>,
    /// except that the deployment plan is read and never changed: a launch applies what the person last applied.
    /// </summary>
    internal Task SynchronizeFromLaunchSession(LoadoutId loadoutId) => SynchronizeCore(loadoutId, refuseWhileRunning: false, reselectBackends: false);

    private async Task SynchronizeCore(LoadoutId loadoutId, bool refuseWhileRunning, bool reselectBackends)
    {
        await _jobMonitor.Begin(new SynchronizeLoadoutJob(loadoutId),
            async ctx =>
            {
                var job = ctx.Definition;
                await _semaphore.WaitAsync();
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

                    if (reselectBackends) await ReselectUnchosenBackends(loadout.InstallationInstance);

                    await loadout.InstallationInstance.GetGame().Synchronizer.Synchronize(loadout, job);
                }
                finally
                {
                    _semaphore.Release();
                }
                
                return Unit.Default;
            }
        );
    }

    /// <inheritdoc />
    public async Task UnManage(GameInstallation installation, bool runGc = true, bool cleanGameFolder = true)
    {
        await _semaphore.WaitAsync();
        try
        {
            if (!_gameRegistry.TryGetMetadata(installation, out var metadata)) return;
            await _loadoutManager.Value.UnManage(installation, runGc, cleanGameFolder);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> ChangeDeploymentBackend(GameInstallation installation, GamePath root, DeploymentBackendId backend, bool chosen)
    {
        await _semaphore.WaitAsync();
        try
        {
            RunningGameGuard.ThrowIfRunning(_serviceProvider, installation);

            using var _ = GetOrAddGameState(_gameRegistry.ForceGetMetadata(installation).GameInstallMetadataId).WithLock();
            return await MoveRoots(installation, [new RootMove(root, backend, chosen)], reapplyLastLoadout: true);
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
    private async Task ReselectUnchosenBackends(GameInstallation installation)
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

        // The apply this is part of puts the files back.
        if (moves.Count > 0) await MoveRoots(installation, moves, reapplyLastLoadout: false);
    }

    /// <summary>
    /// Resets the game with the backends it is deployed with, records the new backends and, when asked, applies
    /// the last applied loadout again. The caller holds the semaphore and the game's state lock.
    /// </summary>
    /// <returns>
    /// False when every root already uses its backend, so nothing was redeployed. A person's pick of the backend a
    /// root already has is still recorded as chosen.
    /// </returns>
    private async Task<bool> MoveRoots(GameInstallation installation, IReadOnlyList<RootMove> requested, bool reapplyLastLoadout)
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

        var registered = _serviceProvider.GetServices<IDeploymentBackend>().Select(b => b.Id).ToHashSet();
        var unknown = plan.Roots.Where(r => !registered.Contains(r.Backend)).ToArray();
        if (unknown.Length == 0)
        {
            // Resets with the plan as it stands, so the backend being replaced removes what it deployed. The
            // root may have been moved here because its backend stopped working, so the apply this starts with
            // must not refuse for that reason.
            using (RecordedRootPreflight.Suspend())
            {
                await _loadoutManager.Value.DeactivateCurrentLoadout(installation);
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
            await loadout.InstallationInstance.GetGame().Synchronizer.Synchronize(loadout);
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
        var loadoutState = GetOrAddLoadoutState(loadoutId);

        var isBusy = loadoutState.ObservePropertyChanged(l => l.Busy);

        var lastApplied = LastAppliedRevisionFor(loadout.InstallationInstance)
            .ToObservable();

        var revisions = LoadoutQueries2.RevisionsWithChildUpdates(_conn, loadoutId)
            .ToObservable()
            // Use DB transaction, since child updates are not part of the loadout
            .Select(rev => (loadout: rev, revDbTx: _conn.Db.BasisTxId));

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
                    var diffFound = await Task.Run(async () =>
                        {
                            _logger.LogTrace("Checking for changes in loadout {LoadoutId}", loadoutId);
                            var diffFound = await GetShouldSynchronize(loadoutId);
                            _logger.LogTrace("Changes found in loadout {LoadoutId}: {DiffFound}", loadoutId, diffFound);
                            return diffFound;
                        }, cancellationToken);

                    return diffFound ? LoadoutSynchronizerState.NeedsSync : LoadoutSynchronizerState.Current;
                },
                awaitOperation: AwaitOperation.ThrottleFirstLast
            )
            .Replay(1)
            .RefCount();

        return statusObservable;
    }
}
