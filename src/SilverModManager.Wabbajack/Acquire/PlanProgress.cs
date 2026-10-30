using System.Diagnostics;

namespace SilverModManager.Wabbajack.Acquire;

/// <summary>The stage of checking a list that <see cref="PlanProgress"/> reports on.</summary>
public enum PlanPhase
{
    /// <summary>Unpacking the list's own file. It has no count.</summary>
    ReadingList,

    /// <summary>Matching the list's archives to the library. Counted in archives.</summary>
    CheckingLibrary,

    /// <summary>Hashing the game files the list takes as sources. Counted in bytes.</summary>
    HashingGameFiles,

    /// <summary>Looking for the list's files in the game folder. Counted in files.</summary>
    CheckingGameFolder,
}

/// <summary>
/// How far a check of a list has got. The unit of <paramref name="Done"/> and <paramref name="Total"/> depends on
/// <paramref name="Phase"/>; a <paramref name="Total"/> of zero means the phase has no count to show.
/// </summary>
/// <param name="Item">What is being worked on right now, such as the game file being hashed, or null.</param>
public sealed record PlanProgress(PlanPhase Phase, long Done = 0, long Total = 0, string? Item = null);

/// <summary>
/// Passes progress on at most every <see cref="Interval"/>, so a loop over thousands of archives, or a read in
/// megabyte blocks, does not drown the page. A phase change and the last report of a phase always go through.
/// Reports may come from several threads at once; one that arrives with less done than a report already sent in the
/// same phase is dropped, so the page never sees a bar step back.
/// </summary>
public sealed class PlanProgressGate(IProgress<PlanProgress>? target)
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    private readonly object _gate = new();
    private long _lastReport;
    private long _lastDone;
    private PlanPhase? _lastPhase;

    public void Report(PlanProgress progress)
    {
        if (target is null) return;

        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            var samePhase = _lastPhase == progress.Phase;
            if (samePhase && progress.Done < _lastDone) return;

            var finished = progress.Total > 0 && progress.Done >= progress.Total;
            if (samePhase && !finished && Stopwatch.GetElapsedTime(_lastReport, now) < Interval) return;

            _lastPhase = progress.Phase;
            _lastReport = now;
            _lastDone = progress.Done;
            target.Report(progress);
        }
    }
}
