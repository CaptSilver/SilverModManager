using Microsoft.Extensions.Hosting;
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
    ILogger<ArchiveAcquirer> logger,
    IHostApplicationLifetime? lifetime = null) : IArchiveAcquirer, IDisposable
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
    private RunState? _state;
    private CancellationTokenSource? _runCancellation;

    /// <summary>What <see cref="Status"/> reads from; each run has its own, so a run that was replaced cannot write into its successor's.</summary>
    private sealed class RunState(LibraryFileId? modlist, int total, long started)
    {
        public readonly LibraryFileId? Modlist = modlist;
        public readonly int Total = total;
        public readonly long Started = started;
        public int Finished;
        public long Bytes;
        public bool IsRunning = true;
        public long? Ended;
        public readonly List<DownloadFailure> Failures = [];
    }

    public event Action<ArchivePlan>? PlanChanged;

    public event Action? PlanForgotten;

    public event Action<AcquireStatus>? StatusChanged;

    public AcquireStatus? Status
    {
        get { lock (_lock) return _state is { } state ? Snapshot(state) : null; }
    }

    // Caller holds the lock.
    private AcquireStatus Snapshot(RunState state) => new(
        state.Modlist, state.IsRunning, state.Finished, state.Total, state.Bytes,
        time.GetElapsedTime(state.Started, state.Ended ?? time.GetTimestamp()),
        state.Failures.TakeLast(AcquireStatus.FailuresKept).ToArray());

    /// <summary>
    /// Hands an event to each listener on its own, so one that throws (a page touching its UI from the wrong thread,
    /// say) cannot stop a run or keep the others from hearing it.
    /// </summary>
    private void Notify<T>(Action<T>? listeners, T value) => NotifyEach(listeners, listener => ((Action<T>)listener)(value));

    private void Notify(Action? listeners) => NotifyEach(listeners, listener => ((Action)listener)());

    private void NotifyEach(Delegate? listeners, Action<Delegate> call)
    {
        if (listeners is null) return;
        foreach (var listener in listeners.GetInvocationList())
        {
            try
            {
                call(listener);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "A listener of the archive downloads threw; the downloads go on");
            }
        }
    }

    /// <summary>Changes the run's numbers and tells listeners, unless a later run has taken over the status.</summary>
    private void Record(RunState state, Action<RunState> change)
    {
        AcquireStatus snapshot;
        lock (_lock)
        {
            change(state);
            if (!ReferenceEquals(_state, state)) return;
            snapshot = Snapshot(state);
        }
        Notify(StatusChanged, snapshot);
    }

    /// <summary>
    /// The app is closing: downloads cut short now are not finished, so the restart flag has to stay for the next
    /// start to find.
    /// </summary>
    private bool IsStopping => lifetime?.ApplicationStopping.IsCancellationRequested == true;

    public void Cancel()
    {
        CancellationTokenSource? current;
        lock (_lock) current = _runCancellation;
        try
        {
            current?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run ended in the same moment.
        }
    }

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
        RunState? abandoned = null;
        lock (_lock)
        {
            if (_running is not null && _state is { IsRunning: true } running)
            {
                running.IsRunning = false;
                running.Ended = time.GetTimestamp();
                abandoned = running;
            }
            _rows = rows;
            _modlist = plan.Modlist;
            _expecting = expecting;
            _running = expecting ? rows : null;
        }
        // A plan shown over a run drops the run's rows, so nothing is left to finish: a restart must not revive it.
        // A run that replaces a run sets its own flag right after this.
        if (abandoned is not null)
        {
            if (!expecting) PendingDownload.Set(settings, null);
            Record(abandoned, _ => { });
        }
        UpdateExpected();
        UpdateWatching();
        // Files that arrived between building the plan and now.
        Recheck();
        // Whoever follows the plan (the Downloads pickup) learns that it now has these rows.
        Notify(PlanChanged, Plan);
        return rows;
    }

    public async Task Run(ArchivePlan plan, IProgress<AcquireProgress>? progress, CancellationToken cancellationToken)
    {
        // Cancel() reaches the run through this, whoever started it.
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellationToken = runCancellation.Token;
        var rows = Adopt(plan, expecting: true);

        var automatic = Enumerable.Range(0, plan.Rows.Count).Where(i => plan.Rows[i].State == ArchiveState.Automatic).ToArray();
        var state = new RunState(plan.Modlist, automatic.Length, time.GetTimestamp());
        lock (_lock)
        {
            _state = state;
            _runCancellation = runCancellation;
        }
        if (plan.Modlist is { } listed) PendingDownload.Set(settings, listed);
        Record(state, _ => { });
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
                    await Download(rows, plan.Modlist, state, index, cancellationToken);
                }
                finally
                {
                    gate.Release();
                    var finished = 0;
                    Record(state, s => finished = ++s.Finished);
                    progress?.Report(new AcquireProgress(finished, automatic.Length));
                }
            }));
        }
        finally
        {
            bool current;
            lock (_lock)
            {
                current = ReferenceEquals(_running, rows);
                if (current) _running = null;
                if (ReferenceEquals(_runCancellation, runCancellation)) _runCancellation = null;
            }
            if (current && plan.Modlist is not null && !IsStopping) PendingDownload.Set(settings, null);
            Record(state, s =>
            {
                s.IsRunning = false;
                s.Ended ??= time.GetTimestamp();
            });
            if (IsCurrent(rows) && UpdateWatching()) Notify(PlanForgotten);
        }
    }

    private async Task Download(ArchivePlanRow[] rows, LibraryFileId? modlist, RunState state, int index, CancellationToken cancellationToken)
    {
        // Still queued when a later run replaced the plan: nobody wants this archive any more.
        if (!IsCurrent(rows)) return;
        var row = RowAt(rows, index);
        try
        {
            await using var destination = temporaryFileManager.CreateFile();
            var job = await downloadJobs.Create(row, destination.Path, cancellationToken);
            // The job runs on the job monitor, not on this token, so stopping the run has to stop the transfer itself.
            await using var stopTransfer = cancellationToken.Register(() => job.Job.AsContext().Cancel());
            var file = await library.AddDownload(job);

            if (file.XxHash64Identity() is { } downloaded && downloaded.Equals(row.Archive.XxHash64))
            {
                Update(rows, index, r => r with { State = ArchiveState.Have, Reason = null, Library = file });
                Record(state, s => s.Bytes += row.Archive.Size);
                // The download was started for this list, so it was fetched for it even if the plan has since been replaced.
                if (modlist is not null) await FetchedArchives.Mark(connection, row.Archive.XxHash64, file);
            }
            else
            {
                Update(rows, index, r => r with { State = ArchiveState.Manual, Reason = "hash mismatch", Page = OriginalUrl(r) });
                Record(state, s => s.Failures.Add(new DownloadFailure(row.Archive.Name, "hash mismatch")));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Downloading '{Name}' failed; it is left for the user to fetch", row.Archive.Name);
            Update(rows, index, r => r with { State = ArchiveState.Manual, Reason = e.Message, Page = OriginalUrl(r) });
            Record(state, s => s.Failures.Add(new DownloadFailure(row.Archive.Name, e.Message)));
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
        if (pages.Count > 0) Notify(PlanChanged, Plan);
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
        if (UpdateWatching()) Notify(PlanForgotten);
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
        Notify(PlanChanged, Plan);
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
        Notify(PlanChanged, Plan);
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
