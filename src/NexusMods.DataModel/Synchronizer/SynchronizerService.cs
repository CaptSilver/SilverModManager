using System.Reactive.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Games.FileHashes;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Files.Diff;
using NexusMods.Abstractions.Loadouts.Exceptions;
using NexusMods.Abstractions.Loadouts.Ids;
using NexusMods.Abstractions.Loadouts.Synchronizers;
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
    public Task Synchronize(LoadoutId loadoutId) => SynchronizeCore(loadoutId, refuseWhileRunning: true);

    /// <summary>
    /// Synchronizes on behalf of a launch session, which holds the running-game state itself and so
    /// must not be refused by it. Everything else (semaphore, state locks) is the same as <see cref="Synchronize"/>.
    /// </summary>
    internal Task SynchronizeFromLaunchSession(LoadoutId loadoutId) => SynchronizeCore(loadoutId, refuseWhileRunning: false);

    private async Task SynchronizeCore(LoadoutId loadoutId, bool refuseWhileRunning)
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
