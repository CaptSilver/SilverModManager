using DynamicData;
using SilverModManager.Jobs.Tests;
using SilverModManager.Jobs.Tests.TestInfrastructure;
using FluentAssertions;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.Backend.Tests.Jobs.Unit;

public class JobMonitorTrackingTests : AJobsTest
{
    [Fact]
    public async Task Should_Filter_ChangeSet_By_Job_Type()
    {
        // Arrange
        var progressJob = new ProgressReportingJob(StepCount: 2, StepDelay: TimeSpan.FromMilliseconds(10));
        var simpleJob = new SimpleTestJob();
        
        var progressJobChanges = new List<IChangeSet<IJob, JobId>>();
        var simpleJobChanges = new List<IChangeSet<IJob, JobId>>();
        
        // Act
        using var progressSubscription = JobMonitor.GetObservableChangeSet<ProgressReportingJob>()
            .Subscribe(changeSet => progressJobChanges.Add(changeSet));
        
        using var simpleSubscription = JobMonitor.GetObservableChangeSet<SimpleTestJob>()
            .Subscribe(changeSet => simpleJobChanges.Add(changeSet));
        
        var progressTask = JobMonitor.Begin<ProgressReportingJob, double>(progressJob);
        var simpleTask = JobMonitor.Begin<SimpleTestJob, string>(simpleJob);
        
        await Task.WhenAll(progressTask.Job.WaitAsync(TestContext.Current.CancellationToken), simpleTask.Job.WaitAsync(TestContext.Current.CancellationToken));
        
        // Assert
        progressJobChanges.Should().NotBeEmpty();
        simpleJobChanges.Should().NotBeEmpty();

        // Verify that each filtered stream only contains jobs of the correct type
        var progressJobs = progressJobChanges.SelectMany(cs => cs).Select(change => change.Current);
        var simpleJobs = simpleJobChanges.SelectMany(cs => cs).Select(change => change.Current);

        progressJobs.All(job => job.Definition is ProgressReportingJob).Should().BeTrue();
        simpleJobs.All(job => job.Definition is SimpleTestJob).Should().BeTrue();
    }

    [Fact]
    public async Task Should_Emit_ChangeSet_Events_For_Job_Lifecycle()
    {
        // Arrange
        var job = new SimpleTestJob();
        var changeSets = new List<IChangeSet<IJob, JobId>>();
        var changeSignal = new AutoResetEvent(false);
        
        // Act
        using var subscription = JobMonitor.GetObservableChangeSet<SimpleTestJob>()
            .Subscribe(changeSet => 
            {
                changeSets.Add(changeSet);
                changeSignal.Set();
            });
        
        var task = JobMonitor.Begin<SimpleTestJob, string>(job);
        
        // Wait for initial change set
        changeSignal.WaitOne(TimeSpan.FromSeconds(2));
        
        await task;
        
        // Wait for potential removal change set
        changeSignal.WaitOne(TimeSpan.FromSeconds(2));
        
        // Assert
        changeSets.Should().NotBeEmpty();

        // Should have at least one Add operation
        var hasAddOperation = changeSets.Any(cs => cs.Any(change => change.Reason == ChangeReason.Add));
        hasAddOperation.Should().BeTrue();
    }

    [Fact]
    public async Task Snapshot_ListsJobsWhileOtherThreadsStartAndFinishThem()
    {
        // The churning jobs can finish before a snapshot catches any of them, so the one job the snapshot
        // must see is held open until the churn is over.
        using var held = new ManualResetEventSlim();
        var heldJob = JobMonitor.Begin<ProgressReportingJob, double>(new ProgressReportingJob(1, TimeSpan.Zero, held));
        var heldId = heldJob.Job.Id;

        var starting = Task.Run(async () =>
        {
            for (var i = 0; i < 300; i++)
                await JobMonitor.Begin<SimpleTestJob, string>(new SimpleTestJob()).Job.WaitAsync(TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        var seen = 0;
        do
        {
            // Enumerating Jobs here would throw "Collection was modified" from time to time.
            seen += JobMonitor.Snapshot().Count(job => job.Id == heldId);
            Thread.Yield();
        } while (!starting.IsCompleted);

        await starting;
        held.Set();
        await heldJob.Job.WaitAsync(TestContext.Current.CancellationToken);
        seen.Should().BeGreaterThan(0, "the snapshot has to see the job that stays open while the other thread churns");
    }
}
