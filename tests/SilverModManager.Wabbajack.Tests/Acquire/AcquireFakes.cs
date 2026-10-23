using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Downloads;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Library;
using NexusMods.Sdk;
using NexusMods.Wabbajack.Acquire;
using NexusMods.Wabbajack.Format;
using NSubstitute;

namespace NexusMods.Wabbajack.Tests.Acquire;

/// <summary>An <see cref="IOSInterop"/> that records the addresses it is asked to open instead of opening them.</summary>
internal sealed class OpenedUris
{
    private readonly List<Uri> _opened = [];

    public OpenedUris()
    {
        Interop = Substitute.For<IOSInterop>();
        Interop.When(i => i.OpenUri(Arg.Any<Uri>())).Do(call => { lock (_opened) _opened.Add(call.Arg<Uri>()); });
    }

    public IOSInterop Interop { get; }

    public IReadOnlyList<Uri> Opened
    {
        get { lock (_opened) return _opened.ToArray(); }
    }
}

/// <summary>A download that writes its bytes after a short pause and counts how many run at once.</summary>
internal sealed record FakeDownloadJob : IJobDefinitionWithStart<FakeDownloadJob, AbsolutePath>, IDownloadJob
{
    public required AbsolutePath Destination { get; init; }
    public required byte[] Bytes { get; init; }
    public required ConcurrencyGauge Gauge { get; init; }
    public Exception? Failure { get; init; }

    public async ValueTask<AbsolutePath> StartAsync(IJobContext<FakeDownloadJob> context)
    {
        Gauge.Enter();
        try
        {
            await Task.Delay(40, context.CancellationToken);
            if (Failure is not null) throw Failure;
            await Destination.WriteAllBytesAsync(Bytes);
            return Destination;
        }
        finally
        {
            Gauge.Exit();
        }
    }

    public ValueTask AddMetadata(ITransaction transaction, LibraryFile.New libraryFile) => ValueTask.CompletedTask;
}

internal sealed class ConcurrencyGauge
{
    private int _current;
    private int _peak;

    public int Peak => _peak;

    public void Enter()
    {
        var now = Interlocked.Increment(ref _current);
        int seen;
        while (now > (seen = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, now, seen) != seen) { }
    }

    public void Exit() => Interlocked.Decrement(ref _current);
}

/// <summary>Hands out <see cref="FakeDownloadJob"/>s whose content (or failure) the test chooses per archive hash.</summary>
internal sealed class FakeDownloadJobs(IServiceProvider provider) : IArchiveDownloadJobs
{
    public ConcurrencyGauge Gauge { get; } = new();
    public Dictionary<Hash, byte[]> Content { get; } = new();
    public Dictionary<Hash, Exception> Failures { get; } = new();
    public List<ArchiveState> Requested { get; } = [];

    public Task<IJobTask<IDownloadJob, AbsolutePath>> Create(ArchivePlanRow row, AbsolutePath destination, CancellationToken cancellationToken)
    {
        lock (Requested) Requested.Add(row.State);
        var job = new FakeDownloadJob
        {
            Destination = destination,
            Bytes = Content.GetValueOrDefault(row.Archive.XxHash64, [1, 2, 3]),
            Gauge = Gauge,
            Failure = Failures.GetValueOrDefault(row.Archive.XxHash64),
        };
        IJobTask<IDownloadJob, AbsolutePath> task = provider.GetRequiredService<IJobMonitor>().Begin<FakeDownloadJob, AbsolutePath>(job);
        return Task.FromResult(task);
    }
}
