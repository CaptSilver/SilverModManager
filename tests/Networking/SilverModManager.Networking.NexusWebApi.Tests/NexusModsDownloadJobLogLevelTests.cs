using FluentAssertions;
using Microsoft.Extensions.Logging;
using SilverModManager.Networking.HttpDownloader;
using SilverModManager.Sdk.Errors;
using Xunit;

namespace SilverModManager.Networking.NexusWebApi.Tests;

/// <summary>
/// Every Error-level log line opens a crash dialog. A download that gave up with a plain reason is shown on its row
/// instead, so it must not be logged as an Error.
/// </summary>
public class NexusModsDownloadJobLogLevelTests
{
    private sealed class LevelLogger : ILogger
    {
        public LogLevel? Level;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Level = logLevel;
    }

    private static LogLevel? LevelOf(Exception failure)
    {
        var logger = new LevelLogger();
        logger.LogFailure(failure, "Exception while downloading file");
        return logger.Level;
    }

    [Fact]
    public void AHostThatKeptDroppingTheConnection_IsLoggedAsAWarning()
    {
        var failure = new ConnectionDroppedException("cdn.test", NexusMods.Paths.Size.FromLong(10), DynamicData.Kernel.Optional<NexusMods.Paths.Size>.None, new IOException("unexpected EOF"));

        LevelOf(failure).Should().Be(LogLevel.Warning);
    }

    [Fact]
    public void ADownloadWrappedInAnotherExceptionKeepsItsPlainReasonsLevel()
    {
        var failure = new AggregateException(new DownloadStalledException("cdn.test"));

        LevelOf(failure).Should().Be(LogLevel.Warning);
    }

    [Fact]
    public void AnUnexpectedFailure_IsLoggedAsAnError()
    {
        LevelOf(new InvalidOperationException("bug")).Should().Be(LogLevel.Error);
    }
}
