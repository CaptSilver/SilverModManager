using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NLog;
using Target = NLog.Targets.Target;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App;
using SilverModManager.Platform.Proton.Launch;

namespace SilverModManager.CLI.Tests;

public class LoggingSetupTests
{
    [Fact]
    public void ACallerIsNotBlockedWhileTheSinkIsStuckAndTheQueueIsFull()
    {
        // More than the queue limit, so the overflow policy is what decides whether the caller waits or a line goes.
        const int count = 25_000;
        var sink = new GatedTarget();
        using var factory = new LogFactory { Configuration = LoggingSetup.CreateConfiguration([sink]) };
        var logger = factory.GetLogger("test");

        // The first line parks the writer inside the sink, the rest pile up behind it like a saturated disk.
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < count; i++) logger.Info("line {0}", i);
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3), "logging only enqueues, the sink is still blocked");
        sink.Gate.Set();
        factory.Flush(TimeSpan.FromSeconds(30));
        sink.Lines.Should().Equal(Enumerable.Range(0, count).Select(i => $"line {i}"), "a stuck sink delays lines, it must not lose them");
    }

    [Fact]
    public void FlushWritesEveryQueuedLineInOrder()
    {
        var sink = new GatedTarget();
        sink.Gate.Set();
        using var factory = new LogFactory { Configuration = LoggingSetup.CreateConfiguration([sink]) };
        var logger = factory.GetLogger("test");

        for (var i = 0; i < 20_000; i++) logger.Info("line {0}", i);
        factory.Flush(TimeSpan.FromSeconds(30));

        sink.Lines.Should().Equal(Enumerable.Range(0, 20_000).Select(i => $"line {i}"));
    }

    [Fact]
    public void ShutdownWritesTheLinesStillQueued()
    {
        var sink = new GatedTarget();
        var factory = new LogFactory { Configuration = LoggingSetup.CreateConfiguration([sink]) };
        var logger = factory.GetLogger("test");
        for (var i = 0; i < 100; i++) logger.Info("line {0}", i);

        sink.Gate.Set();
        LoggingSetup.Shutdown(factory);

        sink.Lines.Count.Should().Be(100);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void ThePerFileCategoryIsHiddenAtTraceUnlessRequested(bool requested, int expected)
    {
        var sink = new GatedTarget();
        sink.Gate.Set();
        using var factory = new LogFactory { Configuration = LoggingSetup.CreateConfiguration([sink]) };
        using var provider = new ServiceCollection()
            .AddLogging(builder => LoggingSetup.ConfigureBuilder(builder, factory, Microsoft.Extensions.Logging.LogLevel.Trace, requested))
            .BuildServiceProvider();
        var loggers = provider.GetRequiredService<ILoggerFactory>();

        loggers.CreateLogger(SynchronizerLogging.PerFileCategory).LogTrace("per file");
        loggers.CreateLogger("Other").LogTrace("summary");
        loggers.CreateLogger("Microsoft.Something").LogDebug("framework chatter");
        factory.Flush(TimeSpan.FromSeconds(10));

        sink.Lines.Count(m => m == "per file").Should().Be(expected);
        sink.Lines.Should().Contain("summary", "only the per-file category is filtered");
        sink.Lines.Should().NotContain("framework chatter", "the framework categories stay at Warning");
    }

    [Fact]
    public void TheMinimumLevelStillApplies()
    {
        var sink = new GatedTarget();
        sink.Gate.Set();
        using var factory = new LogFactory { Configuration = LoggingSetup.CreateConfiguration([sink]) };
        using var provider = new ServiceCollection()
            .AddLogging(builder => LoggingSetup.ConfigureBuilder(builder, factory, Microsoft.Extensions.Logging.LogLevel.Information, true))
            .BuildServiceProvider();

        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Other");
        logger.LogDebug("too detailed");
        logger.LogInformation("kept");
        factory.Flush(TimeSpan.FromSeconds(10));

        sink.Lines.Should().Equal("kept");
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData(null, false)]
    public void ThePerFileCategoryIsRequestedByTheEnvironmentVariable(string? value, bool expected)
    {
        var values = value is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [SynchronizerLogging.PerFileEnvironmentVariable] = value };

        LoggingSetup.PerFileRequested(new DictionaryEnvironmentReader(values)).Should().Be(expected);
    }

    private sealed class GatedTarget : Target
    {
        public ManualResetEventSlim Gate { get; } = new(false);
        public ConcurrentQueue<string> Lines { get; } = new();

        private bool _waited;

        protected override void Write(LogEventInfo logEvent)
        {
            // Only the first line parks here, like a write stuck on a saturated disk. The wait is bounded so a
            // regression that blocks callers fails the test instead of hanging it.
            if (!_waited)
            {
                _waited = true;
                Gate.Wait(TimeSpan.FromSeconds(5));
            }

            Lines.Enqueue(logEvent.FormattedMessage);
        }
    }
}
