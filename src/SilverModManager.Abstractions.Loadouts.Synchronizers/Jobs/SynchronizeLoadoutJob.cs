using System.Diagnostics;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using R3;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// Synchronize the loadout with the game folder,
/// any changes in the game folder will be added to the loadout,
/// and any new changes in the loadout will be applied to the game folder.
/// </summary>
public record SynchronizeLoadoutJob(LoadoutId LoadoutId, BindableReactiveProperty<string> StatusMessage) : IJobDefinition<Unit>
{
    // Reports come from the parallel hashing and extraction workers; the UI only needs a few per second.
    private static readonly TimeSpan DefaultPublishInterval = TimeSpan.FromMilliseconds(100);

    // A reference holds the mutable throttle state: a field of the record itself would feed its generated equality.
    private readonly PublishGate _gate = new();

    /// <summary>
    /// The shortest time between two published in-progress reports. Tests set it to zero to observe every report
    /// instead of the thinned-out ones.
    /// </summary>
    internal TimeSpan PublishInterval
    {
        get => _gate.Interval;
        init => _gate.Interval = value;
    }

    public SynchronizeLoadoutJob(LoadoutId loadoutId) : this(loadoutId, new BindableReactiveProperty<string>()) {}

    /// <summary>
    /// Where the current phase stands.
    /// </summary>
    public BindableReactiveProperty<SyncProgress> Progress { get; } = new();

    /// <summary>
    /// Starts a phase that has no counts to report.
    /// </summary>
    public void SetStatus(string message)
    {
        lock (_gate) Publish(SyncProgress.Indeterminate(message), message);
    }

    /// <summary>
    /// Reports counts for a phase. A report that is behind the last one of the same phase (workers finish out
    /// of order) is dropped, and in-between reports are thinned out; the one that completes the phase always gets through.
    /// </summary>
    public void ReportProgress(string phase, long filesDone, long? filesTotal, long bytesDone = 0, long? bytesTotal = null)
    {
        var progress = new SyncProgress(phase, filesDone, filesTotal, bytesDone, bytesTotal);
        lock (_gate)
        {
            var current = Progress.Value;
            // A phase runs once per deployment root, each with its own totals; a new total starts a new run.
            var samePhase = current.Phase == phase && current.FilesTotal == filesTotal && current.BytesTotal == bytesTotal;
            if (samePhase && (filesDone < current.FilesDone || bytesDone < current.BytesDone)) return;

            // Bytes are the finer measure when known: a packer reports files N/N throughout and only its bytes move.
            var completes = bytesTotal.HasValue ? bytesDone >= bytesTotal : filesTotal.HasValue && filesDone >= filesTotal;
            var due = Stopwatch.GetElapsedTime(_gate.LastPublishedAt) >= _gate.Interval;
            if (samePhase && !completes && !due) return;

            Publish(progress, progress.Text);
        }
    }

    private void Publish(SyncProgress progress, string message)
    {
        _gate.LastPublishedAt = Stopwatch.GetTimestamp();
        try
        {
            Progress.Value = progress;
            StatusMessage.Value = message;
        }
        catch (Exception)
        {
            // ignored
        }
    }

    private sealed class PublishGate
    {
        public long LastPublishedAt = Stopwatch.GetTimestamp();
        public TimeSpan Interval = DefaultPublishInterval;
    }
}
