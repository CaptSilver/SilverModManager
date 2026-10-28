using System.Collections.ObjectModel;
using FluentAssertions;
using NSubstitute;
using SilverModManager.App.UI.Diagnostics;
using SilverModManager.Sdk.Jobs;
using SilverModManager.UI.Sdk;

namespace SilverModManager.UI.Tests;

public class UiStallWatchdogTests
{
    private static StallFingerprint FakeSample() => new(TimeSpan.Zero, true, TimeSpan.Zero, 0, 8, 0, 0, null, null);

    private static UiStallWatchdog Create(Func<Task> probe, List<StallFingerprint> stalls)
        => new(probe, FakeSample, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(50), stall => { lock (stalls) stalls.Add(stall); });

    [Fact]
    public async Task LateProbe_ReportsOneStallWithItsFingerprint()
    {
        var stalls = new List<StallFingerprint>();
        var gate = new TaskCompletionSource();
        var first = true;
        using var watchdog = Create(() =>
        {
            if (!first) return Task.CompletedTask;
            first = false;
            return gate.Task;
        }, stalls);
        watchdog.Start();
        await Task.Delay(300);
        gate.SetResult();
        await Task.Delay(100);

        lock (stalls)
        {
            stalls.Should().HaveCount(2);
            stalls[0].StillPending.Should().BeTrue();
            stalls[0].Late.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(50));
            stalls[1].StillPending.Should().BeFalse();
            stalls[1].Late.Should().BeGreaterThan(TimeSpan.FromMilliseconds(250));
        }
    }

    [Fact]
    public async Task ProbeThatNeverReturns_IsLoggedWhileItIsStillPending()
    {
        var stalls = new List<StallFingerprint>();
        using var watchdog = Create(() => new TaskCompletionSource().Task, stalls);
        watchdog.Start();
        await Task.Delay(200);

        lock (stalls) stalls.Should().ContainSingle().Which.StillPending.Should().BeTrue();
    }

    [Fact]
    public async Task PromptProbes_ReportNothing()
    {
        var stalls = new List<StallFingerprint>();
        using var watchdog = Create(() => Task.CompletedTask, stalls);
        watchdog.Start();
        await Task.Delay(300);

        lock (stalls) stalls.Should().BeEmpty();
    }

    [Fact]
    public void Dispose_StopsTheThread()
    {
        var watchdog = Create(() => new TaskCompletionSource().Task, []);
        watchdog.Start();
        var thread = watchdog.LoopThread;
        watchdog.Dispose();

        thread.Join(TimeSpan.FromSeconds(2)).Should().BeTrue();
    }

    [Fact]
    public void StallDuringASuspendedCommand_ReportsTheOperationWithItsAge()
    {
        var jobs = Substitute.For<IJobMonitor>();
        jobs.Jobs.Returns(new ReadOnlyObservableCollection<IJob>(new ObservableCollection<IJob>()));

        using (UiOperation.Enter("SuspendedCommand"))
        {
            Thread.Sleep(100);
            var fingerprint = UiStallWatchdog.SampleNow(TimeSpan.Zero, 0, jobs);

            fingerprint.UiOperation.Should().Be("SuspendedCommand");
            fingerprint.UiOperationAge.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(100));
        }

        UiOperation.Current.Should().BeNull();
    }

    [Fact]
    public void SampleNow_CountsOnlyRunningJobs()
    {
        var running = Substitute.For<IJob>();
        running.Status.Returns(JobStatus.Running);
        var done = Substitute.For<IJob>();
        done.Status.Returns(JobStatus.Completed);
        var jobs = Substitute.For<IJobMonitor>();
        jobs.Jobs.Returns(new ReadOnlyObservableCollection<IJob>(new ObservableCollection<IJob> { running, done, running }));

        UiStallWatchdog.SampleNow(TimeSpan.Zero, 0, jobs).RunningJobs.Should().Be(2);
    }
}
