using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Settings;
using SilverModManager.SingleProcess;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// What a forwarded launch that throws in the main process looks like to the <c>run</c> process: a refusal as its own
/// sentence with exit code 1, a crash as the pointer to the log with -1, and the exception in the main process's log once.
/// </summary>
public sealed class ForwardedLaunchFailureTests : IAsyncLifetime
{
    private const int TimeoutMs = 120_000;

    private readonly string _directory = Directory.CreateTempSubdirectory("forwarded-launch-failure-tests").FullName;
    private readonly FailingHandler _handler = new();
    private readonly List<(LogLevel Level, Exception? Exception)> _log = [];
    private CliServer? _server;

    private sealed class TestRefusal(string message) : UserFacingException(message)
    {
        public override string Title => "Test refusal";
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_server is not null) await _server.StopAsync(CancellationToken.None);
        _server?.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private async Task<CliClient> StartMain()
    {
        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<IForwardedLaunchHandler>(_handler)
            .BuildServiceProvider();
        var syncPath = FileSystem.Shared.FromUnsanitizedFullPath(Path.Combine(_directory, "test.sync"));
        _server = new CliServer(new RecordingLogger<CliServer>(_log), new CommandLineConfigurator(services, [], []), Substitute.For<ISettingsManager>(), new SyncFile(NullLogger<SyncFile>.Instance, syncPath), services);
        await _server.StartCliServerAsync();
        return new CliClient(NullLogger<CliClient>.Instance, new SyncFile(NullLogger<SyncFile>.Instance, syncPath), services);
    }

    private static ForwardedLaunch Launch() => new("My Loadout", "1091500", ["game.exe"], new Dictionary<string, string>(), "/games/peak");

    [Fact(Timeout = TimeoutMs)]
    public async Task ARefusalWhileRunning_ComesBackAsItsSentence_WithExitCodeOne()
    {
        _handler.RunFailure = new TestRefusal("The game is still running. Close it and try again.");
        var client = await StartMain();

        var outcome = await client.ForwardLaunchAsync(Launch(), CancellationToken.None);

        outcome.Should().Be(new ForwardedLaunchOutcome.Exited(1, "The game is still running. Close it and try again."));
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task ACrashWhileRunning_ComesBackAsThePointerToTheLog_WithExitCodeMinusOne_AndIsLoggedOnce()
    {
        _handler.RunFailure = new InvalidOperationException("no such program");
        var client = await StartMain();

        var outcome = await client.ForwardLaunchAsync(Launch(), CancellationToken.None);

        outcome.Should().Be(new ForwardedLaunchOutcome.Exited(-1, CliErrors.CrashPointer));
        _log.Should().ContainSingle(e => e.Exception != null).Which.Level.Should().Be(LogLevel.Error);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task ARefusalWhileDeciding_IsDeclinedWithItsSentence_AndLoggedOnce()
    {
        _handler.AdmitFailure = new TestRefusal("The game is still running. Close it and try again.");
        var client = await StartMain();

        var outcome = await client.ForwardLaunchAsync(Launch(), CancellationToken.None);

        outcome.Should().Be(new ForwardedLaunchOutcome.Declined("The game is still running. Close it and try again."));
        _log.Should().ContainSingle(e => e.Exception != null).Which.Level.Should().Be(LogLevel.Warning);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task ACrashWhileDeciding_IsDeclinedWithThePointerToTheLog_NotTheMessage()
    {
        _handler.AdmitFailure = new InvalidOperationException("sequence contains no elements");
        var client = await StartMain();

        var outcome = await client.ForwardLaunchAsync(Launch(), CancellationToken.None);

        outcome.Should().Be(new ForwardedLaunchOutcome.Declined(CliErrors.CrashPointer));
        _log.Should().ContainSingle(e => e.Exception != null).Which.Level.Should().Be(LogLevel.Error);
    }

    private sealed class FailingHandler : IForwardedLaunchHandler
    {
        public Exception? AdmitFailure { get; set; }
        public Exception? RunFailure { get; set; }

        public Task<ForwardedLaunchAdmission> AdmitAsync(ForwardedLaunch launch)
        {
            if (AdmitFailure is not null) throw AdmitFailure;
            return Task.FromResult(ForwardedLaunchAdmission.Accepted(_ => throw RunFailure!));
        }
    }

    private sealed class RecordingLogger<T>(List<(LogLevel, Exception?)> entries) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (entries) entries.Add((logLevel, exception));
        }
    }
}
