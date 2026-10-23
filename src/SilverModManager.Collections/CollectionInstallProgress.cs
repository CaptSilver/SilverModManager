using System.Reactive.Subjects;

namespace NexusMods.Collections;

/// <summary>
/// What an <see cref="InstallCollectionJob"/> has done so far. The job updates it from several
/// threads at once; observers always see snapshots in the order they were produced.
/// </summary>
public sealed class CollectionInstallProgress
{
    private readonly object _lock = new();
    private readonly BehaviorSubject<Snapshot> _subject = new(Snapshot.Empty);
    private readonly List<string> _installing = [];
    private readonly List<FailedMod> _failed = [];
    private int _total;
    private int _alreadyInstalled;
    private int _installed;
    private bool _isFinished;
    private bool _leftDisabled;

    /// <summary>
    /// A mod that could not be installed.
    /// </summary>
    public record FailedMod(string Name, string Reason);

    /// <summary>
    /// The state at one point in time.
    /// </summary>
    /// <param name="Total">Mods this run installs, after leaving out the already installed ones.</param>
    /// <param name="AlreadyInstalled">Mods of the collection that were installed before this run.</param>
    /// <param name="Installed">Mods this run has installed so far.</param>
    /// <param name="Failed">Mods that failed, with the reason.</param>
    /// <param name="Installing">Names of the mods being installed right now.</param>
    /// <param name="IsFinished">The job is done, whether or not mods failed.</param>
    /// <param name="LeftDisabled">The collection was left disabled because required mods are missing.</param>
    public record Snapshot(
        int Total,
        int AlreadyInstalled,
        int Installed,
        IReadOnlyList<FailedMod> Failed,
        IReadOnlyList<string> Installing,
        bool IsFinished,
        bool LeftDisabled)
    {
        /// <summary>
        /// Nothing has happened yet.
        /// </summary>
        public static readonly Snapshot Empty = new(0, 0, 0, [], [], IsFinished: false, LeftDisabled: false);
    }

    /// <summary>
    /// The latest snapshot.
    /// </summary>
    public Snapshot Current => _subject.Value;

    /// <summary>
    /// Emits the current snapshot on subscription and a new one on every change.
    /// </summary>
    public IObservable<Snapshot> Changes => _subject;

    internal void Begin(int total, int alreadyInstalled) => Update(() =>
    {
        _total = total;
        _alreadyInstalled = alreadyInstalled;
    });

    internal void ModStarted(string name) => Update(() => _installing.Add(name));

    internal void ModInstalled(string name) => Update(() =>
    {
        _installing.Remove(name);
        _installed++;
    });

    internal void ModFailed(string name, string reason) => Update(() =>
    {
        _installing.Remove(name);
        _failed.Add(new FailedMod(name, reason));
    });

    internal void Finish(bool leftDisabled) => Update(() =>
    {
        _isFinished = true;
        _leftDisabled = leftDisabled;
        _installing.Clear();
    });

    private void Update(Action change)
    {
        // Emitting under the lock keeps the snapshots in the order the changes happened.
        lock (_lock)
        {
            change();
            _subject.OnNext(new Snapshot(_total, _alreadyInstalled, _installed, _failed.ToArray(), _installing.ToArray(), _isFinished, _leftDisabled));
        }
    }
}
