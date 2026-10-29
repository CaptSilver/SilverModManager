using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using R3;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

public class ProgressLogTests
{
    private sealed class ListLogger : ILogger
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Enqueue($"{logLevel}: {formatter(state, exception)}");
    }

    private static Dictionary<GamePath, long> Sizes(int count)
        => Enumerable.Range(0, count).ToDictionary(i => new GamePath(LocationId.Game, $"f{i}.bin"), _ => 1024L);

    [Fact]
    public void TheGate_TakesAtMostOncePerInterval()
    {
        var gate = new ProgressLogGate(TimeSpan.FromHours(1));
        gate.TryTake().Should().BeFalse("the interval counts from the gate's creation");

        var open = new ProgressLogGate(TimeSpan.Zero);
        open.TryTake().Should().BeTrue();
        open.TryTake().Should().BeTrue("an interval of zero is always due");
    }

    [Fact]
    public void ReadingManyFiles_LogsTheStartOnceAndNothingMoreWithinTheInterval()
    {
        var logger = new ListLogger();
        var sizes = Sizes(50);
        var progress = new WholeReadProgress(null, logger, sizes, new ProgressLogGate(TimeSpan.FromHours(1)));

        progress.Begin();
        foreach (var path in sizes.Keys) progress.FileRead(path);

        logger.Lines.Should().ContainSingle().Which.Should().Be("Information: Reading 50 files (50 KB) to back up");
    }

    [Fact]
    public void ReadingFilesAfterTheInterval_LogsHowFarItGot()
    {
        var logger = new ListLogger();
        var sizes = Sizes(3);
        var progress = new WholeReadProgress(null, logger, sizes, new ProgressLogGate(TimeSpan.Zero));

        progress.Begin();
        foreach (var path in sizes.Keys) progress.FileRead(path);

        logger.Lines.Should().HaveCount(4);
        logger.Lines.Last().Should().Be("Information: Read 3 of 3 files (3 KB of 3 KB) to back up");
    }

    [Fact]
    public void TheJob_SeesFilesAndBytesDoneRiseToTheTotal()
    {
        var job = new SynchronizeLoadoutJob(SilverModManager.Sdk.Loadouts.LoadoutId.From(1UL));
        var seen = new List<SyncProgress>();
        using var _ = job.Progress.Where(p => p.Phase is not null).Subscribe(seen.Add);
        var sizes = Sizes(4);
        var progress = new WholeReadProgress(job, new ListLogger(), sizes, new ProgressLogGate(TimeSpan.FromHours(1)));

        progress.Begin();
        foreach (var path in sizes.Keys) progress.FileRead(path);

        seen.Should().OnlyContain(p => p.Phase == SyncPhases.ReadingBackups);
        seen.Select(p => p.FilesDone).Should().BeInAscendingOrder();
        seen.Last().Should().Be(new SyncProgress(SyncPhases.ReadingBackups, 4, 4, 4096, 4096));
    }
}

public class BackupPackProgressTests
{
    private static (SynchronizeLoadoutJob Job, List<SyncProgress> Seen, IDisposable Subscription) Watch()
    {
        var job = new SynchronizeLoadoutJob(SilverModManager.Sdk.Loadouts.LoadoutId.From(1UL));
        var seen = new List<SyncProgress>();
        var subscription = job.Progress.Where(p => p.Phase is not null).Subscribe(seen.Add);
        return (job, seen, subscription);
    }

    [Fact]
    public void PackedShare_FillsOnlyTheBytesTheStoreDidNotAlreadyHold()
    {
        var (job, seen, subscription) = Watch();
        using var _ = subscription;
        var progress = new BackupPackProgress(job, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, totalFiles: 10, totalBytes: 1000, bytesAccountedFor: 200, new ProgressLogGate(TimeSpan.FromHours(1)));

        progress.Report(0);
        progress.Report(0.5);
        progress.Report(1.0);

        seen.Select(p => p.BytesDone).Should().Equal(200, 600, 1000);
        seen.Should().OnlyContain(p => p.Phase == SyncPhases.BackingUp && p.FilesDone == 10 && p.FilesTotal == 10 && p.BytesTotal == 1000);
    }

    [Fact]
    public void AReportBehindTheLastOne_DoesNotMoveTheBarBack()
    {
        var (job, seen, subscription) = Watch();
        using var _ = subscription;
        var progress = new BackupPackProgress(job, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, 4, 400, 0, new ProgressLogGate(TimeSpan.FromHours(1)));

        progress.Report(0.75);
        progress.Report(0.25);
        progress.Report(1.0);

        seen.Select(p => p.BytesDone).Should().BeInAscendingOrder();
        seen.Last().BytesDone.Should().Be(400);
    }

    [Fact]
    public void NothingLeftToPack_StillEndsAtTheTotal()
    {
        var (job, seen, subscription) = Watch();
        using var _ = subscription;
        var progress = new BackupPackProgress(job, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, 3, 300, bytesAccountedFor: 300, new ProgressLogGate(TimeSpan.FromHours(1)));

        progress.Report(1.0);

        seen.Last().Should().Be(new SyncProgress(SyncPhases.BackingUp, 3, 3, 300, 300));
    }

    [Fact]
    public void TheLog_IsThrottledByTheGate()
    {
        var quiet = new ListLoggerForPack();
        var slow = new BackupPackProgress(null, quiet, 1, 100, 0, new ProgressLogGate(TimeSpan.FromHours(1)));
        for (var i = 1; i <= 10; i++) slow.Report(i / 10.0);
        quiet.Lines.Should().BeEmpty();

        var chatty = new ListLoggerForPack();
        var fast = new BackupPackProgress(null, chatty, 1, 100, 0, new ProgressLogGate(TimeSpan.Zero));
        fast.Report(0.5);
        chatty.Lines.Should().ContainSingle().Which.Should().Be("Archived 50 B of 100 B of the files to back up");
    }

    private sealed class ListLoggerForPack : ILogger
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }
}
