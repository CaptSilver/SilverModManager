using FluentAssertions;
using SilverModManager.Jobs.Tests;
using SilverModManager.Jobs.Tests.TestInfrastructure;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.Backend.Tests.Jobs.Unit;

/// <summary>
/// An <see cref="OperationCanceledException"/> only means the user cancelled when the job's own token was cancelled.
/// HttpClient throws one for its own timeout, and that has to surface as a failure instead of vanishing as a cancellation.
/// </summary>
public class JobTimeoutTests : AJobsTest
{
    [Fact]
    public async Task ACancellationExceptionWithoutACancelledToken_FailsTheJob()
    {
        var timeout = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout", new TimeoutException());

        var task = JobMonitor.Begin<FailingJob, string>(new FailingJob(timeout));

        var thrown = await FluentActions.Awaiting(() => task.Job.WaitAsync()).Should().ThrowAsync<TaskCanceledException>();
        thrown.Which.Should().BeSameAs(timeout);
        task.Job.Status.Should().Be(JobStatus.Failed);
    }

    [Fact]
    public async Task ACancellationExceptionFromACancelledToken_StillCancelsTheJob()
    {
        var task = JobMonitor.Begin<SelfCancellingJob, string>(new SelfCancellingJob());

        await FluentActions.Awaiting(() => task.Job.WaitAsync()).Should().ThrowAsync<OperationCanceledException>();
        task.Job.Status.Should().Be(JobStatus.Cancelled);
    }
}
