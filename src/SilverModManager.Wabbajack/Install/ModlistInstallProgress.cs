using System.Reactive.Subjects;
using NexusMods.Paths;

namespace SilverModManager.Wabbajack.Install;

public enum InstallPhase { Check, Resolve, Write, Order }

/// <summary>
/// How far an <see cref="InstallModlistJob"/> has got. The job updates it from whichever thread it runs on; observers
/// of <see cref="Changes"/> see every snapshot in the order it was produced.
/// </summary>
public sealed class ModlistInstallProgress
{
    private readonly object _lock = new();
    private readonly BehaviorSubject<Snapshot> _subject = new(Snapshot.Empty);
    // Every archive being built, by its number, with when it last reported. Several build at once, so the snapshot's
    // single Archive slot is only the one that reported last; this is where the others keep their counts.
    private readonly Dictionary<int, (ArchiveBuild Build, long Touched)> _building = new();
    private long _touches;

    /// <summary>
    /// A Bethesda archive the Write phase is building. The archive is one item of the phase however long it takes, so
    /// this carries its own count of the files packed into it, which moves while the item is still unfinished.
    /// </summary>
    /// <param name="Number">Which archive this is of <paramref name="Count"/>, in the order the builds started. It is also the key the model tracks the build by, so two archives with one file name stay apart.</param>
    public sealed record ArchiveBuild(string Name, int Number, int Count, int DoneFiles, int TotalFiles)
    {
        /// <summary>The line the page and the log show for this build.</summary>
        public string Describe(IFormatProvider culture)
            => string.Create(culture, $"Building archive {Number} of {Count}: {Name} ({DoneFiles:N0} of {TotalFiles:N0} files)");
    }

    /// <param name="Archive">The archive being built, or null when none is; with several building at once, the one that reported last.</param>
    /// <param name="ArchiveItems">How much of the phase's items the archives still being built amount to, from the files packed so far; each is less than one because the archive's last step follows its last file. An archive's item is counted by <see cref="Advance"/> when the in-order writer records it, which can be after the archive ends.</param>
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
        bool IsEstimateLowerBound,
        ArchiveBuild? Archive = null,
        double ArchiveItems = 0)
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

    public void BeginArchive(string name, int number, int count, int totalFiles)
        => ChangeArchives(() => _building[number] = (new ArchiveBuild(name, number, count, 0, totalFiles), ++_touches));

    /// <summary>Counts one more file packed into archive <paramref name="number"/>, which makes it the one the snapshot shows.</summary>
    public void AdvanceArchive(int number)
        => ChangeArchives(() =>
        {
            if (_building.TryGetValue(number, out var running))
                _building[number] = (running.Build with { DoneFiles = running.Build.DoneFiles + 1 }, ++_touches);
        });

    public void EndArchive(int number) => ChangeArchives(() => _building.Remove(number));

    // The slot and the partial items are worked out from every running build each time, so one ending can never
    // hide another.
    private void ChangeArchives(Action change)
    {
        lock (_lock)
        {
            change();
            var shown = _building.Count == 0 ? null : _building.Values.MaxBy(b => b.Touched).Build;
            var items = _building.Values.Sum(b => b.Build.TotalFiles <= 0 ? 0 : Math.Min((double)b.Build.DoneFiles / b.Build.TotalFiles, MostOfAnItem));
            _subject.OnNext(_subject.Value with { Archive = shown, ArchiveItems = items });
        }
    }

    private const double MostOfAnItem = 0.99;

    private void Update(Func<Snapshot, Snapshot> change)
    {
        lock (_lock) _subject.OnNext(change(_subject.Value));
    }
}
