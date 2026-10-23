using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Library;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Hashes;
using NexusMods.Wabbajack.Library;

namespace NexusMods.Wabbajack.Acquire;

/// <summary>A file in the inbox that no archive of the current plan wants.</summary>
public sealed record UnmatchedInbox(string FileName, Hash XxHash64);

/// <summary>
/// The folder the user drops archives into when the app cannot fetch them itself. Only the inbox folder from
/// <see cref="WabbajackSettings"/> is read; the user's Downloads folder is never looked at.
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
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

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
            var found = new List<(AbsolutePath Path, Hash Hash)>();
            foreach (var file in paths.Inbox.EnumerateFiles(recursive: false).ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsPlainFile(file)) continue;

                try
                {
                    await using (var stream = file.Read())
                    {
                        found.Add((file, await Xx64Hasher.HashAsync(stream, cancellationToken: cancellationToken)));
                    }
                }
                catch (IOException e)
                {
                    logger.LogWarning(e, "Could not read '{File}' in the inbox, it is probably still being written", file.FileName);
                }
            }

            var unreadable = new List<(AbsolutePath Path, Hash Hash)>();
            foreach (var (file, hash) in found)
            {
                if (locator.FindByXxHash64(connection.Db, hash) is not null) continue;
                try
                {
                    await library.AddLocalFile(file);
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
                if (added && plan.FindArchive(hash) is not null) file.Delete();
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

    /// <summary>Scans whenever the folder has been quiet for <see cref="DebounceInterval"/> after a change.</summary>
    public void StartWatching()
    {
        if (_watcher is not null) return;

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
        _watcher?.Dispose();
        _debounce?.Dispose();
        _scanning.Dispose();
    }

    /// <summary>A link could point anywhere, so only real files that are not mid-download count.</summary>
    private static bool IsPlainFile(AbsolutePath file)
    {
        if (InProgressExtensions.Contains(file.Extension.ToString())) return false;
        var info = new FileInfo(file.ToString());
        return info.LinkTarget is null && !info.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }
}
