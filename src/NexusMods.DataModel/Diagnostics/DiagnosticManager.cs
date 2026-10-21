using System.Collections.Frozen;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Loadouts;
using R3;
using CompositeDisposable = R3.CompositeDisposable;
using GameInstallMetadata = NexusMods.Sdk.Games.GameInstallMetadata;

namespace NexusMods.DataModel.Diagnostics;

/// <inheritdoc/>
[UsedImplicitly]
internal sealed class DiagnosticManager : IDiagnosticManager
{
    private readonly ILogger<DiagnosticManager> _logger;

    private static readonly object Lock = new();
    private readonly Dictionary<LoadoutId, LoadoutDiagnostics> _loadouts = [];

    private bool _isDisposed;
    private readonly CompositeDisposable _compositeDisposable = new();
    private readonly IConnection _connection;
    private readonly IDiagnosticEmitter[] _sharedEmitters;

    /// <param name="sharedEmitters">Emitters registered in the service collection; they run for every game, next to the game's own.</param>
    public DiagnosticManager(ILogger<DiagnosticManager> logger, IConnection connection, IEnumerable<IDiagnosticEmitter> sharedEmitters)
    {
        _logger = logger;
        _connection = connection;
        _sharedEmitters = sharedEmitters.ToArray();
    }

    public IObservable<Diagnostic[]> GetLoadoutDiagnostics(LoadoutId loadoutId)
    {
        lock (Lock)
        {
            return GetOrCreate(loadoutId).Diagnostics;
        }
    }

    public Task RefreshAsync(LoadoutId loadoutId)
    {
        LoadoutDiagnostics entry;
        TaskCompletionSource pending;
        lock (Lock)
        {
            entry = GetOrCreate(loadoutId);
            if (entry.PendingRefresh is { } running) return running.Task;

            pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            entry.PendingRefresh = pending;
            entry.PendingRefreshAfterRun = Volatile.Read(ref entry.RunsStarted.Value);
        }

        // Sent outside the lock: the pipeline publishes a result while holding its own gate, and the
        // publish takes the lock, so pushing into the pipeline with the lock held can deadlock.
        entry.RefreshRequests.OnNext(Unit.Default);
        return pending.Task;
    }

    /// <summary>Must be called with <see cref="Lock"/> held.</summary>
    private LoadoutDiagnostics GetOrCreate(LoadoutId loadoutId)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, typeof(DiagnosticManager));
        if (_loadouts.TryGetValue(loadoutId, out var existing)) return existing;

        var refreshRequests = new R3.Subject<Unit>();
        var runsStarted = new StrongBox<int>();
        var results = LoadoutQueries2.RevisionsWithChildUpdates(_connection, loadoutId)
            .ToObservable()
            .Debounce(TimeSpan.FromMilliseconds(250))
            .Select(_ => Unit.Default)
            .Merge(refreshRequests)
            .Select(_ => Interlocked.Increment(ref runsStarted.Value))
            .SelectAwait(async (run, cancellationToken) => (Run: run, Diagnostics: await DiagnoseLoadout(loadoutId, cancellationToken)), AwaitOperation.Switch)
            .Replay(bufferSize: 1);

        var entry = new LoadoutDiagnostics(results.Select(result => result.Diagnostics).AsSystemObservable(), refreshRequests, runsStarted);

        // Subscribed ahead of every outside subscriber, and the replay stores a value before it notifies,
        // so a refresh completes only once its result can be read back from GetLoadoutDiagnostics. Only a
        // run that started after the refresh was asked for counts; a run that a newer one cancelled never
        // publishes, which leaves the refresh waiting for the newer run.
        _compositeDisposable.Add(results.Subscribe(result =>
        {
            lock (Lock)
            {
                if (entry.PendingRefresh is not { } pending || result.Run <= entry.PendingRefreshAfterRun) return;
                pending.TrySetResult();
                entry.PendingRefresh = null;
            }
        }));

        // Safe under the lock only because the first revision goes through the debounce timer, so nothing
        // is published from inside Connect.
        _compositeDisposable.Add(results.Connect());
        _compositeDisposable.Add(refreshRequests);
        _loadouts.Add(loadoutId, entry);
        return entry;
    }

    private async ValueTask<Diagnostic[]> DiagnoseLoadout(LoadoutId loadoutId, CancellationToken cancellationToken)
    {
        var db = _connection.Db;
        var loadout = Loadout.Load(db, loadoutId);
        if (!loadout.IsValid()) return [];

        try
        {
            return await GetLoadoutDiagnostics(loadout, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Exception while diagnosing loadout {Loadout}", loadout.Name);
            return [];
        }
    }

    private sealed class LoadoutDiagnostics(IObservable<Diagnostic[]> diagnostics, R3.Subject<Unit> refreshRequests, StrongBox<int> runsStarted)
    {
        public IObservable<Diagnostic[]> Diagnostics { get; } = diagnostics;
        public R3.Subject<Unit> RefreshRequests { get; } = refreshRequests;

        /// <summary>Numbers each run as it starts, so a refresh can tell a run it asked for from one already under way.</summary>
        public StrongBox<int> RunsStarted { get; } = runsStarted;

        /// <summary>The refresh whose result has not been published yet; guarded by <see cref="DiagnosticManager.Lock"/>.</summary>
        public TaskCompletionSource? PendingRefresh { get; set; }

        /// <summary>The last run that had started when <see cref="PendingRefresh"/> was asked for; guarded by <see cref="DiagnosticManager.Lock"/>.</summary>
        public int PendingRefreshAfterRun { get; set; }
    }

    private async Task<Diagnostic[]> GetLoadoutDiagnostics(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        var diagnosticEmitters = loadout.InstallationInstance.GetGame().DiagnosticEmitters.Concat(_sharedEmitters);
        
        var db = _connection.Db;
        var synchronizer = loadout.InstallationInstance.GetGame().Synchronizer;
        var metaData = loadout.Installation;
        var hasPreviousLoadout = GameInstallMetadata.LastSyncedLoadoutTransaction.TryGetValue(metaData, out var lastId);

        var lastScannedDiskState = await Task.Run(() => synchronizer.GetLastScannedDiskState(metaData), cancellationToken);
        
        var previousDiskState = hasPreviousLoadout ?
            await Task.Run(() => synchronizer.GetPreviouslyAppliedDiskState(metaData), cancellationToken) : 
            lastScannedDiskState;
        
        var baseSyncTree = synchronizer.BuildSyncTree(lastScannedDiskState, previousDiskState, loadout);
        synchronizer.ProcessSyncTree(baseSyncTree);
        var syncTree = baseSyncTree.ToFrozenDictionary();
        
        try
        {
            List<Diagnostic> diagnostics = [];
            
            await Parallel.ForEachAsync(diagnosticEmitters.OfType<ILoadoutDiagnosticEmitter>(), cancellationToken, async (emitter, token) =>
            {
                var start = DateTimeOffset.UtcNow;

                try
                {
                    await foreach (var diagnostic in emitter.Diagnose(loadout, syncTree, token))
                    {
                        lock(diagnostics)
                        {
                            diagnostics.Add(diagnostic);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // ignore
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Exception in emitter {Emitter}", emitter.GetType());
                }
                finally
                {
                    var end = DateTimeOffset.UtcNow;
                    var duration = end - start;
                    _logger.LogTrace("Emitter {Emitter} took {Duration} ms", emitter.GetType(), duration.TotalMilliseconds);
                }
            });

            if (cancellationToken.IsCancellationRequested) return [];

            var flattened = diagnostics
                .OrderByDescending(x => x.Severity)
                .ThenBy(x => x.Id)
                .ToArray();

            return flattened;
        }
        catch (TaskCanceledException)
        {
            // ignore
            return [];
        }
    }

    public IObservable<(int NumSuggestions, int NumWarnings, int NumCritical)> CountDiagnostics(LoadoutId loadoutId)
    {
        return GetLoadoutDiagnostics(loadoutId)
            .Select(diagnostics =>
            {
                int numSuggestions = 0, numWarnings = 0, numCritical = 0;
                foreach (var diagnostic in diagnostics)
                {
                    switch (diagnostic.Severity)
                    {
                        case DiagnosticSeverity.Suggestion:
                            numSuggestions += 1;
                            break;
                        case DiagnosticSeverity.Warning:
                            numWarnings += 1;
                            break;
                        case DiagnosticSeverity.Critical:
                            numCritical += 1;
                            break;
                        default: break;
                    }
                }

                return (numSuggestions, numWarnings, numCritical);
            });
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        lock (Lock)
        {
            // Nothing will publish again, so a caller still waiting on a refresh would wait forever.
            foreach (var entry in _loadouts.Values) entry.PendingRefresh?.TrySetCanceled();
            _compositeDisposable.Dispose();
        }
    }
}
