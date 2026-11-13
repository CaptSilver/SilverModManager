using System.Reactive.Subjects;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Sdk.Loadouts.Install;

namespace SilverModManager.Collections;

/// <summary>
/// What an <see cref="InstallCollectionJob"/> has done so far. The job updates it from several
/// threads at once; observers always see snapshots in the order they were produced.
/// </summary>
public sealed class CollectionInstallProgress
{
    private readonly object _lock = new();
    private readonly BehaviorSubject<Snapshot> _subject = new(Snapshot.Empty);
    private readonly ModInstallRows _mods = new();
    private int _total;
    private int _alreadyInstalled;
    private int _installed;
    private int _failed;
    private int _rowsInstalled;
    private int _rowsFailed;
    private bool _isFinished;
    private bool _leftDisabled;

    /// <summary>
    /// The counts at one point in time. The mods themselves are <see cref="Mods"/>: a snapshot is copied on every change,
    /// which is fine for a few numbers and not for a row per mod.
    /// </summary>
    /// <param name="Total">Mods this run installs, after leaving out the already installed ones.</param>
    /// <param name="AlreadyInstalled">Mods of the collection that were installed before this run.</param>
    /// <param name="Installed">Mods this run has installed so far.</param>
    /// <param name="Failed">Mods that failed.</param>
    /// <param name="IsFinished">The job is done, whether or not mods failed.</param>
    /// <param name="LeftDisabled">The collection was left disabled because required mods are missing.</param>
    public record Snapshot(
        int Total,
        int AlreadyInstalled,
        int Installed,
        int Failed,
        bool IsFinished,
        bool LeftDisabled)
    {
        /// <summary>
        /// Nothing has happened yet.
        /// </summary>
        public static readonly Snapshot Empty = new(0, 0, 0, 0, IsFinished: false, LeftDisabled: false);
    }

    /// <summary>
    /// The latest snapshot.
    /// </summary>
    public Snapshot Current => _subject.Value;

    /// <summary>
    /// Emits the current snapshot on subscription and a new one on every change.
    /// </summary>
    public IObservable<Snapshot> Changes => _subject;

    /// <summary>
    /// One row per mod of the collection the job was given, including the ones installed before this run. Rows are keyed
    /// by the mod's index in the collection, since two mods can share a name.
    /// </summary>
    public IModInstallRows Mods => _mods;

    // The concrete set, for the job's own tests to read rows back from.
    internal ModInstallRows Rows => _mods;

    internal void Begin(int total, int alreadyInstalled, IEnumerable<(int Index, string Name, bool AlreadyInstalled)> mods)
    {
        lock (_lock)
        {
            _total = total;
            _alreadyInstalled = alreadyInstalled;
            var rows = mods.Select(static mod => new ModInstallRow(
                mod.Index.ToString(),
                mod.Name,
                mod.Index,
                new ModInstallRowState(mod.AlreadyInstalled ? ModInstallState.Installed : ModInstallState.Waiting, mod.AlreadyInstalled ? 1 : 0, mod.AlreadyInstalled ? 1 : 0, 1))).ToArray();
            _rowsInstalled = rows.Count(static row => row.Current.State == ModInstallState.Installed);
            _mods.Add(rows);
            Publish();
        }
    }

    internal void ModStarted(int index) => Update(index, ModInstallState.Installing, reason: null);

    internal void ModInstalled(int index) => Update(index, ModInstallState.Installed, reason: null, onChange: () => _installed++);

    internal void ModFailed(int index, string reason) => Update(index, ModInstallState.Failed, reason, onChange: () => _failed++);

    internal void ModNeedsReview(int index, ReviewKind kind, string reason) => Update(index, ModInstallState.NeedsUser, reason);

    internal void Finish(bool leftDisabled)
    {
        lock (_lock)
        {
            _isFinished = true;
            _leftDisabled = leftDisabled;
            // A run that was cancelled leaves mods half way; they did not install, so they wait for the next run.
            foreach (var row in _mods.Snapshot().Where(static row => row.Current.State == ModInstallState.Installing))
                row.Set(row.Current with { State = ModInstallState.Waiting, Resolved = 0, Written = 0 });
            Publish();
        }
    }

    private void Update(int index, ModInstallState state, string? reason, Action? onChange = null)
    {
        // Emitting under the lock keeps the snapshots in the order the changes happened.
        lock (_lock)
        {
            if (!_mods.TryGet(index.ToString(), out var row)) return;
            if (row.Current.State == ModInstallState.Installed) _rowsInstalled--;
            if (row.Current.State == ModInstallState.Failed) _rowsFailed--;
            if (state == ModInstallState.Installed) _rowsInstalled++;
            if (state == ModInstallState.Failed) _rowsFailed++;
            var done = state == ModInstallState.Installed ? 1 : 0;
            row.Set(row.Current with { State = state, Resolved = done, Written = done, Reason = reason });
            onChange?.Invoke();
            Publish();
        }
    }

    private void Publish()
    {
        _mods.SetTotals(new ModInstallTotals(_mods.Count, _rowsInstalled, _rowsFailed));
        _subject.OnNext(new Snapshot(_total, _alreadyInstalled, _installed, _failed, _isFinished, _leftDisabled));
    }
}
