using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.Networking.HttpDownloader.Tests;

public class HttpDownloadJobUnreachableHostTests
{
    private readonly TemporaryFileManager _temporaryFileManager;
    private readonly IJobMonitor _monitor;

    public HttpDownloadJobUnreachableHostTests(IServiceProvider serviceProvider)
    {
        _temporaryFileManager = serviceProvider.GetRequiredService<TemporaryFileManager>();
        _monitor = serviceProvider.GetRequiredService<IJobMonitor>();
    }

    [Fact]
    public async Task AHostThatNeverAnswers_FailsTheJobNamingTheHostAndNotTheQuery()
    {
        using var listener = new SilentListener();
        var uri = listener.UriWithSignature("SECRET");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        await using var destination = _temporaryFileManager.CreateFile();
        var job = new HttpDownloadJob
        {
            Client = client,
            Logger = NullLogger.Instance,
            Uri = uri,
            DownloadPageUri = uri,
            Destination = destination.Path,
        };

        var stopwatch = Stopwatch.StartNew();
        var task = _monitor.Begin<HttpDownloadJob, AbsolutePath>(job);
        var thrown = await FluentActions.Awaiting(async () => await task).Should().ThrowAsync<Exception>();
        stopwatch.Stop();

        task.Job.Status.Should().Be(JobStatus.Failed);
        thrown.Which.Should().NotBeAssignableTo<OperationCanceledException>();
        thrown.Which.Message.Should().Contain("127.0.0.1").And.NotContain("SECRET");
        // One attempt, not the job's retries stacked on top of the timeout
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task PausingWhileTheHostIsHanging_PausesTheJobInsteadOfFailingIt()
    {
        using var listener = new SilentListener();
        var uri = listener.UriWithSignature("SECRET");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        await using var destination = _temporaryFileManager.CreateFile();
        var job = new HttpDownloadJob
        {
            Client = client,
            Logger = NullLogger.Instance,
            Uri = uri,
            DownloadPageUri = uri,
            Destination = destination.Path,
        };
        var task = _monitor.Begin<HttpDownloadJob, AbsolutePath>(job);
        while (listener.Accepted == 0) await Task.Delay(10, TestContext.Current.CancellationToken);

        _monitor.Pause(task);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (task.Job.Status == JobStatus.Running && DateTime.UtcNow < deadline)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        task.Job.Status.Should().Be(JobStatus.Paused);

        // A paused job can still be cancelled cleanly
        _monitor.Cancel(task);
        await FluentActions.Awaiting(async () => await task).Should().ThrowAsync<OperationCanceledException>();
        task.Job.Status.Should().Be(JobStatus.Cancelled);
    }
}
