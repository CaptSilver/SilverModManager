using Microsoft.Extensions.Logging;
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
    private Timer? _rescan;
    private bool _missingFolderLogged;

    /// <summary>How long a file has to be unchanged before it counts as finished, and the wait before a re-scan.</summary>
    public TimeSpan DebounceInterval { get; set; } = TimeSpan.FromSeconds(2);

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
            if (!folder.DirectoryExists())
            {
                if (!_missingFolderLogged) logger.LogInformation("The Downloads folder '{Folder}' does not exist, so nothing is picked up from it", folder);
                _missingFolderLogged = true;
                return;
            }
            _missingFolderLogged = false;

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
                    verdict = await Check(file, candidates, cancellationToken);
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
                            Remember(file.FileName, verdict.Size, verdict.Modified);
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

    /// <summary>Forgets which files were already set aside (wrong bytes, unreadable, or not copyable), so the next scan reads them afresh.</summary>
    internal void ForgetMismatches()
    {
        lock (_mismatches) _mismatches.Clear();
    }

    private enum Outcome { Skip, Unsettled, Match }

    private readonly record struct Verdict(Outcome Outcome, long Size = 0, DateTime Modified = default);

    private void Remember(string name, long size, DateTime modified)
    {
        lock (_mismatches) _mismatches[name] = (size, modified);
    }

    private async Task<Verdict> Check(AbsolutePath file, List<(long Size, FileIdentity Hash)> candidates, CancellationToken cancellationToken)
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
            Remember(file.FileName, size, modified);
            return new Verdict(Outcome.Skip);
        }

        if (sized.Any(c => c.Hash.Equals(hash))) return new Verdict(Outcome.Match, size, modified);

        logger.LogDebug("'{File}' in the Downloads folder has the expected name and size but other contents", file.FileName);
        Remember(file.FileName, size, modified);
        return new Verdict(Outcome.Skip);
    }

    private Dictionary<string, List<(long Size, FileIdentity Hash)>> ExpectedFiles()
    {
        var expected = new Dictionary<string, List<(long Size, FileIdentity Hash)>>(StringComparer.Ordinal);
        foreach (var row in acquirer.Plan.Rows)
        {
            if (row.State is not (ArchiveState.Browser or ArchiveState.Manual)) continue;
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
        _rescan?.Dispose();
        _scanning.Dispose();
    }
}
