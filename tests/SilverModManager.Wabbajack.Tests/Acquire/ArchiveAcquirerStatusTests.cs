using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Settings;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;

namespace SilverModManager.Wabbajack.Tests.Acquire;

/// <summary>What the acquirer can say about a run (progress, bytes, failures) and the restart flag it keeps for it.</summary>
public class ArchiveAcquirerStatusTests(ITestOutputHelper helper) : AcquireTestBase<ArchiveAcquirerStatusTests>(helper)
{
    private static readonly LibraryFileId ListA = LibraryFileId.From(EntityId.From(0xA0A));
    private static readonly LibraryFileId ListB = LibraryFileId.From(EntityId.From(0xB0B));
    private static readonly Uri Mirror = new("https://example.test/files/");

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddSingleton<IOSInterop>(provider => provider.GetRequiredService<OpenedUris>().Interop)
            .AddSingleton<OpenedUris>()
            .AddSingleton<IArchiveDownloadJobs>(provider => new FakeDownloadJobs(provider));
    }

    private FakeDownloadJobs Jobs => (FakeDownloadJobs)ServiceProvider.GetRequiredService<IArchiveDownloadJobs>();
    private IArchiveAcquirer Acquirer => ServiceProvider.GetRequiredService<IArchiveAcquirer>();
    private ISettingsManager Settings => ServiceProvider.GetRequiredService<ISettingsManager>();

    private ArchivePlan Plan(LibraryFileId? list, params string[] names)
    {
        var rows = names.Select((name, i) =>
        {
            var content = new byte[] { (byte)i, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
            Jobs.Content[Hash64(content)] = content;
            return new ArchivePlanRow(Entry(new HttpState(new Uri(Mirror, name), []), Hash64(content), content.Length, name), ArchiveState.Automatic, null, null, null);
        }).ToArray();
        return new ArchivePlan(rows, list);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(5, TestContext.Current.CancellationToken);
        condition().Should().BeTrue();
    }

    [Fact]
    public void BeforeAnyRunThereIsNoStatus()
    {
        Acquirer.Status.Should().BeNull();
    }

    [Fact]
    public async Task ARunReportsItsProgressBytesAndSpeedUntilItEnds()
    {
        var gate = new TaskCompletionSource();
        Jobs.Gate = gate.Task;
        using var page = Acquirer.Attach();

        var run = Acquirer.Run(Plan(ListA, "1.7z", "2.7z", "3.7z"), null, CancellationToken.None);
        await WaitUntil(() => Jobs.Gauge.Current == 3);

        Acquirer.Status.Should().Match<AcquireStatus>(s => s.IsRunning && s.Modlist == ListA && s.Total == 3 && s.Finished == 0 && s.BytesFetched == 0);

        gate.SetResult();
        await run;

        var status = Acquirer.Status!;
        status.IsRunning.Should().BeFalse();
        status.Finished.Should().Be(3);
        status.BytesFetched.Should().Be(30);
        status.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        status.Failures.Should().BeEmpty();
    }

    [Theory]
    [InlineData(30, 10, 3L)]
    [InlineData(1000, 1, 1000L)]
    [InlineData(1000, 0, null)]
    public void TheSpeedIsTheFinishedBytesOverTheTimeTheRunHasTaken(long bytes, int seconds, long? expected)
    {
        new AcquireStatus(null, true, 1, 2, bytes, TimeSpan.FromSeconds(seconds), []).BytesPerSecond.Should().Be(expected);
    }

    [Fact]
    public async Task AFailedDownloadIsKeptWithTheArchiveAndThePlainReason()
    {
        using var page = Acquirer.Attach();
        var plan = Plan(ListA, "good.7z", "bad.7z");
        Jobs.Failures[plan.Rows[1].Archive.XxHash64] = new IOException("connection reset");

        await Acquirer.Run(plan, null, CancellationToken.None);

        var status = Acquirer.Status!;
        status.Finished.Should().Be(2);
        status.BytesFetched.Should().Be(10);
        status.Failures.Should().ContainSingle().Which.Should().Be(new DownloadFailure("bad.7z", "connection reset"));
    }

    [Fact]
    public async Task OnlyTheLatestFailuresAreKept()
    {
        using var page = Acquirer.Attach();
        var names = Enumerable.Range(0, AcquireStatus.FailuresKept + 5).Select(i => $"{i:00}.7z").ToArray();
        var plan = Plan(ListA, names);
        foreach (var row in plan.Rows) Jobs.Failures[row.Archive.XxHash64] = new IOException("nope");

        await Acquirer.Run(plan, null, CancellationToken.None);

        Acquirer.Status!.Failures.Should().HaveCount(AcquireStatus.FailuresKept);
    }

    [Fact]
    public async Task EveryChangeOfTheStatusIsAnnounced()
    {
        var seen = new List<AcquireStatus>();
        Acquirer.StatusChanged += status => { lock (seen) seen.Add(status); };

        await Acquirer.Run(Plan(ListA, "1.7z", "2.7z"), null, CancellationToken.None);

        lock (seen)
        {
            seen.First().IsRunning.Should().BeTrue();
            seen.Select(s => s.Finished).Should().BeInAscendingOrder().And.Contain(2);
            seen.Last().IsRunning.Should().BeFalse();
        }
    }

    [Fact]
    public async Task RunSetsTheRestartFlagForItsModlistAndTheEndOfTheRunClearsIt()
    {
        var gate = new TaskCompletionSource();
        Jobs.Gate = gate.Task;

        var run = Acquirer.Run(Plan(ListA, "1.7z"), null, CancellationToken.None);

        PendingDownload.Read(Settings).Should().Be(ListA);
        gate.SetResult();
        await run;
        PendingDownload.Read(Settings).Should().BeNull();
    }

    [Fact]
    public async Task APlanWithoutAModlistLeavesNoFlag()
    {
        var gate = new TaskCompletionSource();
        Jobs.Gate = gate.Task;

        var run = Acquirer.Run(Plan(null, "1.7z"), null, CancellationToken.None);

        PendingDownload.Read(Settings).Should().BeNull();
        gate.SetResult();
        await run;
    }

    [Fact]
    public async Task CancellingARunClearsTheFlag()
    {
        var gate = new TaskCompletionSource();
        Jobs.Gate = gate.Task;
        var run = Acquirer.Run(Plan(ListA, "1.7z", "2.7z"), null, CancellationToken.None);
        await WaitUntil(() => Jobs.Gauge.Current == 2);

        Acquirer.Cancel();

        await run.Invoking(r => r).Should().ThrowAsync<OperationCanceledException>();
        PendingDownload.Read(Settings).Should().BeNull();
        Acquirer.Status!.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task TheCallersTokenCancelsARunAndClearsTheFlagToo()
    {
        var gate = new TaskCompletionSource();
        Jobs.Gate = gate.Task;
        using var cancel = new CancellationTokenSource();
        var run = Acquirer.Run(Plan(ListA, "1.7z"), null, cancel.Token);
        await WaitUntil(() => Jobs.Gauge.Current == 1);

        await cancel.CancelAsync();

        await run.Invoking(r => r).Should().ThrowAsync<OperationCanceledException>();
        PendingDownload.Read(Settings).Should().BeNull();
    }

    [Fact]
    public async Task ARunCutShortByTheAppClosingKeepsTheFlagForTheNextStart()
    {
        var gate = new TaskCompletionSource();
        Jobs.Gate = gate.Task;
        var run = Acquirer.Run(Plan(ListA, "1.7z"), null, CancellationToken.None);
        await WaitUntil(() => Jobs.Gauge.Current == 1);

        ServiceProvider.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        Acquirer.Cancel();
        await run.Invoking(r => r).Should().ThrowAsync<OperationCanceledException>();

        PendingDownload.Read(Settings).Should().Be(ListA);
    }

    [Fact]
    public async Task ShowingAnotherPlanWhileARunIsUnderWayAbandonsItAndClearsTheFlag()
    {
        var gate = new TaskCompletionSource();
        Jobs.Gate = gate.Task;
        var run = Acquirer.Run(Plan(ListA, "1.7z"), null, CancellationToken.None);
        await WaitUntil(() => Jobs.Gauge.Current == 1);

        Acquirer.Show(Plan(ListB, "other.7z"));
        PendingDownload.Read(Settings).Should().BeNull();

        gate.SetResult();
        await run;
    }

    [Fact]
    public async Task ARunThatReplacesAnotherKeepsTheFlagOfTheNewOne()
    {
        var gate = new TaskCompletionSource();
        Jobs.Gate = gate.Task;
        var first = Acquirer.Run(Plan(ListA, "1.7z"), null, CancellationToken.None);
        await WaitUntil(() => Jobs.Gauge.Current == 1);
        var second = Acquirer.Run(Plan(ListB, "2.7z"), null, CancellationToken.None);

        PendingDownload.Read(Settings).Should().Be(ListB);
        gate.SetResult();
        await Task.WhenAll(first, second);

        PendingDownload.Read(Settings).Should().BeNull();
        Acquirer.Status!.Modlist.Should().Be(ListB);
    }
}
