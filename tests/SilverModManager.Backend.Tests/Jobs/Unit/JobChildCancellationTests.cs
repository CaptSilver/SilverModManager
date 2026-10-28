using FluentAssertions;
using Microsoft.Extensions.Logging;
using SilverModManager.Backend.Jobs;
using SilverModManager.Jobs.Tests.TestInfrastructure;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.Backend.Tests.Jobs.Unit;

/// <summary>
/// The user can cancel a child job (a download card) without touching its parent. The parent only sees the child's
/// cancellation arrive as an exception and has to treat it as a cancellation, not a failure worth a warning.
/// </summary>
public class JobChildCancellationTests
{
    private sealed class LevelRecorder : ILogger<JobMonitor>
    {
        public List<LogLevel> Levels { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Levels) Levels.Add(logLevel);
        }
    }

    [Fact]
    public async Task ACancelledChild_CancelsTheAwaitingParentWithoutAWarning()
    {
        var log = new LevelRecorder();
        using var monitor = new JobMonitor(log);
        var ready = new ManualResetEventSlim();
        var child = monitor.Begin<WaitForCancellationJob, string>(new WaitForCancellationJob(ready));
        var parent = monitor.Begin<SimpleTestJob, string>(new SimpleTestJob(), async _ => await child);
        ready.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).Should().BeTrue();

        monitor.Cancel(child);

        await FluentActions.Awaiting(async () => await parent).Should().ThrowAsync<OperationCanceledException>();
        parent.Job.Status.Should().Be(JobStatus.Cancelled);
        log.Levels.Should().NotContain(level => level >= LogLevel.Warning);
    }
}
