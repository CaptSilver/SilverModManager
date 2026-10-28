using System.Collections.ObjectModel;
using FluentAssertions;
using NSubstitute;
using SilverModManager.App.UI.Diagnostics;
using SilverModManager.Sdk.Jobs;
using SilverModManager.UI.Sdk;

namespace SilverModManager.UI.Tests;

[Collection("UiOperation")]
public class UiStallWatchdogTests
{
    private static StallFingerprint FakeSample() => new(TimeSpan.Zero, true, TimeSpan.Zero, 0, 8, 0, 0, null, null);

    private static UiStallWatchdog Create(Func<Task> probe, List<StallFingerprint> stalls)
        => new(probe, FakeSample, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(50), stall => { lock (stalls) stalls.Add(stall); }, _ => { });

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
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var neverStarted = Create(() => Task.CompletedTask, []);
        neverStarted.Dispose();
        FluentActions.Invoking(neverStarted.Dispose).Should().NotThrow();

        var started = Create(() => new TaskCompletionSource().Task, []);
        started.Start();
        started.Dispose();
        FluentActions.Invoking(started.Dispose).Should().NotThrow();
    }

    [Fact]
    public async Task SamplerThatThrows_ReportsTheErrorAndKeepsWatching()
    {
        var errors = new List<Exception>();
        var stalls = new List<StallFingerprint>();
        var probes = 0;
        var calls = 0;
        using var watchdog = new UiStallWatchdog(
            () => Interlocked.Increment(ref probes) <= 2 ? new TaskCompletionSource().Task : Task.CompletedTask,
            () => Interlocked.Increment(ref calls) == 1 ? throw new ArgumentException("collection changed") : FakeSample(),
            TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(50),
            stall => { lock (stalls) stalls.Add(stall); },
            ex => { lock (errors) errors.Add(ex); });
        watchdog.Start();
        await Task.Delay(400);

        watchdog.LoopThread.IsAlive.Should().BeTrue();
        lock (errors) errors.Should().ContainSingle().Which.Should().BeOfType<ArgumentException>();
        lock (stalls) stalls.Should().NotBeEmpty();
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
    }

    [Fact]
    public void OutOfOrderScopeEnds_DoNotBringBackAnEndedOperation()
    {
        var x = UiOperation.Enter("X");
        var y = UiOperation.Enter("Y");
        x.Dispose();
        y.Dispose();

        UiOperation.Current?.Name.Should().NotBe("X");
        UiOperation.Current?.Name.Should().NotBe("Y");
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
