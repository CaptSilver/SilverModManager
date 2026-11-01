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
    private readonly Lock _watchLock = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private bool _waitingForPlan;

    /// <summary>How long the folder has to be quiet before a change triggers a scan.</summary>
    public TimeSpan DebounceInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The files the last scan found that matched nothing.</summary>
    public IReadOnlyList<UnmatchedInbox> Unmatched { get; private set; } = [];

    /// <summary>
    /// Adds every file in the inbox to the library and checks the plan against it. Files the plan wanted have
    /// served their purpose and are removed from the inbox; the rest stay and are returned.
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
                    found.Add((file, FileIdentity.XxHash64(await FileHashing.XxHash64(file, cancellationToken))));
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
                if (locator.FindByXxHash64(connection.Db, hash) is not null) continue;
                try
                {
                    await library.AddLocalFile(file, AcquisitionFor(wanting, hash));
                    addedHere.Add(hash);
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
                    file.Delete();
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
        if (IsLink(source)) throw new InboxSourceRefusedException($"'{source.FileName}' is a link, not a file. Drop the file itself.");

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
