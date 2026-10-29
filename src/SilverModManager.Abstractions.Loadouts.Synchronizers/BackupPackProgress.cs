using Microsoft.Extensions.Logging;
using NexusMods.Paths;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// Turns the file store's 0 to 1 progress while it packs a backup archive into the bytes the apply reports. Files the
/// store already held count as done from the start (<c>bytesAccountedFor</c>), so the packed share fills only the
/// rest of the bar and the bar never goes backwards.
/// </summary>
internal sealed class BackupPackProgress(
    SynchronizeLoadoutJob? job,
    ILogger logger,
    long totalFiles,
    long totalBytes,
    long bytesAccountedFor,
    ProgressLogGate? logGate = null) : IProgress<double>
{
    private readonly ProgressLogGate _logGate = logGate ?? new ProgressLogGate();
    private readonly object _sync = new();
    private long _bytesDone = bytesAccountedFor;

    /// <inheritdoc />
    public void Report(double fraction)
    {
        long bytes;
        lock (_sync)
        {
            var packed = (long)(Math.Clamp(fraction, 0, 1) * (totalBytes - bytesAccountedFor));
            bytes = _bytesDone = Math.Max(_bytesDone, bytesAccountedFor + packed);
            job?.ReportProgress(SyncPhases.BackingUp, totalFiles, totalFiles, bytes, totalBytes);
        }

        if (_logGate.TryTake())
        {
            logger.LogInformation("Archived {Done} of {Size} of the files to back up",
                BackupOverflow.FormatSize(Size.FromLong(bytes)), BackupOverflow.FormatSize(Size.FromLong(totalBytes)));
        }
    }
}
