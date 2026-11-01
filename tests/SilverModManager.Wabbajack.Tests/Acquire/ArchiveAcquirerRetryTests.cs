using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Settings;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;

namespace SilverModManager.Wabbajack.Tests.Acquire;

/// <summary>A row whose download failed is fetched again the way the first attempt fetched it.</summary>
public class ArchiveAcquirerRetryTests(ITestOutputHelper helper) : AcquireTestBase<ArchiveAcquirerRetryTests>(helper)
{
    private static readonly Uri Mirror = new("https://example.test/files/");

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddSingleton<OpenedUris>()
            .AddSingleton<IOSInterop>(provider => provider.GetRequiredService<OpenedUris>().Interop)
            .AddSingleton<IArchiveDownloadJobs>(provider => new FakeDownloadJobs(provider));
    }

    private FakeDownloadJobs _jobs => (FakeDownloadJobs)ServiceProvider.GetRequiredService<IArchiveDownloadJobs>();

    private IArchiveAcquirer Acquirer => ServiceProvider.GetRequiredService<IArchiveAcquirer>();

    private (ArchivePlan Plan, byte[][] Contents) HttpRows(int count)
    {
        var contents = Enumerable.Range(0, count).Select(i => new byte[] { (byte)i, 7, 7, 7 }).ToArray();
        var rows = contents.Select((c, i) => new ArchivePlanRow(
            Entry(new HttpState(new Uri(Mirror, $"{i}.7z"), []), Hash64(c), c.Length, $"{i}.7z"), ArchiveState.Automatic, null, null, null)).ToArray();
        foreach (var content in contents) _jobs.Content[Hash64(content)] = content;
        return (new ArchivePlan(rows), contents);
    }

    private async Task<byte[][]> RunWithFailures(int count, params int[] failing)
    {
        var (plan, contents) = HttpRows(count);
        foreach (var i in failing) _jobs.Failures[Hash64(contents[i])] = new IOException($"connection {i} reset");
        await Acquirer.Run(plan, null, CancellationToken.None);
        return contents;
    }

    [Fact]
    public async Task RetryingAFailedRowRunsTheJobAgainAndHoldsTheFile()
    {
        using var page = Acquirer.Attach();
        var contents = await RunWithFailures(3, 1);
        Acquirer.Plan.Rows[1].CanRetry.Should().BeTrue();
        _jobs.Failures.Clear();

        await Acquirer.Retry([Hash64(contents[1])], null, CancellationToken.None);

        var row = Acquirer.Plan.Rows[1];
        row.State.Should().Be(ArchiveState.Have);
        row.Reason.Should().BeNull();
        row.Library.Should().NotBeNull();
        row.CanRetry.Should().BeFalse();
        _jobs.Requested.Should().HaveCount(4);
    }

    [Fact]
    public async Task ASecondFailureShowsTheNewReason()
    {
        using var page = Acquirer.Attach();
        var contents = await RunWithFailures(2, 0);
        _jobs.Failures[Hash64(contents[0])] = new IOException("the mirror is gone");

        await Acquirer.Retry([Hash64(contents[0])], null, CancellationToken.None);

        var row = Acquirer.Plan.Rows[0];
        row.State.Should().Be(ArchiveState.Manual);
        row.Reason.Should().Be("the mirror is gone");
        row.CanRetry.Should().BeTrue();
    }

    [Fact]
    public async Task RetryingEveryFailedRowTouchesOnlyTheFailedOnes()
    {
        using var page = Acquirer.Attach();
        var (plan, contents) = HttpRows(5);
        var handMade = new ArchivePlanRow(Entry(new ManualState(new Uri("https://example.test/m"), "get it"), Hash64([42]), 1, "m.7z"),
            ArchiveState.Manual, "get it", new Uri("https://example.test/m"), null);
        _jobs.Failures[Hash64(contents[1])] = new IOException("reset");
        _jobs.Failures[Hash64(contents[3])] = new IOException("reset");
        await Acquirer.Run(new ArchivePlan(plan.Rows.Append(handMade).ToArray()), null, CancellationToken.None);
        _jobs.Failures.Clear();
        var before = _jobs.Requested.Count;
        var held = Acquirer.Plan.Rows[0];

        await Acquirer.Retry(null, null, CancellationToken.None);

        _jobs.Requested.Count.Should().Be(before + 2);
        Acquirer.Plan.Rows[1].State.Should().Be(ArchiveState.Have);
        Acquirer.Plan.Rows[3].State.Should().Be(ArchiveState.Have);
        Acquirer.Plan.Rows[0].Should().Be(held);
        Acquirer.Plan.Rows[5].State.Should().Be(ArchiveState.Manual);
        Acquirer.Plan.Rows[5].Reason.Should().Be("get it");
    }

    [Fact]
    public async Task RetryingOneRowLeavesTheOtherFailedRowsAlone()
    {
        using var page = Acquirer.Attach();
        var contents = await RunWithFailures(3, 0, 2);
        _jobs.Failures.Clear();

        await Acquirer.Retry([Hash64(contents[2])], null, CancellationToken.None);

        Acquirer.Plan.Rows[2].State.Should().Be(ArchiveState.Have);
        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Manual);
        Acquirer.Plan.Rows[0].Reason.Should().Be("connection 0 reset");
    }

    [Fact]
    public async Task ARowThatIsStillDownloadingIsNotStartedASecondTime()
    {
        using var page = Acquirer.Attach();
        var (plan, contents) = HttpRows(3);
        var release = new TaskCompletionSource();
        _jobs.Gate = release.Task;
        var run = Acquirer.Run(plan, null, CancellationToken.None);
        await WaitFor(() => _jobs.Gauge.Current == 3);

        await Acquirer.Retry(null, null, CancellationToken.None);
        await Acquirer.Retry([Hash64(contents[0])], null, CancellationToken.None);

        _jobs.Requested.Should().HaveCount(3);
        release.SetResult();
        await run;
        Acquirer.Plan.Rows.Should().OnlyContain(r => r.State == ArchiveState.Have);
    }

    [Fact]
    public async Task ARowAlreadyHeldIsNotDownloadedAgain()
    {
        using var page = Acquirer.Attach();
        var contents = await RunWithFailures(2, 0);
        var held = Acquirer.Plan.Rows[1];

        await Acquirer.Retry([Hash64(contents[1])], null, CancellationToken.None);

        Acquirer.Plan.Rows[1].Should().Be(held);
        _jobs.Requested.Should().HaveCount(2);
    }

    [Fact]
    public async Task WithNothingFailedARetryDoesNothing()
    {
        using var page = Acquirer.Attach();
        await RunWithFailures(2);
        var plan = Acquirer.Plan;

        await Acquirer.Retry(null, null, CancellationToken.None);

        Acquirer.Plan.Rows.Should().Equal(plan.Rows);
        _jobs.Requested.Should().HaveCount(2);
    }

    [Fact]
    public async Task AWrongFileOnRetryIsStillRefused()
    {
        using var page = Acquirer.Attach();
        var contents = await RunWithFailures(1, 0);
        _jobs.Failures.Clear();
        _jobs.Content[Hash64(contents[0])] = [9, 9, 9, 9];

        await Acquirer.Retry(null, null, CancellationToken.None);

        var row = Acquirer.Plan.Rows[0];
        row.State.Should().Be(ArchiveState.Manual);
        row.Reason.Should().Be("hash mismatch");
        row.Library.Should().BeNull();
        row.CanRetry.Should().BeTrue();
    }

    [Fact]
    public async Task ARetryRunsUnderTheDownloadsAtOnceSetting()
    {
        ServiceProvider.GetRequiredService<ISettingsManager>().Update<DownloadSettings>(_ => new DownloadSettings { MaxParallelDownloads = 2 });
        using var page = Acquirer.Attach();
        await RunWithFailures(6, 0, 1, 2, 3, 4, 5);
        _jobs.Failures.Clear();
        var release = new TaskCompletionSource();
        _jobs.Gate = release.Task;

        var retry = Acquirer.Retry(null, null, CancellationToken.None);
        await WaitFor(() => _jobs.Gauge.Current == 2);
        release.SetResult();
        await retry;

        _jobs.Gauge.Peak.Should().Be(2);
        Acquirer.Plan.Rows.Should().OnlyContain(r => r.State == ArchiveState.Have);
    }

    [Fact]
    public async Task ARetryReportsItsProgressAndStatus()
    {
        using var page = Acquirer.Attach();
        await RunWithFailures(4, 0, 1);
        _jobs.Failures.Clear();
        var reports = new List<AcquireProgress>();

        await Acquirer.Retry(null, new Progress<AcquireProgress>(reports.Add), CancellationToken.None);

        await WaitFor(() => reports.Count > 0 && reports.Last().Equals(new AcquireProgress(2, 2)));
        Acquirer.Status.Should().Match<AcquireStatus>(s => !s.IsRunning && s.Total == 2 && s.Finished == 2);
    }

    [Fact]
    public async Task CancellingStopsARetryLikeAnyDownload()
    {
        using var page = Acquirer.Attach();
        await RunWithFailures(2, 0, 1);
        _jobs.Failures.Clear();
        _jobs.Gate = new TaskCompletionSource().Task;

        var retry = Acquirer.Retry(null, null, CancellationToken.None);
        await WaitFor(() => _jobs.Gauge.Current == 2);
        Acquirer.Cancel();

        await retry.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        Acquirer.Status!.IsRunning.Should().BeFalse();
        Acquirer.Plan.Rows.Should().NotContain(r => r.State == ArchiveState.Have);
    }

    [Fact]
    public async Task TheTokenOfARetryStopsItToo()
    {
        using var page = Acquirer.Attach();
        await RunWithFailures(1, 0);
        _jobs.Failures.Clear();
        _jobs.Gate = new TaskCompletionSource().Task;
        using var closed = new CancellationTokenSource();

        var retry = Acquirer.Retry(null, null, closed.Token);
        await WaitFor(() => _jobs.Gauge.Current == 1);
        closed.Cancel();

        await retry.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AHalfWrittenFileOfAFailedAttemptIsNotTheDestinationOfTheRetry()
    {
        using var page = Acquirer.Attach();
        var (plan, contents) = HttpRows(1);
        _jobs.Failures[Hash64(contents[0])] = new IOException("cut off");
        _jobs.Partials[Hash64(contents[0])] = [1, 2];
        await Acquirer.Run(plan, null, CancellationToken.None);
        var firstTry = _jobs.Destinations.Single();
        firstTry.FileExists.Should().BeFalse();
        _jobs.Failures.Clear();
        _jobs.Partials.Clear();

        await Acquirer.Retry(null, null, CancellationToken.None);

        _jobs.Destinations.Should().HaveCount(2);
        _jobs.Destinations[1].Should().NotBe(firstTry);
        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Have);
    }
}
