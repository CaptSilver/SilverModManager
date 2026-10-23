using System.ComponentModel;
using System.Diagnostics;
using CliWrap;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NexusMods.Platform.Proton;
using NexusMods.Sdk;
using NSubstitute;

namespace NexusMods.Platform.Proton.Tests;

public class DesktopNotifierTests
{
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();
    private readonly CapturingLogger _logger = new();
    private readonly List<(Command Command, CancellationToken Token)> _runs = [];

    public DesktopNotifierTests()
    {
        _processRunner
            .RunAsync(Arg.Any<Command>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _runs.Add((call.Arg<Command>(), call.Arg<CancellationToken>()));
                return new CommandResult(0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            });
    }

    private DesktopNotifier Notifier() => new(_processRunner, _logger);

    [Fact]
    public async Task Notify_RunsNotifySendWithCriticalUrgencyAndAppIcon()
    {
        await Notifier().NotifyAsync("Cannot start game", "Mods are still deploying");

        var (command, _) = _runs.Should().ContainSingle().Subject;
        command.TargetFilePath.Should().Be("notify-send");
        command.Arguments.Should().Be(
            $"--app-name={ApplicationConstants.ProductName} --urgency=critical --icon={ApplicationConstants.ApplicationId} \"Cannot start game\" \"Mods are still deploying\""
        );
    }

    [Fact]
    public async Task Notify_GivesUpOnANotifySendThatNeverExits()
    {
        // Only the notifier's own timeout can end this run.
        _processRunner
            .RunAsync(Arg.Any<Command>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>())
                .ContinueWith<CommandResult>(_ => throw new OperationCanceledException(), TaskScheduler.Default));

        var stopwatch = Stopwatch.StartNew();
        await Notifier().NotifyAsync("a", "b").WaitAsync(TimeSpan.FromSeconds(30));
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeCloseTo(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1.5));
        _logger.Messages.Should().ContainSingle().Which.Should().Contain("notify-send");
    }

    [Fact]
    public async Task Notify_MissingNotifySend_LogsAndDoesNotThrow()
    {
        _processRunner
            .RunAsync(Arg.Any<Command>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<CommandResult>(_ => throw new Win32Exception(2, "No such file or directory"));

        var act = () => Notifier().NotifyAsync("a", "b");

        await act.Should().NotThrowAsync();
        _logger.Messages.Should().ContainSingle().Which.Should().Contain("notify-send");
    }

    [Fact]
    public async Task Notify_TimedOut_LogsAndDoesNotThrow()
    {
        _processRunner
            .RunAsync(Arg.Any<Command>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<CommandResult>(_ => throw new OperationCanceledException());

        var act = () => Notifier().NotifyAsync("a", "b");

        await act.Should().NotThrowAsync();
        _logger.Messages.Should().ContainSingle();
    }

    [Fact]
    public void Notify_StartsTheProcessAndReturnsWithoutWaitingForIt()
    {
        var gate = new TaskCompletionSource<CommandResult>();
        var started = 0;
        _processRunner
            .RunAsync(Arg.Any<Command>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                started++;
                return gate.Task;
            });

        // A run process may exit right after notifying, so notify-send has to be started by the time Notify returns.
        var returned = Task.Run(() => Notifier().Notify("a", "b")).Wait(TimeSpan.FromSeconds(10));

        returned.Should().BeTrue("Notify must not wait for notify-send to exit");
        started.Should().Be(1);
        gate.Task.IsCompleted.Should().BeFalse();
        gate.SetResult(new CommandResult(0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
    }

    private sealed class CapturingLogger : ILogger<DesktopNotifier>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
