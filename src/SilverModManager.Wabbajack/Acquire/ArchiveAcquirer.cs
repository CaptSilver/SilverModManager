using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Settings;
using SilverModManager.Wabbajack.Acquire.Hosts;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Library;

namespace SilverModManager.Wabbajack.Acquire;

public sealed class ArchiveAcquirer(
    ILibraryService library,
    ILibraryArchiveLocator locator,
    IConnection connection,
    IArchiveDownloadJobs downloadJobs,
    TemporaryFileManager temporaryFileManager,
    IOSInterop osInterop,
    ExpectedNexusDownloads expectedDownloads,
    TimeProvider time,
    ISettingsManager settings,
    ILogger<ArchiveAcquirer> logger) : IArchiveAcquirer, IDisposable
{
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(1);

    private readonly Lock _lock = new();
    private ArchivePlanRow[] _rows = [];
    private LibraryFileId? _modlist;
    private IDisposable? _subscription;
    private ITimer? _timer;
    private bool _recheckDue;
    private bool _expecting;
    private int _pages;
    private ArchivePlanRow[]? _running;

    public event Action<ArchivePlan>? PlanChanged;

    public event Action? PlanForgotten;

    public ArchivePlan Plan
    {
        get { lock (_lock) return new ArchivePlan(_rows.ToArray(), _modlist); }
    }

    public void Show(ArchivePlan plan) => Adopt(plan, expecting: false);

    // Downloads of a plan this replaces still hold their own array, so they cannot write into this one.
    private ArchivePlanRow[] Adopt(ArchivePlan plan, bool expecting)
    {
        Release();
        var rows = plan.Rows.ToArray();
        lock (_lock)
        {
            _rows = rows;
            _modlist = plan.Modlist;
            _expecting = expecting;
            _running = expecting ? rows : null;
        }
        UpdateExpected();
        UpdateWatching();
        // Files that arrived between building the plan and now.
        Recheck();
        // Whoever follows the plan (the Downloads pickup) learns that it now has these rows.
        PlanChanged?.Invoke(Plan);
        return rows;
    }

    public async Task Run(ArchivePlan plan, IProgress<AcquireProgress>? progress, CancellationToken cancellationToken)
    {
        var rows = Adopt(plan, expecting: true);

        var automatic = Enumerable.Range(0, plan.Rows.Count).Where(i => plan.Rows[i].State == ArchiveState.Automatic).ToArray();
        var finished = 0;
        progress?.Report(new AcquireProgress(0, automatic.Length));

        try
        {
            // Read per run: the user's "downloads at once" choice applies from the next run on.
            using var gate = new SemaphoreSlim(Math.Max(1, settings.Get<DownloadSettings>().MaxParallelDownloads));
            await Task.WhenAll(automatic.Select(async index =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    await Download(rows, plan.Modlist, index, cancellationToken);
                }
                finally
                {
                    gate.Release();
                    progress?.Report(new AcquireProgress(Interlocked.Increment(ref finished), automatic.Length));
                }
            }));
        }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_running, rows)) _running = null;
            }
            if (IsCurrent(rows) && UpdateWatching()) PlanForgotten?.Invoke();
        }
    }

    private async Task Download(ArchivePlanRow[] rows, LibraryFileId? modlist, int index, CancellationToken cancellationToken)
    {
        // Still queued when a later run replaced the plan: nobody wants this archive any more.
        if (!IsCurrent(rows)) return;
        var row = RowAt(rows, index);
        try
        {
            await using var destination = temporaryFileManager.CreateFile();
            var job = await downloadJobs.Create(row, destination.Path, cancellationToken);
            var file = await library.AddDownload(job);

            if (file.XxHash64Identity() is { } downloaded && downloaded.Equals(row.Archive.XxHash64))
            {
                Update(rows, index, r => r with { State = ArchiveState.Have, Reason = null, Library = file });
                // The download was started for this list, so it was fetched for it even if the plan has since been replaced.
                if (modlist is not null) await FetchedArchives.Mark(connection, row.Archive.XxHash64, file);
            }
            else
                Update(rows, index, r => r with { State = ArchiveState.Manual, Reason = "hash mismatch", Page = OriginalUrl(r) });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Downloading '{Name}' failed; it is left for the user to fetch", row.Archive.Name);
            Update(rows, index, r => r with { State = ArchiveState.Manual, Reason = e.Message, Page = OriginalUrl(r) });
        }
    }

    public int OpenNext(int count)
    {
        List<Uri> pages = [];
        lock (_lock)
        {
            for (var i = 0; i < _rows.Length && pages.Count < count; i++)
            {
                var row = _rows[i];
                if (row.State != ArchiveState.Browser || row.Opened || row.Page is not { } page) continue;
                _rows[i] = row with { Opened = true };
                pages.Add(page);
            }
        }

        foreach (var page in pages) osInterop.OpenUri(page);
        if (pages.Count > 0) PlanChanged?.Invoke(Plan);
        return pages.Count;
    }

    public IDisposable Attach()
    {
        lock (_lock) _pages++;
        UpdateWatching();
        // What arrived while no page was watching.
        Recheck();
        return new PageHandle(this);
    }

    private void Detach()
    {
        lock (_lock) _pages--;
        if (UpdateWatching()) PlanForgotten?.Invoke();
    }

    private sealed class PageHandle(ArchiveAcquirer owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Detach();
        }
    }

    public void Recheck()
    {
        var db = connection.Db;
        var changed = false;
        List<(FileIdentity Archive, LibraryFile.ReadOnly File)>? fetched = null;
        lock (_lock)
        {
            for (var i = 0; i < _rows.Length; i++)
            {
                var row = _rows[i];
                if (row.State is not (ArchiveState.Browser or ArchiveState.SignIn or ArchiveState.Manual or ArchiveState.Unsupported)) continue;
                if (locator.FindByXxHash64(db, row.Archive.XxHash64) is not { } held) continue;
                // A Browser row is one the library lacked when the plan was built and the run sent the user to fetch,
                // so its arrival is the download the run asked for. Manual, SignIn and Unsupported rows are the user's own adds.
                if (row.State == ArchiveState.Browser && _expecting && _modlist is not null
                    && locator.FindTopLevelByXxHash64(db, row.Archive.XxHash64) is { } topLevel)
                    (fetched ??= []).Add((row.Archive.XxHash64, topLevel));
                _rows[i] = row with { State = ArchiveState.Have, Reason = null, Library = held };
                changed = true;
            }
        }

        if (fetched is not null) _ = MarkFetched(fetched);

        if (!changed) return;
        UpdateExpected();
        UpdateWatching();
        PlanChanged?.Invoke(Plan);
    }

    private async Task MarkFetched(List<(FileIdentity Archive, LibraryFile.ReadOnly File)> fetched)
    {
        try
        {
            foreach (var (archive, file) in fetched) await FetchedArchives.Mark(connection, archive, file);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not record which archives were fetched for a modlist");
        }
    }

    public void Release()
    {
        StopWatching();
        expectedDownloads.Clear(this);
    }

    /// <summary>
    /// The library is only worth watching for a page that shows the plan or a run that is downloading it. With
    /// neither, the subscription goes and so do the rows: scanning every row on every database revision of the
    /// whole app for a plan nobody looks at is what this exists to avoid. What the user fetched in the meantime is
    /// matched when a page comes back, which shows its plan again and looks the rows up afresh. The Nexus Mods
    /// files the plan expects are kept apart from the rows, so the nxm handler still accepts them.
    /// </summary>
    /// <returns>Whether it dropped rows, so the plan now reads as empty.</returns>
    private bool UpdateWatching()
    {
        lock (_lock)
        {
            if (_pages > 0 || _running is not null)
            {
                if (_subscription is null)
                {
                    _timer = time.CreateTimer(_ => RecheckDue(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    _subscription = connection.Revisions.Subscribe(_ => ScheduleRecheck());
                }
                return false;
            }

            StopWatching();
            var dropped = _rows.Length > 0;
            _rows = [];
            return dropped;
        }
    }

    private void StopWatching()
    {
        lock (_lock)
        {
            _subscription?.Dispose();
            _subscription = null;
            _timer?.Dispose();
            _timer = null;
            _recheckDue = false;
        }
    }

    /// <summary>Many revisions in a row cost one scan, after <see cref="RecheckInterval"/>.</summary>
    private void ScheduleRecheck()
    {
        lock (_lock)
        {
            if (_timer is null || _recheckDue) return;
            _recheckDue = true;
            _timer.Change(RecheckInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private void RecheckDue()
    {
        lock (_lock)
        {
            if (!_recheckDue) return;
            _recheckDue = false;
        }
        Recheck();
    }

    public void Dispose() => Release();

    private bool IsCurrent(ArchivePlanRow[] rows)
    {
        lock (_lock) return ReferenceEquals(rows, _rows);
    }

    private ArchivePlanRow RowAt(ArchivePlanRow[] rows, int index)
    {
        lock (_lock) return rows[index];
    }

    /// <summary>Changes a row of <paramref name="rows"/>, unless a later run has replaced that plan.</summary>
    private void Update(ArchivePlanRow[] rows, int index, Func<ArchivePlanRow, ArchivePlanRow> change)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(rows, _rows)) return;
            rows[index] = change(rows[index]);
        }
        UpdateExpected();
        PlanChanged?.Invoke(Plan);
    }

    /// <summary>Only Browser rows are expected: the user is sent to Nexus Mods for exactly those.</summary>
    private void UpdateExpected()
    {
        ArchivePlanRow[] rows;
        lock (_lock)
        {
            rows = _expecting ? _rows.ToArray() : [];
        }

        expectedDownloads.Set(this, rows
            .Where(r => r.State == ArchiveState.Browser)
            .SelectMany(r => ExpectedFile(r.Archive.State)));
    }

    private static IEnumerable<(GameDomain, ModId, FileId)> ExpectedFile(DownloadState state)
    {
        if (state is NexusState nexus && WabbajackGames.NexusDomainFor(nexus.GameName) is { } domain)
            yield return (domain, ModId.From((uint)nexus.ModId), FileId.From((uint)nexus.FileId));
    }

    /// <summary>Where the user can get the archive themselves when the app could not.</summary>
    private static Uri? OriginalUrl(ArchivePlanRow row) => row.Page ?? row.Archive.State switch
    {
        HttpState http => http.Url,
        WabbajackCdnState cdn => cdn.Url,
        MegaState mega => mega.Url,
        GoogleDriveState drive => GoogleDriveDownloadJob.PageFor(drive.Id),
        MediaFireState mediaFire => mediaFire.Url,
        ModDbState modDb => modDb.Url,
        _ => null,
    };
}
