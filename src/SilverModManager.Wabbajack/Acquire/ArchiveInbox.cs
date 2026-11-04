using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Library;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Native;
using SilverModManager.Wabbajack.Library;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Wabbajack.Acquire;

/// <summary>A file in the inbox that no archive of the current plan wants.</summary>
public sealed record UnmatchedInbox(string FileName, FileIdentity XxHash64);

/// <summary>
/// The folder the user drops archives into when the app cannot fetch them itself. Only the inbox folder from
/// <see cref="WabbajackSettings"/> is read here. The user's Downloads folder is looked at only by
/// <see cref="DownloadsPickup"/>, when the user turned that on, and it hands matching files to this inbox.
/// </summary>
public sealed class ArchiveInbox(
    WabbajackPaths paths,
    ILibraryService library,
    ILibraryArchiveLocator locator,
    IConnection connection,
    IArchiveAcquirer acquirer,
    ILogger<ArchiveInbox> logger) : IDisposable
{
    // What browsers leave behind while a download is still running.
    private static readonly HashSet<string> InProgressExtensions = new(StringComparer.OrdinalIgnoreCase) { ".part", ".crdownload", ".download", ".tmp" };

    private readonly SemaphoreSlim _scanning = new(1);
    // Only touched inside Scan, which _scanning serialises.
    private readonly Dictionary<string, ((long Length, DateTime WrittenUtc) Stamp, FileIdentity Hash)> _hashed = new();
    // Files this app put in the inbox itself, with the size and write time they had then. Only these are removed after a scan.
    private readonly ConcurrentDictionary<string, (long Length, DateTime WrittenUtc)> _staged = new();
    private readonly Lock _watchLock = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private bool _waitingForPlan;

    /// <summary>How long the folder has to be quiet before a change triggers a scan.</summary>
    public TimeSpan DebounceInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The files the last scan found that matched nothing.</summary>
    public IReadOnlyList<UnmatchedInbox> Unmatched { get; private set; } = [];

    /// <summary>
    /// Adds every file in the inbox to the library and checks the plan against it. A file the user dropped stays
    /// where it is, the ones the plan wanted included: the library holds its own copy. A file this app staged itself
    /// (<see cref="StageCopy"/>) is a third copy nobody asked for, so it is removed once the plan has it, unless it
    /// changed since. The files that matched nothing are returned.
    /// </summary>
    public async Task<IReadOnlyList<UnmatchedInbox>> Scan(CancellationToken cancellationToken)
    {
        await _scanning.WaitAsync(cancellationToken);
        try
        {
            var found = new List<(AbsolutePath Path, FileIdentity Hash)>();
            foreach (var file in paths.Inbox.EnumerateFiles(recursive: false).ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsPlainFile(file)) continue;

                try
                {
                    found.Add((file, await HashOf(file, cancellationToken)));
                }
                catch (IOException e)
                {
                    logger.LogWarning(e, "Could not read '{File}' in the inbox, it is probably still being written", file.FileName);
                }
            }

            // Taken before any add: a file the plan lists comes for that plan's list, and anything else is nobody's yet.
            var wanting = acquirer.Plan;
            var unreadable = new List<(AbsolutePath Path, FileIdentity Hash)>();
            var addedHere = new HashSet<FileIdentity>();
            foreach (var (file, hash) in found)
            {
                // A held archive that lacks what the list copies out of it is added again; the drop would otherwise be skipped as a duplicate.
                if (wanting.IsHeld(connection.Db, locator, hash)) continue;
                // Adding a file the library already holds completes that copy, which stays the user's own.
                var userHad = locator.FindTopLevelByXxHash64(connection.Db, hash) is not null;
                try
                {
                    await library.AddLocalFile(file, AcquisitionFor(wanting, hash));
                    if (!userHad) addedHere.Add(hash);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning(e, "'{File}' in the inbox could not be added to the library", file.FileName);
                    unreadable.Add((file, hash));
                }
            }

            acquirer.Recheck();

            var plan = acquirer.Plan;
            var unmatched = new List<UnmatchedInbox>();
            foreach (var (file, hash) in found)
            {
                var added = !unreadable.Contains((file, hash)) && locator.FindByXxHash64(connection.Db, hash) is not null;
                if (added && plan.FindArchive(hash) is not null)
                {
                    // Only a file this scan added came for the list; one the library already held was the user's.
                    if (plan.Modlist is not null && addedHere.Contains(hash) && locator.FindTopLevelByXxHash64(connection.Db, hash) is { } held)
                        await FetchedArchives.Mark(connection, hash, held);
                    RemoveIfStagedByUs(file);
                }
                else unmatched.Add(new UnmatchedInbox(file.FileName, hash));
            }

            Unmatched = unmatched;
            return unmatched;
        }
        finally
        {
            _scanning.Release();
        }
    }

    /// <summary>
    /// Files stay in the inbox after a scan, so the same ones are looked at again on every change in the folder. A file
    /// whose size and write time are as they were is not read again.
    /// </summary>
    private async Task<FileIdentity> HashOf(AbsolutePath file, CancellationToken cancellationToken)
    {
        var info = new FileInfo(file.ToString());
        var stamp = (info.Length, info.LastWriteTimeUtc);
        if (_hashed.TryGetValue(file.ToString(), out var known) && known.Stamp == stamp) return known.Hash;

        var hash = FileIdentity.XxHash64(await FileHashing.XxHash64(file, cancellationToken));
        _hashed[file.ToString()] = (stamp, hash);
        return hash;
    }

    private void RemoveIfStagedByUs(AbsolutePath file)
    {
        if (!_staged.TryGetValue(file.ToString(), out var stamp)) return;
        try
        {
            var info = new FileInfo(file.ToString());
            if (info.Exists && (info.Length, info.LastWriteTimeUtc) == stamp)
            {
                file.Delete();
                _hashed.Remove(file.ToString());
                logger.LogDebug("Removed the staged copy '{File}' from the inbox, the library holds it", file.FileName);
            }
            _staged.TryRemove(file.ToString(), out _);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Could not remove the staged copy '{File}' from the inbox", file.FileName);
        }
    }

    /// <summary>
    /// <see cref="Stage"/> into this inbox, remembering the file so a scan can remove it once the library has it.
    /// Use this for files the app copies in on the user's behalf; a file the user drops in by hand is never removed.
    /// </summary>
    public async Task StageCopy(AbsolutePath source, Func<string, string, int>? link = null, CancellationToken cancellationToken = default)
    {
        await Stage(source, paths.Inbox, link, cancellationToken);
        var destination = paths.Inbox.Combine(source.FileName);
        // Dropped from the inbox onto itself: that file is the user's, not a copy of ours.
        if (Path.GetFullPath(source.ToString()) == Path.GetFullPath(destination.ToString())) return;
        var info = new FileInfo(destination.ToString());
        _staged[destination.ToString()] = (info.Length, info.LastWriteTimeUtc);
    }

    private IAcquisitionContext AcquisitionFor(ArchivePlan plan, FileIdentity hash)
        => plan.Modlist is not null && plan.FindArchive(hash) is not null
            ? new ModlistAcquisition(plan.Modlist, logger)
            : UnassignedAcquisition.Instance;

    /// <summary>Whether the inbox folder is being watched.</summary>
    public bool IsWatching
    {
        get
        {
            lock (_watchLock) return _watcher is not null;
        }
    }

    /// <summary>
    /// Starts <see cref="StartWatching"/> with the first plan that has a row waiting for the user, whatever the
    /// Downloads setting says: a file dropped in the inbox is the user's own action. The inbox has no stop, so the
    /// watch then runs until the app exits. Idempotent.
    /// </summary>
    public void WatchOnceArchivesAreWaiting()
    {
        lock (_watchLock)
        {
            if (_waitingForPlan || _watcher is not null) return;
            _waitingForPlan = true;
        }
        acquirer.PlanChanged += OnPlanChanged;
        StartWhenArchivesWait();
    }

    private void OnPlanChanged(ArchivePlan _) => StartWhenArchivesWait();

    private void StartWhenArchivesWait()
    {
        if (!acquirer.Plan.Rows.Any(row => row.State.WaitsForUser())) return;
        acquirer.PlanChanged -= OnPlanChanged;
        try
        {
            StartWatching();
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Could not watch the inbox folder; files dropped there are found when it is checked by hand");
        }
    }

    /// <summary>Scans whenever the folder has been quiet for <see cref="DebounceInterval"/> after a change.</summary>
    public void StartWatching()
    {
        lock (_watchLock)
        {
            if (_watcher is not null) return;
            StartWatcher();
        }
    }

    private void StartWatcher()
    {
        _debounce = new Timer(_ => _ = ScanAfterChange());
        _watcher = new FileSystemWatcher(paths.Inbox.ToString())
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Created += (_, _) => Schedule();
        _watcher.Changed += (_, _) => Schedule();
        _watcher.Renamed += (_, _) => Schedule();
        _watcher.EnableRaisingEvents = true;
    }

    private void Schedule() => _debounce?.Change(DebounceInterval, Timeout.InfiniteTimeSpan);

    private async Task ScanAfterChange()
    {
        try
        {
            await Scan(CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Scanning the inbox failed");
        }
    }

    public void Dispose()
    {
        acquirer.PlanChanged -= OnPlanChanged;
        _watcher?.Dispose();
        _debounce?.Dispose();
        _scanning.Dispose();
    }

    /// <summary>
    /// Puts <paramref name="source"/> into <paramref name="inbox"/> under its own name and leaves the source where it
    /// is. With a <paramref name="link"/> function (<see cref="LibcLinks.TryLink"/>'s shape) it hard-links first and
    /// copies when the link fails for any reason (across filesystems, a protected hardlink, a filesystem without
    /// links); without one it copies. Either way the file goes to a <c>.part</c> name first, which the inbox scan
    /// skips, so the watcher never hashes half a file; and a file dropped from the inbox itself is read in full
    /// before anything replaces it, rather than truncated by opening its own path for writing.
    /// </summary>
    public static async Task Stage(AbsolutePath source, AbsolutePath inbox, Func<string, string, int>? link = null, CancellationToken cancellationToken = default)
    {
        // A link could point anywhere, so a dropped one is refused rather than followed or copied.
        if (IsLink(source)) throw new InboxSourceRefusedException($"{MarkdownText.Code(source.FileName)} is a link, not a file. Drop the file itself.");

        var destination = inbox.Combine(source.FileName);
        var partial = inbox.Combine(source.FileName + ".part");
        try
        {
            if (partial.FileExists) partial.Delete();
            if (link is null || link(source.ToString(), partial.ToString()) != 0)
            {
                await using var input = source.Read();
                await using var output = partial.Create();
                await input.CopyToAsync(output, cancellationToken);
            }
            partial.FileSystem.MoveFile(partial, destination, overwrite: true);
        }
        finally
        {
            // Renaming a link onto another name of the same inode does nothing, so the partial can still be there.
            if (partial.FileExists) partial.Delete();
        }
    }

    /// <summary>A link could point anywhere, so only real files that are not mid-download count.</summary>
    internal static bool IsPlainFile(AbsolutePath file)
        => !InProgressExtensions.Contains(file.Extension.ToString()) && !IsLink(file);

    private static bool IsLink(AbsolutePath file)
    {
        var info = new FileInfo(file.ToString());
        return info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }
}

/// <summary>A file the user handed to the inbox that the app will not take, with the reason in plain words.</summary>
public sealed class InboxSourceRefusedException(string message) : UserFacingException(message)
{
    public override string Title => "File not added";
}
