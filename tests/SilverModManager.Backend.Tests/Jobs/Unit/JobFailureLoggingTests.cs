using FluentAssertions;
using Microsoft.Extensions.Logging;
using SilverModManager.Backend.Jobs;
using SilverModManager.Jobs.Tests.TestInfrastructure;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.Backend.Tests.Jobs.Unit;

/// <summary>
/// A failed job someone awaits reaches that caller as an exception, which owns what the user sees. Only a job nobody
/// awaits has no one to report it, so only that one is logged as an error.
/// </summary>
public class JobFailureLoggingTests
{
    private sealed class CapturingLogger : ILogger<JobMonitor>
    {
        private readonly SemaphoreSlim _failureLogged = new(0);
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public Task FailureLogged => _failureLogged.WaitAsync(TimeSpan.FromSeconds(30));

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, exception));
            _failureLogged.Release();
        }
    }

    [Fact]
    public async Task AwaitingTheTask_LogsTheFailureAsAWarningWithTheException()
    {
        var log = new CapturingLogger();
        using var monitor = new JobMonitor(log);
        var signal = new ManualResetEventSlim();
        var boom = new InvalidOperationException("boom");
        var task = monitor.Begin<FailingJob, string>(new FailingJob(boom, signal));

        var awaiting = Assert.ThrowsAsync<InvalidOperationException>(async () => await task);
        signal.Set();
        await awaiting;
        await log.FailureLogged;

        log.Entries.Should().ContainSingle().Which.Should().Be((LogLevel.Warning, (Exception?)boom));
    }

    [Fact]
    public async Task WaitingOnTheJob_LogsTheFailureAsAWarning()
    {
        var log = new CapturingLogger();
        using var monitor = new JobMonitor(log);
        var signal = new ManualResetEventSlim();
        var task = monitor.Begin<FailingJob, string>(new FailingJob(new InvalidOperationException("boom"), signal));

        var waiting = Assert.ThrowsAsync<InvalidOperationException>(() => task.Job.WaitAsync());
        signal.Set();
        await waiting;
        await log.FailureLogged;

        log.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public async Task WaitingForTheResult_LogsTheFailureAsAWarning()
    {
        var log = new CapturingLogger();
        using var monitor = new JobMonitor(log);
        var signal = new ManualResetEventSlim();
        var task = monitor.Begin<FailingJob, string>(new FailingJob(new InvalidOperationException("boom"), signal));

        var waiting = Assert.ThrowsAsync<InvalidOperationException>(() => ((IJobWithResult<string>)task.Job).WaitForResult());
        signal.Set();
        await waiting;
        await log.FailureLogged;

        log.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public async Task ATaskNobodyAwaits_LogsTheFailureAsAnError()
    {
        var log = new CapturingLogger();
        using var monitor = new JobMonitor(log);
        var boom = new InvalidOperationException("boom");

        _ = monitor.Begin<FailingJob, string>(new FailingJob(boom));
        await log.FailureLogged;

        log.Entries.Should().ContainSingle().Which.Should().Be((LogLevel.Error, (Exception?)boom));
    }
}
