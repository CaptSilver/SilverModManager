using DynamicData;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Downloads;
using SilverModManager.Library.Tests.DownloadsService.Helpers;
using NexusMods.Paths;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.NexusModsApi;
using Xunit;

namespace SilverModManager.Library.Tests.DownloadsService;

/// <summary>Downloads that are not from Nexus Mods, such as the file hosts a modlist's archives come from, are listed too.</summary>
public class DownloadsServiceHostedJobTests(
    IJobMonitor jobMonitor,
    Library.DownloadsService service,
    IServiceProvider serviceProvider)
    : DownloadsServiceTestsBase(jobMonitor, service, serviceProvider)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static AbsolutePath Destination(string name) =>
        FileSystem.Shared.GetKnownPath(KnownPath.CurrentDirectory).Combine("test/downloads").Combine(name);

    private TestTrackedDownloadJob NewJob(string name = "Some Archive.7z") => new() { Destination = Destination("tmp-1234.part"), DisplayName = name };

    private IJobTask<TestTrackedDownloadJob, AbsolutePath> Start(TestTrackedDownloadJob job)
        => jobMonitor.Begin<TestTrackedDownloadJob, AbsolutePath>(job);

    private static async Task<T> Eventually<T>(Func<T?> probe) where T : class
    {
        var until = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < until)
        {
            if (probe() is { } found) return found;
            await Task.Delay(10);
        }
        throw new TimeoutException("The condition did not hold in time");
    }

    private static async Task Eventually(Func<bool> probe)
    {
        var until = DateTime.UtcNow + Timeout;
        while (!probe())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("The condition did not hold in time");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task AHostJobIsListedWithItsNameSourceAndPage()
    {
        using var all = service.AllDownloads.AsObservableCache();
        var job = NewJob() with { GroupName = "Some Modlist" };
        var task = Start(job);

        var info = await Eventually(() => all.Items.SingleOrDefault(d => d.Id == task.Job.Id));
        info.Name.Value.Should().Be("Some Archive.7z");
        info.SourceName.Value.Should().Be("MEGA");
        info.GroupName.Value.Should().Be("Some Modlist");
        info.DownloadPageUri.Value.Value.Should().Be(new Uri("https://mega.example/file/abc"));

        job.Outcome.SetResult(job.Destination);
        await task;
    }

    [Fact]
    public async Task AHostJobWithoutANameIsListedByItsFileName()
    {
        using var all = service.AllDownloads.AsObservableCache();
        var job = NewJob(name: "") with { Destination = Destination("Some_File-Name.7z") };
        var task = Start(job);

        var info = await Eventually(() => all.Items.SingleOrDefault(d => d.Id == task.Job.Id));
        info.Name.Value.Should().Be("Some File Name");

        job.Outcome.SetResult(job.Destination);
        await task;
    }

    [Fact]
    public async Task AHostJobShowsItsProgressAndSize()
    {
        using var all = service.AllDownloads.AsObservableCache();
        var job = NewJob();
        var task = Start(job);
        var info = await Eventually(() => all.Items.SingleOrDefault(d => d.Id == task.Job.Id));

        job.State.Report(250, 1000);
        task.Job.AsContext().SetPercent(Size.FromLong(250), Size.FromLong(1000));

        await Eventually(() => info.Progress.Value.Value > 0.2);
        await Eventually(() => info.DownloadedBytes.Value == Size.FromLong(250));
        info.FileSize.Value.Should().Be(Size.FromLong(1000));
        info.Progress.Value.Value.Should().BeApproximately(0.25, 0.001);
        info.Status.Value.Should().Be(JobStatus.Running);

        job.Outcome.SetResult(job.Destination);
        await task;
    }

    [Fact]
    public async Task ACompletedHostJobStaysListedAsCompleted()
    {
        using var all = service.AllDownloads.AsObservableCache();
        using var completed = service.CompletedDownloads.AsObservableCache();
        var job = NewJob();
        var task = Start(job);
        var info = await Eventually(() => all.Items.SingleOrDefault(d => d.Id == task.Job.Id));

        job.Outcome.SetResult(job.Destination);
        await task;

        await Eventually(() => completed.Items.SingleOrDefault(d => d.Id == task.Job.Id));
        info.Status.Value.Should().Be(JobStatus.Completed);
        // The monitor drops the job a moment after it completes, and that is when the time is recorded.
        await Eventually(() => info.CompletedAt.Value.HasValue);
        info.Progress.Value.Should().Be(Percent.One);
        all.Items.Should().ContainSingle(d => d.Id == task.Job.Id);
    }

    [Fact]
    public async Task AFailedHostJobStaysListedAsFailedWithItsReason()
    {
        using var all = service.AllDownloads.AsObservableCache();
        using var active = service.ActiveDownloads.AsObservableCache();
        var job = NewJob();
        var task = Start(job);
        var info = await Eventually(() => all.Items.SingleOrDefault(d => d.Id == task.Job.Id));

        job.Outcome.SetException(new InvalidOperationException("MEGA answered 509; download it in your browser"));
        var act = async () => await task;
        await act.Should().ThrowAsync<InvalidOperationException>();

        await Eventually(() => info.Status.Value == JobStatus.Failed);
        await Eventually(() => info.FailureReason.Value is not null);
        info.FailureReason.Value.Should().Be("MEGA answered 509; download it in your browser");
        all.Items.Should().ContainSingle(d => d.Id == task.Job.Id);
        active.Items.Should().NotContain(d => d.Id == task.Job.Id);
    }

    [Fact]
    public async Task CancellingFromTheServiceReachesTheHostJob()
    {
        using var all = service.AllDownloads.AsObservableCache();
        var job = NewJob();
        var task = Start(job);
        var info = await Eventually(() => all.Items.SingleOrDefault(d => d.Id == task.Job.Id));

        service.CancelDownload(info);

        var act = async () => await task;
        await act.Should().ThrowAsync<OperationCanceledException>();
        task.Job.Status.Should().Be(JobStatus.Cancelled);
        await Eventually(() => all.Items.All(d => d.Id != task.Job.Id));
    }

    [Fact]
    public async Task APauseIsNotOfferedForAJobThatCannotPause()
    {
        using var all = service.AllDownloads.AsObservableCache();
        var job = NewJob();
        var task = Start(job);
        var info = await Eventually(() => all.Items.SingleOrDefault(d => d.Id == task.Job.Id));
        info.SupportsPausing.Value.Should().BeFalse();

        service.PauseDownload(info);
        service.PauseAll();

        await Task.Delay(100);
        task.Job.Status.Should().Be(JobStatus.Running);

        job.Outcome.SetResult(job.Destination);
        await task;
    }

    [Fact]
    public async Task ANexusDownloadStillOffersPauseAndNamesItsSource()
    {
        using var all = service.AllDownloads.AsObservableCache();
        var context = _jobFactory.CreateAndStartDownloadJob(NexusModsGameId.From(1234u));
        context.WaitForJobsReady(Timeout).Should().BeTrue();

        var info = await Eventually(() => all.Items.SingleOrDefault(d => d.Id == context.JobTask.Job.Id));
        info.SupportsPausing.Value.Should().BeTrue();
        info.SourceName.Value.Should().Be("Nexus Mods");

        context.CancelJob();
        context.SignalJobsToStart();
    }

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services) => DIHelpers.ConfigureServices(services);
    }
}
