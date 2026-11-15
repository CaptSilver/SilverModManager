using FluentAssertions;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

public class SynchronizeLoadoutJobPublishFailureTests
{
    [Fact]
    public void SetStatus_SubscriberThrows_LogsTheFailureAtTraceAndKeepsGoing()
    {
        var log = new RecordingLogger();
        var job = new SynchronizeLoadoutJob(LoadoutId.From(1)) { Logger = log };
        job.StatusMessage.PropertyChanged += (_, _) => throw new InvalidOperationException("bound off-thread");

        var act = () => job.SetStatus("Collecting");

        act.Should().NotThrow("a faulty subscriber must not fail the apply");
        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Trace && e.Exception is InvalidOperationException);
    }

    [Fact]
    public void SetStatus_NoSubscriberFailure_LogsNothing()
    {
        var log = new RecordingLogger();
        var job = new SynchronizeLoadoutJob(LoadoutId.From(1)) { Logger = log };

        job.SetStatus("Collecting");

        log.Entries.Should().BeEmpty();
        job.StatusMessage.Value.Should().Be("Collecting");
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, exception));
    }
}
