using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using SilverModManager.CrossPlatform;
using NSubstitute;
using SilverModManager.Abstractions.Cli;
using SilverModManager.ProxyConsole;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.Sdk.Settings;
using SilverModManager.SingleProcess;
using Spectre.Console.Testing;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// What a verb that throws looks like to the person at the terminal: a sentence, never a stack trace, and an exit
/// code that says whether it was a refusal or a crash, in the process that ran the verb and through the client.
/// </summary>
public sealed class CliErrorTests : IAsyncLifetime
{
    private const int TimeoutMs = 120_000;

    private readonly string _directory = Directory.CreateTempSubdirectory("cli-error-tests").FullName;
    private CliServer? _server;

    private sealed class TestRefusal(string message) : UserFacingException(message)
    {
        public override string Title => "Test refusal";
    }

    public static class TestVerbs
    {
        [Verb("test-refuse", "Throws a refusal")]
        public static Task<int> Refuse([Injected] IRenderer renderer) =>
            throw new TestRefusal("The game is still running. Close it and try again.");

        [Verb("test-crash", "Throws a bug")]
        public static Task<int> Crash([Injected] IRenderer renderer) =>
            throw new InvalidOperationException("sequence contains no elements");

        [Verb("test-crash-late", "Throws a bug after its first await")]
        public static async Task<int> CrashLate([Injected] IRenderer renderer)
        {
            await Task.Yield();
            throw new InvalidOperationException("sequence contains no elements");
        }

        [Verb("test-ok", "Succeeds")]
        public static Task<int> Ok([Injected] IRenderer renderer) => Task.FromResult(0);

        [Verb("test-option", "Takes an option whose parser throws")]
        public static Task<int> WithOption([Injected] IRenderer renderer, [Option("t", "thing", "A thing")] Thing thing) => Task.FromResult(0);
    }

    public sealed record Thing(string Name);

    /// <summary>
    /// A parser that breaks its contract by throwing instead of returning an error, as LoadoutParser once did.
    /// </summary>
    private sealed class ThrowingThingParser : IOptionParser<Thing>
    {
        public bool TryParse(string input, out Thing value, out string error) =>
            throw new InvalidOperationException("sequence contains no elements");
    }

    private static ServiceProvider Services(LevelRecorder? log = null) => new ServiceCollection()
        .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(log ?? new LevelRecorder()))
        .AddDefaultRenderers()
        .AddVerb(() => TestVerbs.Refuse)
        .AddVerb(() => TestVerbs.Crash)
        .AddVerb(() => TestVerbs.CrashLate)
        .AddVerb(() => TestVerbs.Ok)
        .AddVerb(() => TestVerbs.WithOption)
        .AddOptionParser<Thing, ThrowingThingParser>()
        .AddSingleton<CommandLineConfigurator>()
        .BuildServiceProvider();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_server is not null) await _server.StopAsync(CancellationToken.None);
        _server?.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private static string TextOf(LoggingRenderer renderer) =>
        string.Concat(renderer.Logs.OfType<Text>().Select(t => string.Format(t.Template, t.Arguments ?? [])));

    [Fact]
    public async Task ARefusalPrintsItsMessageOnly_AndExitsWithOne()
    {
        var services = Services();
        var renderer = new LoggingRenderer();

        var exitCode = await services.GetRequiredService<CommandLineConfigurator>().RunAsync(["test-refuse"], renderer, CancellationToken.None);

        exitCode.Should().Be(1);
        TextOf(renderer).Trim().Should().Be("The game is still running. Close it and try again.");
    }

    [Theory]
    [InlineData("test-crash")]
    [InlineData("test-crash-late")]
    public async Task ACrashPrintsNoStackTrace_PointsToTheLog_AndExitsWithMinusOne(string verb)
    {
        var services = Services();
        var renderer = new LoggingRenderer();

        var exitCode = await services.GetRequiredService<CommandLineConfigurator>().RunAsync([verb], renderer, CancellationToken.None);

        exitCode.Should().Be(-1);
        var text = TextOf(renderer);
        text.Should().Contain("Details are in the log at").And.Contain(LoggingSettings.GetLogBaseFolder(FileSystem.Shared).ToString());
        text.Should().NotContain("   at ").And.NotContain("InvalidOperationException").And.NotContain("sequence contains");
    }

    [Theory]
    [InlineData("test-refuse", LogLevel.Warning)]
    [InlineData("test-crash", LogLevel.Error)]
    public async Task AFailureIsLoggedWithItsExceptionAtTheLevelOfItsKind(string verb, LogLevel expected)
    {
        var log = new LevelRecorder();
        var services = Services(log);

        await services.GetRequiredService<CommandLineConfigurator>().RunAsync([verb], new LoggingRenderer(), CancellationToken.None);

        log.Entries.Should().ContainSingle(e => e.Exception != null).Which.Level.Should().Be(expected);
    }

    [Fact]
    public async Task AnOptionParserThatThrows_FailsTheCommandWithoutAStackTrace_AndLogsTheError()
    {
        var log = new LevelRecorder();
        var services = Services(log);
        var renderer = new LoggingRenderer();

        var exitCode = await services.GetRequiredService<CommandLineConfigurator>().RunAsync(["test-option", "-t", "x"], renderer, CancellationToken.None);

        exitCode.Should().Be(1);
        TextOf(renderer).Should().Contain("Details are in the log at").And.NotContain("   at ").And.NotContain("InvalidOperationException");
        log.Entries.Should().ContainSingle(e => e.Exception != null).Which.Level.Should().Be(LogLevel.Error);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AnOptionParserThatThrows_ThroughTheClient_ExitsWithOne()
    {
        var client = await StartMain();
        var console = new TestConsole();

        var exitCode = await client.ExecuteCommand(["test-option", "-t", "x"], console);

        exitCode.Should().Be(1);
        console.Output.Should().Contain("Details are in the log at").And.NotContain("InvalidOperationException");
    }

    private async Task<CliClient> StartMain()
    {
        var services = Services();
        var syncPath = FileSystem.Shared.FromUnsanitizedFullPath(Path.Combine(_directory, "test.sync"));
        _server = new CliServer(NullLogger<CliServer>.Instance, services.GetRequiredService<CommandLineConfigurator>(), Substitute.For<ISettingsManager>(), new SyncFile(NullLogger<SyncFile>.Instance, syncPath), services);
        await _server.StartCliServerAsync();
        return new CliClient(NullLogger<CliClient>.Instance, new SyncFile(NullLogger<SyncFile>.Instance, syncPath), services);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task ARefusalThroughTheClientPrintsItsMessageOnly_AndExitsWithOne()
    {
        var client = await StartMain();
        var console = new TestConsole();

        var exitCode = await client.ExecuteCommand(["test-refuse"], console);

        exitCode.Should().Be(1);
        console.Output.Trim().Should().Be("The game is still running. Close it and try again.");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task ACrashThroughTheClientPrintsNoStackTrace_AndExitsWithMinusOne()
    {
        var client = await StartMain();
        var console = new TestConsole();

        var exitCode = await client.ExecuteCommand(["test-crash"], console);

        exitCode.Should().Be(-1);
        console.Output.Should().Contain("Details are in the log at").And.NotContain(" at System").And.NotContain("InvalidOperationException");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task ASuccessfulVerbThroughTheClientExitsWithZero()
    {
        var client = await StartMain();

        var exitCode = await client.ExecuteCommand(["test-ok"], new TestConsole());

        exitCode.Should().Be(0);
    }

    private sealed class LevelRecorder : ILoggerProvider
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Recording(Entries);
        public void Dispose() { }

        private sealed class Recording(List<(LogLevel, Exception?)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (entries) entries.Add((logLevel, exception));
            }
        }
    }
}
