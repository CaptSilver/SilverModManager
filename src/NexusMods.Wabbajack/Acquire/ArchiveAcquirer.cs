using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Library;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.NexusModsApi;
using NexusMods.Sdk;
using NexusMods.Wabbajack.Format;
using NexusMods.Wabbajack.Library;

namespace NexusMods.Wabbajack.Acquire;

public sealed class ArchiveAcquirer(
    ILibraryService library,
    ILibraryArchiveLocator locator,
    IConnection connection,
    IArchiveDownloadJobs downloadJobs,
    TemporaryFileManager temporaryFileManager,
    IOSInterop osInterop,
    ExpectedNexusDownloads expectedDownloads,
    ILogger<ArchiveAcquirer> logger) : IArchiveAcquirer, IDisposable
{
    private const int ConcurrentDownloads = 4;

    private readonly Lock _lock = new();
    private ArchivePlanRow[] _rows = [];
    private IDisposable? _subscription;
    private bool _expecting;

    public event Action<ArchivePlan>? PlanChanged;

    public ArchivePlan Plan
    {
        get { lock (_lock) return new ArchivePlan(_rows.ToArray()); }
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
            _expecting = expecting;
        }
        UpdateExpected();
        _subscription = connection.Revisions.Subscribe(_ => Recheck());
        // Files that arrived between building the plan and now.
        Recheck();
        return rows;
    }

    public async Task Run(ArchivePlan plan, IProgress<AcquireProgress>? progress, CancellationToken cancellationToken)
    {
        var rows = Adopt(plan, expecting: true);

        var automatic = Enumerable.Range(0, plan.Rows.Count).Where(i => plan.Rows[i].State == ArchiveState.Automatic).ToArray();
        var finished = 0;
        progress?.Report(new AcquireProgress(0, automatic.Length));

        using var gate = new SemaphoreSlim(ConcurrentDownloads);
        await Task.WhenAll(automatic.Select(async index =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                await Download(rows, index, cancellationToken);
            }
            finally
            {
                gate.Release();
                progress?.Report(new AcquireProgress(Interlocked.Increment(ref finished), automatic.Length));
            }
        }));
    }

    private async Task Download(ArchivePlanRow[] rows, int index, CancellationToken cancellationToken)
    {
        // Still queued when a later run replaced the plan: nobody wants this archive any more.
        if (!IsCurrent(rows)) return;
        var row = RowAt(rows, index);
        try
        {
            await using var destination = temporaryFileManager.CreateFile();
            var job = await downloadJobs.Create(row, destination.Path, cancellationToken);
            var file = await library.AddDownload(job);

            if (file.XxHash64.HasValue && file.XxHash64.Value == row.Archive.XxHash64)
                Update(rows, index, r => r with { State = ArchiveState.Have, Reason = null, Library = file });
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

    public void Recheck()
    {
        var db = connection.Db;
        var changed = false;
        lock (_lock)
        {
            for (var i = 0; i < _rows.Length; i++)
            {
                var row = _rows[i];
                if (row.State is not (ArchiveState.Browser or ArchiveState.Manual or ArchiveState.Unsupported)) continue;
                if (locator.FindByXxHash64(db, row.Archive.XxHash64) is not { } held) continue;
                _rows[i] = row with { State = ArchiveState.Have, Reason = null, Library = held };
                changed = true;
            }
        }

        if (!changed) return;
        UpdateExpected();
        PlanChanged?.Invoke(Plan);
    }

    public void Release()
    {
        _subscription?.Dispose();
        _subscription = null;
        expectedDownloads.Clear(this);
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
        _ => null,
    };
}
