using Microsoft.Extensions.Logging;
using R3;
using NexusMods.Paths;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Native;
using SilverModManager.Sdk.Settings;
using SilverModManager.Wabbajack.Library;

namespace SilverModManager.Wabbajack.Acquire;

/// <summary>
/// Notices a browser download the plan is waiting for in the user's Downloads folder and hands it to the inbox. It is
/// strictly opt-in (<see cref="WabbajackSettings.PickUpFromDownloads"/>) and looks only at file names the current plan
/// expects: nothing else in the folder is stated, opened or hashed, and nothing in it is ever deleted, renamed or
/// written. The user's file stays where the browser put it.
/// </summary>
public sealed class DownloadsPickup(
    ISettingsManager settings,
    WabbajackPaths paths,
    IArchiveAcquirer acquirer,
    ArchiveInbox inbox,
    IFileSystem fileSystem,
    ILogger<DownloadsPickup> logger) : IDisposable
{
    private readonly SemaphoreSlim _scanning = new(1);
    private readonly Dictionary<string, (long Size, DateTime Modified)> _mismatches = new(StringComparer.Ordinal);
    private long _mismatchEpoch;
    private readonly Lock _watchLock = new();
    private Timer? _rescan;
    private bool _missingFolderLogged;
    private FileSystemWatcher? _watcher;
    private AbsolutePath? _watchedFolder;
    private IDisposable? _settingsSubscription;
    private HashSet<string> _watchedNames = [];
    private bool _following;
    private bool _disposed;

    /// <summary>How long a file has to be unchanged before it counts as finished, and the wait before a re-scan.</summary>
    public TimeSpan DebounceInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Whether a watcher is on the Downloads folder right now.</summary>
    public bool IsWatching
    {
        get
        {
            lock (_watchLock) return _watcher is not null;
        }
    }

    /// <summary>The folder the watcher is on, or null when nothing is watched.</summary>
    public AbsolutePath? WatchedFolder
    {
        get
        {
            lock (_watchLock) return _watcher is null ? null : _watchedFolder;
        }
    }

    /// <summary>How a file is linked into the inbox; tests swap it to reach the copy fallback.</summary>
    internal Func<string, string, int> Link { get; set; } = LibcLinks.TryLink;

    /// <summary>The plain-file check (a link or an in-progress name is not one); tests wrap it to see which paths it is asked about.</summary>
    internal Func<AbsolutePath, bool> IsPlainFile { get; set; } = ArchiveInbox.IsPlainFile;

    /// <summary>How a matched file reaches the inbox; tests swap it to make staging fail.</summary>
    internal Func<AbsolutePath, AbsolutePath, Func<string, string, int>, CancellationToken, Task> Stage { get; set; }
        = (source, inbox, link, token) => ArchiveInbox.Stage(source, inbox, link, token);

    /// <summary>The folder a scan reads: the configured one, or the XDG download folder when none is set.</summary>
    public AbsolutePath Folder
    {
        get
        {
            var configured = settings.Get<WabbajackSettings>().DownloadsFolder;
            return string.IsNullOrWhiteSpace(configured)
                ? XdgUserDirs.Download(fileSystem).WithFileSystem(fileSystem)
                : fileSystem.FromUnsanitizedFullPath(configured).WithFileSystem(fileSystem);
        }
    }

    /// <summary>
    /// Looks through the Downloads folder for files the plan expects and credits the ones that match. Each step
    /// narrows what the next may touch: name, then plain file, then size, then a quiet mtime, and only then are the
    /// bytes read.
    /// </summary>
    public async Task Scan(CancellationToken cancellationToken)
    {
        await _scanning.WaitAsync(cancellationToken);
        try
        {
            if (!settings.Get<WabbajackSettings>().PickUpFromDownloads) return;

            var expected = ExpectedFiles();
            if (expected.Count == 0) return;

            var folder = Folder;
            if (!FolderExists(folder)) return;

            var epoch = MismatchEpoch();
            var inboxFolder = paths.Inbox;
            var staged = false;
            var waiting = false;
            foreach (var entry in folder.EnumerateFiles(recursive: false).ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = entry.WithFileSystem(fileSystem);
                if (!expected.TryGetValue(file.FileName, out var candidates)) continue;

                Verdict verdict;
                try
                {
                    verdict = await Check(file, candidates, epoch, cancellationToken);
                }
                catch (IOException e)
                {
                    // Still being written or briefly locked: look again shortly.
                    logger.LogDebug(e, "Could not read '{File}' in the Downloads folder, it is probably still being written", file.FileName);
                    waiting = true;
                    continue;
                }

                switch (verdict.Outcome)
                {
                    case Outcome.Match:
                        try
                        {
                            await Stage(file, inboxFolder, Link, cancellationToken);
                            staged = true;
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException or UserFacingException)
                        {
                            // Retrying would hash and copy the same bytes again; wait for the file, or the plan, to change.
                            logger.LogWarning(e, "Could not copy '{File}' from the Downloads folder into the inbox. It will be tried again if the file changes", file.FileName);
                            Remember(file.FileName, verdict.Size, verdict.Modified, epoch);
                        }
                        break;
                    case Outcome.Unsettled:
                        waiting = true;
                        break;
                }
            }

            if (staged) await inbox.Scan(cancellationToken);
            if (waiting) ScheduleRescan();
        }
        finally
        {
            _scanning.Release();
        }
    }

    /// <summary>
    /// Starts watching the Downloads folder for as long as the pickup is on and a row waits for the user. Idempotent.
    /// Without a modlist page or run the acquirer holds no rows, so the watch ends with the last page.
    /// </summary>
    public void Follow()
    {
        lock (_watchLock)
        {
            if (_following || _disposed) return;
            _following = true;
            acquirer.PlanChanged += OnPlanChanged;
            acquirer.PlanForgotten += Reevaluate;
            _settingsSubscription = settings.GetChanges<WabbajackSettings>().Subscribe(_ => Reevaluate());
        }
        Reevaluate();
    }

    private void OnPlanChanged(ArchivePlan _) => Reevaluate();

    private void Reevaluate()
    {
        var startedNow = false;
        lock (_watchLock)
        {
            if (_disposed) return;

            var pending = settings.Get<WabbajackSettings>().PickUpFromDownloads
                ? acquirer.Plan.Rows.Where(row => row.State.WaitsForUser()).Select(row => row.Archive.Name).ToHashSet(StringComparer.Ordinal)
                : [];
            if (pending.Count == 0)
            {
                StopWatching();
                return;
            }

            var folder = Folder;
            // A plan changes after every finished archive; the watcher only moves when the chosen folder did.
            if (_watcher is not null)
            {
                if (_watchedFolder == folder)
                {
                    _watchedNames = pending;
                    return;
                }
                StopWatching();
                _missingFolderLogged = false;
            }

            _watchedNames = pending;
            if (!FolderExists(folder)) return;
            _watcher = StartWatcher(folder);
            _watchedFolder = _watcher is null ? null : folder;
            startedNow = _watcher is not null;
        }

        // One look for what landed before the watch began.
        if (startedNow) _ = RescanQuietly();
    }

    private FileSystemWatcher? StartWatcher(AbsolutePath folder)
    {
        try
        {
            var watcher = new FileSystemWatcher(folder.ToString())
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Created += (_, e) => OnFileEvent(e.Name);
            watcher.Changed += (_, e) => OnFileEvent(e.Name);
            watcher.Renamed += (_, e) => OnFileEvent(e.Name);
            watcher.Error += (_, e) => logger.LogWarning(e.GetException(), "Watching the Downloads folder '{Folder}' failed", folder);
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Could not watch the Downloads folder '{Folder}'", folder);
            return null;
        }
    }

    // A busy folder raises events for files the plan never asked for; those cost one name comparison.
    private void OnFileEvent(string? name)
    {
        if (name is null) return;
        bool expected;
        lock (_watchLock) expected = _watcher is not null && _watchedNames.Contains(name);
        if (expected) ScheduleRescan();
    }

    private void StopWatching()
    {
        if (_watcher is null) return;
        _watcher.Dispose();
        _watcher = null;
        _watchedFolder = null;
        _watchedNames = [];
        _rescan?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        ForgetMismatches();
    }

    private bool FolderExists(AbsolutePath folder)
    {
        lock (_watchLock)
        {
            if (folder.DirectoryExists())
            {
                _missingFolderLogged = false;
                return true;
            }
            if (!_missingFolderLogged) logger.LogInformation("The Downloads folder '{Folder}' does not exist, so nothing is picked up from it", folder);
            _missingFolderLogged = true;
            return false;
        }
    }

    /// <summary>Forgets which files were already set aside (wrong bytes, unreadable, or not copyable), so the next scan reads them afresh.</summary>
    internal void ForgetMismatches()
    {
        lock (_mismatches)
        {
            _mismatches.Clear();
            _mismatchEpoch++;
        }
    }

    private enum Outcome { Skip, Unsettled, Match }

    private readonly record struct Verdict(Outcome Outcome, long Size = 0, DateTime Modified = default);

    private long MismatchEpoch()
    {
        lock (_mismatches) return _mismatchEpoch;
    }

    // A scan that began before the memory was cleared (the watch ended while it was still reading) must not write its
    // verdict into the next watch's memory: that scan's view of the folder is from the old watch.
    private void Remember(string name, long size, DateTime modified, long epoch)
    {
        lock (_mismatches)
        {
            if (epoch == _mismatchEpoch) _mismatches[name] = (size, modified);
        }
    }

    private async Task<Verdict> Check(AbsolutePath file, List<(long Size, FileIdentity Hash)> candidates, long epoch, CancellationToken cancellationToken)
    {
        if (!IsPlainFile(file)) return new Verdict(Outcome.Skip);

        var info = file.FileSystem.GetFileEntry(file);
        var size = (long)info.Size.Value;
        var sized = candidates.Where(c => c.Size == size).ToArray();
        if (sized.Length == 0) return new Verdict(Outcome.Skip);

        var modified = info.LastWriteTimeUtc;
        if (DateTime.UtcNow - modified < DebounceInterval) return new Verdict(Outcome.Unsettled);

        lock (_mismatches)
        {
            if (_mismatches.TryGetValue(file.FileName, out var seen) && seen == (size, modified)) return new Verdict(Outcome.Skip);
        }

        FileIdentity hash;
        try
        {
            hash = FileIdentity.XxHash64(await FileHashing.XxHash64(file, cancellationToken));
        }
        catch (UnauthorizedAccessException e)
        {
            // Not something waiting will fix, and it must not stop the other candidates.
            logger.LogWarning(e, "'{File}' in the Downloads folder cannot be read, so it is skipped until it changes", file.FileName);
            Remember(file.FileName, size, modified, epoch);
            return new Verdict(Outcome.Skip);
        }

        if (sized.Any(c => c.Hash.Equals(hash))) return new Verdict(Outcome.Match, size, modified);

        logger.LogDebug("'{File}' in the Downloads folder has the expected name and size but other contents", file.FileName);
        Remember(file.FileName, size, modified, epoch);
        return new Verdict(Outcome.Skip);
    }

    private Dictionary<string, List<(long Size, FileIdentity Hash)>> ExpectedFiles()
    {
        var expected = new Dictionary<string, List<(long Size, FileIdentity Hash)>>(StringComparer.Ordinal);
        foreach (var row in acquirer.Plan.Rows)
        {
            if (!row.State.WaitsForUser()) continue;
            if (!expected.TryGetValue(row.Archive.Name, out var list)) expected[row.Archive.Name] = list = [];
            list.Add((row.Archive.Size, row.Archive.XxHash64));
        }
        return expected;
    }

    private void ScheduleRescan()
    {
        _rescan ??= new Timer(_ => _ = RescanQuietly());
        _rescan.Change(DebounceInterval, Timeout.InfiniteTimeSpan);
    }

    private async Task RescanQuietly()
    {
        try
        {
            await Scan(CancellationToken.None);
        }
        catch (ObjectDisposedException)
        {
            // Shut down while the timer was firing.
        }
        catch (Exception e)
        {
            logger.LogError(e, "Scanning the Downloads folder failed");
        }
    }

    public void Dispose()
    {
        lock (_watchLock)
        {
            if (_disposed) return;
            _disposed = true;
            acquirer.PlanChanged -= OnPlanChanged;
            acquirer.PlanForgotten -= Reevaluate;
            _settingsSubscription?.Dispose();
            StopWatching();
        }
        _rescan?.Dispose();
        _scanning.Dispose();
    }
}
