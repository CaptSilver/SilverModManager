using System.Reactive.Subjects;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Library;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Abstractions.Downloads;

namespace SilverModManager.Wabbajack.Tests.Acquire;

/// <summary>
/// When the acquirer looks the plan up in the library: only while a page shows the plan, a run is under way or
/// Nexus Mods files are expected, and never more than once per interval. A fake clock and a hand-driven revision
/// stream stand in for the database.
/// </summary>
public class ArchiveAcquirerWatchingTests : IDisposable
{
    private readonly Subject<IDb> _revisions = new();
    private readonly FakeTimeProvider _time = new();
    private readonly CountingLocator _locator = new();
    private readonly BlockingJobs _jobs = new();
    private readonly ExpectedNexusDownloads _expected = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "acquirer-watching-" + Guid.NewGuid());
    private readonly ArchiveAcquirer _acquirer;

    public ArchiveAcquirerWatchingTests()
    {
        var connection = Substitute.For<IConnection>();
        connection.Revisions.Returns(_revisions);
        connection.Db.Returns(Substitute.For<IDb>());
        var fileSystem = FileSystem.Shared;
        _acquirer = new ArchiveAcquirer(
            Substitute.For<ILibraryService>(),
            _locator,
            connection,
            _jobs,
            new TemporaryFileManager(fileSystem, fileSystem.FromUnsanitizedFullPath(_root) / "tmp"),
            Substitute.For<IOSInterop>(),
            _expected,
            _time,
            NullLogger<ArchiveAcquirer>.Instance);
    }

    public void Dispose()
    {
        _acquirer.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static ArchiveEntry Entry(DownloadState state, ulong hash)
        => new(Xx64.From(hash), 10, $"{hash}.7z", "", state);

    private static ArchivePlanRow ManualRow(ulong hash)
        => new(Entry(new ManualState(new Uri("https://example.test/m"), "get it"), hash), ArchiveState.Manual, "get it", null, null);

    private static ArchivePlanRow BrowserRow(ulong hash)
        => new(Entry(new NexusState("Skyrim", 5, 6, null, null, null, false), hash), ArchiveState.Browser, null, new Uri("https://www.nexusmods.com/x"), null);

    private static ArchivePlanRow AutomaticRow(ulong hash)
        => new(Entry(new HttpState(new Uri("https://example.test/a.7z"), []), hash), ArchiveState.Automatic, null, null, null);

    private static ArchivePlan ThreeManualRows() => new([ManualRow(1), ManualRow(2), ManualRow(3)]);

    private void Burst(int revisions = 20)
    {
        for (var i = 0; i < revisions; i++) _revisions.OnNext(Substitute.For<IDb>());
    }

    [Fact]
    public void NothingLooksAtTheLibraryOnceThePageIsGoneAndNothingIsDownloading()
    {
        var page = _acquirer.Attach();
        _acquirer.Show(ThreeManualRows());
        page.Dispose();
        _locator.Calls = 0;

        Burst();
        _time.Advance(TimeSpan.FromMinutes(1));

        _locator.Calls.Should().Be(0);
        _revisions.HasObservers.Should().BeFalse();
    }

    [Fact]
    public void ThePlanIsForgottenWhenNothingNeedsIt()
    {
        var page = _acquirer.Attach();
        _acquirer.Show(ThreeManualRows());
        page.Dispose();

        _acquirer.Plan.Rows.Should().BeEmpty();
    }

    [Fact]
    public void ABurstOfRevisionsCausesOneLookupOfTheRowsAfterTheIntervalHasPassed()
    {
        using var page = _acquirer.Attach();
        _acquirer.Show(ThreeManualRows());
        _locator.Calls = 0;

        Burst();
        _locator.Calls.Should().Be(0, "a lookup waits out the interval");
        _time.Advance(ArchiveAcquirer.RecheckInterval - TimeSpan.FromMilliseconds(1));
        _locator.Calls.Should().Be(0);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        _locator.Calls.Should().Be(3);

        Burst();
        _time.Advance(ArchiveAcquirer.RecheckInterval);
        _locator.Calls.Should().Be(6);
    }

    [Fact]
    public void ShowingAPlanAgainAfterThePageCameBackLooksTheRowsUpAtOnce()
    {
        var plan = ThreeManualRows();
        var first = _acquirer.Attach();
        _acquirer.Show(plan);
        first.Dispose();
        _locator.Calls = 0;

        using var second = _acquirer.Attach();
        _acquirer.Show(plan);

        _locator.Calls.Should().Be(3);
        _acquirer.Plan.Rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task ARunKeepsLookingAfterThePageIsClosedAndStopsWhenItEnds()
    {
        var page = _acquirer.Attach();
        var run = _acquirer.Run(new ArchivePlan([AutomaticRow(1), ManualRow(2)]), null, CancellationToken.None);
        await _jobs.Started;
        page.Dispose();
        _locator.Calls = 0;

        Burst();
        _time.Advance(ArchiveAcquirer.RecheckInterval);
        _locator.Calls.Should().BeGreaterThan(0);

        _jobs.Fail();
        await run;
        _revisions.HasObservers.Should().BeFalse();
    }

    [Fact]
    public async Task ExpectedBrowserDownloadsKeepTheLookupGoingUntilReleased()
    {
        await _acquirer.Run(new ArchivePlan([BrowserRow(1), ManualRow(2)]), null, CancellationToken.None);
        _locator.Calls = 0;

        Burst();
        _time.Advance(ArchiveAcquirer.RecheckInterval);
        _locator.Calls.Should().BeGreaterThan(0);

        _acquirer.Release();
        _locator.Calls = 0;
        Burst();
        _time.Advance(TimeSpan.FromMinutes(1));
        _locator.Calls.Should().Be(0);
        _revisions.HasObservers.Should().BeFalse();
    }

    private sealed class CountingLocator : ILibraryArchiveLocator
    {
        public int Calls;

        public LibraryFile.ReadOnly? FindByXxHash64(IDb db, FileIdentity identity)
        {
            Interlocked.Increment(ref Calls);
            return null;
        }
    }

    /// <summary>Holds every download until <see cref="Fail"/>, then fails it.</summary>
    private sealed class BlockingJobs : IArchiveDownloadJobs
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;
        public void Fail() => _gate.TrySetResult();

        public async Task<IJobTask<IDownloadJob, AbsolutePath>> Create(ArchivePlanRow row, AbsolutePath destination, CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await _gate.Task;
            throw new IOException("connection reset");
        }
    }
}
