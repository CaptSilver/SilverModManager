using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// Progress of the read that gives every file about to be archived its xxHash3. It runs before anything else of the
/// apply and on a large game takes minutes, so it reports files and bytes done of total to the job and, every few
/// seconds, to the log.
/// </summary>
internal sealed class WholeReadProgress
{
    private readonly SynchronizeLoadoutJob? _job;
    private readonly ILogger _logger;
    private readonly ProgressLogGate _logGate;
    private readonly Dictionary<GamePath, long> _sizes;
    private readonly long _totalBytes;
    private readonly object _sync = new();
    private long _filesDone;
    private long _bytesDone;

    public WholeReadProgress(SynchronizeLoadoutJob? job, ILogger logger, Dictionary<GamePath, long> sizes, ProgressLogGate? logGate = null)
    {
        _job = job;
        _logger = logger;
        _sizes = sizes;
        _totalBytes = sizes.Values.Sum();
        _logGate = logGate ?? new ProgressLogGate();
    }

    /// <summary>Starts the phase at zero and logs what is about to be read.</summary>
    public void Begin()
    {
        _logger.LogInformation("Reading {Count} files ({Size}) to back up or add", _sizes.Count, BackupOverflow.FormatSize(Size.FromLong(_totalBytes)));
        _job?.ReportProgress(SyncPhases.ReadingBackups, 0, _sizes.Count, 0, _totalBytes);
    }

    /// <summary>Counts a file as read. Sizes are the ones the plan was made with, so the totals are met exactly.</summary>
    public void FileRead(GamePath path)
    {
        long files, bytes;
        lock (_sync)
        {
            files = ++_filesDone;
            bytes = _bytesDone += _sizes[path];
            _job?.ReportProgress(SyncPhases.ReadingBackups, files, _sizes.Count, bytes, _totalBytes);
        }

        if (_logGate.TryTake())
        {
            _logger.LogInformation("Read {Done} of {Count} files ({DoneSize} of {Size}) to back up or add",
                files, _sizes.Count, BackupOverflow.FormatSize(Size.FromLong(bytes)), BackupOverflow.FormatSize(Size.FromLong(_totalBytes)));
        }
    }
}
