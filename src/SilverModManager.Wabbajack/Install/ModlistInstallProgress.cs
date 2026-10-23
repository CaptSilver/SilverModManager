using System.Reactive.Subjects;
using NexusMods.Paths;

namespace NexusMods.Wabbajack.Install;

public enum InstallPhase { Check, Resolve, Write, Order }

/// <summary>
/// How far an <see cref="InstallModlistJob"/> has got. The job updates it from whichever thread it runs on; observers
/// of <see cref="Changes"/> see every snapshot in the order it was produced.
/// </summary>
public sealed class ModlistInstallProgress
{
    private readonly object _lock = new();
    private readonly BehaviorSubject<Snapshot> _subject = new(Snapshot.Empty);

    /// <param name="DroppedCount">Paths the list has that the app never installs (Mod Organizer's own files, other profiles, seeds); they leave no skip row.</param>
    /// <param name="ExtraCount">Files of tool folders, which the loadout does not deploy.</param>
    /// <param name="EstimatedExtractedSize">What the archives need on disk once the library has extracted them.</param>
    /// <param name="IsEstimateLowerBound">The estimate counts an archive that is not in the library yet by its download size.</param>
    public sealed record Snapshot(
        InstallPhase Phase,
        int DoneItems,
        int TotalItems,
        long DoneBytes,
        long TotalBytes,
        int DroppedCount,
        int ExtraCount,
        Size EstimatedExtractedSize,
        bool IsEstimateLowerBound)
    {
        public static readonly Snapshot Empty = new(InstallPhase.Check, 0, 0, 0, 0, 0, 0, Size.From(0), false);
    }

    public Snapshot Current => _subject.Value;

    public IObservable<Snapshot> Changes => _subject;

    public InstallPhase Phase => Current.Phase;
    public int DoneItems => Current.DoneItems;
    public int TotalItems => Current.TotalItems;
    public long DoneBytes => Current.DoneBytes;
    public long TotalBytes => Current.TotalBytes;
    public int DroppedCount => Current.DroppedCount;
    public int ExtraCount => Current.ExtraCount;
    public Size EstimatedExtractedSize => Current.EstimatedExtractedSize;
    public bool IsEstimateLowerBound => Current.IsEstimateLowerBound;

    /// <summary>Starts <paramref name="phase"/> with its own totals; the done counts restart.</summary>
    public void Enter(InstallPhase phase, int totalItems, long totalBytes)
        => Update(s => s with { Phase = phase, DoneItems = 0, TotalItems = totalItems, DoneBytes = 0, TotalBytes = totalBytes });

    public void Advance(int items, long bytes)
        => Update(s => s with { DoneItems = s.DoneItems + items, DoneBytes = s.DoneBytes + bytes });

    public void AddDropped(int count) => Update(s => s with { DroppedCount = s.DroppedCount + count });

    public void AddExtras(int count) => Update(s => s with { ExtraCount = s.ExtraCount + count });

    public void SetEstimate(Size size, bool isLowerBound)
        => Update(s => s with { EstimatedExtractedSize = size, IsEstimateLowerBound = isLowerBound });

    private void Update(Func<Snapshot, Snapshot> change)
    {
        lock (_lock) _subject.OnNext(change(_subject.Value));
    }
}
