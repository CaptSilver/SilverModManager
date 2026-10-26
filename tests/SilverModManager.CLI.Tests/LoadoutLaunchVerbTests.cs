using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Platform.Proton.Verbs;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.SingleProcess;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Abstractions.Cli;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>silvermod loadout launch</c> with a stubbed tool manager: the verb must come back once the launch is under way,
/// never when the game exits.
/// </summary>
public class LoadoutLaunchVerbTests(IServiceProvider provider)
{
    private static readonly TimeSpan ShortWait = TimeSpan.FromMilliseconds(300);

    // A verb that waited for the game would never return in the tests whose game never exits; fail, don't hang.
    private static readonly TimeSpan VerbTimeout = TimeSpan.FromSeconds(10);

    private sealed class CapturingLogger : ILogger<LoadoutLaunchCommand>
    {
        public readonly List<(LogLevel Level, Exception? Exception)> Entries = [];
        private readonly TaskCompletionSource _logged = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Logged => _logged.Task;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, exception));
            if (exception is not null) _logged.TrySetResult();
        }
    }

    private async Task<Loadout.ReadOnly> SeedLoadout()
    {
        var connection = provider.GetRequiredService<IConnection>();
        using var tx = connection.BeginTransaction();
        var metadata = new GameInstallMetadata.New(tx)
        {
            Path = "/tmp/loadout-launch-tests-" + Guid.NewGuid(),
            Name = "Launch Verb Game",
            Store = GameStore.Unknown,
            Game = GameId.From("LaunchVerbGame-" + Guid.NewGuid()),
        };
        var loadout = new Loadout.New(tx)
        {
            Name = "Launch Verb Loadout " + Guid.NewGuid(),
            ShortName = "LV" + Guid.NewGuid().ToString("N")[..6],
            InstallationId = metadata,
            LoadoutKind = LoadoutKind.Default,
            Revision = 0,
            GameVersion = VanityVersion.From("Unknown"),
        };
        var result = await tx.Commit();
        return result.Remap(loadout);
    }

    private sealed record Setup(LoadoutLaunchCommand Command, Loadout.ReadOnly Loadout, IToolManager Tools, CapturingLogger Logger, LoggingRenderer Renderer);

    private async Task<Setup> Arrange(Func<Task<Loadout.ReadOnly>, Task<Loadout.ReadOnly>> toolBehaviour, bool gui = true, bool withRunTool = true)
    {
        var loadout = await SeedLoadout();
        var tools = Substitute.For<IToolManager>();
        var runTool = Substitute.For<IRunGameTool>();
        tools.GetTools(Arg.Any<Loadout.ReadOnly>()).Returns(withRunTool ? new ITool[] { runTool } : []);
        tools.RunTool(Arg.Any<ITool>(), Arg.Any<Loadout.ReadOnly>(), Arg.Any<IJobMonitor>(), Arg.Any<CancellationToken>())
            .Returns(_ => toolBehaviour(Task.FromResult(loadout)));

        var services = new ServiceCollection();
        if (gui) services.AddSingleton<WindowHost>();
        var logger = new CapturingLogger();
        var command = new LoadoutLaunchCommand(services.BuildServiceProvider(), tools, Substitute.For<IJobMonitor>(), logger);
        return new Setup(command, loadout, tools, logger, new LoggingRenderer());
    }

    private static string Output(LoggingRenderer renderer) => string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Template));

    [Fact]
    public async Task ALaunchThatOutlivesTheWaitReportsInProgressAndExitsZero()
    {
        var gameStillRunning = new TaskCompletionSource<Loadout.ReadOnly>();
        var setup = await Arrange(_ => gameStillRunning.Task);

        var exitCode = await setup.Command.RunAsync(setup.Renderer, setup.Loadout, ShortWait).WaitAsync(VerbTimeout);

        exitCode.Should().Be(0);
        var output = Output(setup.Renderer);
        output.Should().Contain("launch in progress").And.Contain(setup.Loadout.Name).And.Contain("applies loadout").And.Contain("Run `status` (the app_status tool over MCP) to follow it.");
        gameStillRunning.Task.IsCompleted.Should().BeFalse("the verb must not wait for the game to exit");
    }

    [Fact]
    public async Task ARefusalInsideTheWaitIsPrintedAndExitsOne()
    {
        var setup = await Arrange(_ => Task.FromException<Loadout.ReadOnly>(new GameLaunchException("The game is already running.")));

        var exitCode = await setup.Command.RunAsync(setup.Renderer, setup.Loadout, TimeSpan.FromSeconds(30));

        exitCode.Should().Be(1);
        Output(setup.Renderer).Should().Contain("The game is already running.");
    }

    [Fact]
    public async Task ACrashInsideTheWaitPointsAtTheLogWithoutAStackTrace()
    {
        var setup = await Arrange(_ => Task.FromException<Loadout.ReadOnly>(new InvalidOperationException("secret internal detail")));

        var exitCode = await setup.Command.RunAsync(setup.Renderer, setup.Loadout, TimeSpan.FromSeconds(30));

        exitCode.Should().Be(1);
        var output = Output(setup.Renderer);
        output.Should().Contain("log").And.NotContain("secret internal detail").And.NotContain(" at ");
    }

    [Fact]
    public async Task ALaunchThatEndsCleanlyInsideTheWaitStillSaysInProgress()
    {
        // Steam never starting the game is a desktop notification, not an exception, so a clean end proves nothing.
        var setup = await Arrange(loadout => loadout);

        var exitCode = await setup.Command.RunAsync(setup.Renderer, setup.Loadout, TimeSpan.FromSeconds(30));

        exitCode.Should().Be(0);
        Output(setup.Renderer).Should().Contain("launch in progress").And.Contain("status");
    }

    [Fact]
    public async Task AFaultAfterTheVerbHasReturnedIsLoggedNotLost()
    {
        var fault = new TaskCompletionSource<Loadout.ReadOnly>();
        var setup = await Arrange(_ => fault.Task);

        var exitCode = await setup.Command.RunAsync(setup.Renderer, setup.Loadout, ShortWait).WaitAsync(VerbTimeout);
        exitCode.Should().Be(0);
        fault.SetException(new GameLaunchException("Steam closed the game."));
        await setup.Logger.Logged.WaitAsync(TimeSpan.FromSeconds(10));

        setup.Logger.Entries.Should().ContainSingle(e => e.Exception is GameLaunchException).Which.Level.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public async Task AFaultAfterTheVerbHasReturnedIsNotAlsoAnUnobservedTaskException()
    {
        var fault = new GameLaunchException("Steam closed the game.");
        var unobserved = false;
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, args) =>
        {
            // Other tests' tasks can be finalized here too, so only this test's fault counts.
            if (args.Exception.Flatten().InnerExceptions.Any(e => ReferenceEquals(e, fault))) unobserved = true;
        };
        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            await RunAndFaultAfterReturn(fault);

            // The detached task is only finalized, and its exception only reported, once nothing references it.
            for (var i = 0; i < 3 && !unobserved; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(50);
            }

            unobserved.Should().BeFalse("the launch already logged its own failure");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }

    // Kept out of the test method so the JIT holds no reference to the setup when the test collects garbage.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private async Task RunAndFaultAfterReturn(Exception exception)
    {
        var game = new TaskCompletionSource<Loadout.ReadOnly>();
        var setup = await Arrange(_ => game.Task);
        (await setup.Command.RunAsync(setup.Renderer, setup.Loadout, ShortWait).WaitAsync(VerbTimeout)).Should().Be(0);
        game.SetException(exception);
        await setup.Logger.Logged.WaitAsync(TimeSpan.FromSeconds(10));
        // The log entry is written before the detached task finishes; give it a moment to end.
        await Task.Delay(100);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public async Task AWaitOutsideItsRangeIsRefusedInASentence(int seconds)
    {
        var setup = await Arrange(loadout => loadout);

        var exitCode = await setup.Command.RunAsync(setup.Renderer, setup.Loadout, TimeSpan.FromSeconds(seconds));

        exitCode.Should().Be(1);
        Output(setup.Renderer).Should().Contain("--wait");
        await setup.Tools.DidNotReceive().RunTool(Arg.Any<ITool>(), Arg.Any<Loadout.ReadOnly>(), Arg.Any<IJobMonitor>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OutsideTheWindowTheVerbRefusesWithoutStartingAnything()
    {
        var setup = await Arrange(loadout => loadout, gui: false);

        var exitCode = await setup.Command.RunAsync(setup.Renderer, setup.Loadout, ShortWait);

        exitCode.Should().Be(1);
        Output(setup.Renderer).Should().Contain("window").And.Contain("open");
        await setup.Tools.DidNotReceive().RunTool(Arg.Any<ITool>(), Arg.Any<Loadout.ReadOnly>(), Arg.Any<IJobMonitor>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AGameWithoutARunToolIsRefusedInASentence()
    {
        var setup = await Arrange(loadout => loadout, withRunTool: false);

        var exitCode = await setup.Command.RunAsync(setup.Renderer, setup.Loadout, ShortWait);

        exitCode.Should().Be(1);
        Output(setup.Renderer).Should().Contain("cannot be launched");
        await setup.Tools.DidNotReceive().RunTool(Arg.Any<ITool>(), Arg.Any<Loadout.ReadOnly>(), Arg.Any<IJobMonitor>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheVerbIsRegisteredAsLoadoutLaunchWithAWaitOption()
    {
        var setup = await Arrange(loadout => loadout);
        var services = new ServiceCollection()
            .AddLogging()
            .AddDefaultParsers()
            .AddSingleton(provider.GetRequiredService<IConnection>())
            .AddSingleton<CommandLineConfigurator>()
            .AddCLI()
            .AddModule("loadout", "Commands for managing a specific loadout")
            .AddSingleton<WindowHost>()
            .AddSingleton(setup.Command)
            .AddLaunchVerbs()
            .BuildServiceProvider();

        var renderer = new LoggingRenderer();
        var exitCode = await services.GetRequiredService<CommandLineConfigurator>()
            .RunAsync(["loadout", "launch", "--loadout", setup.Loadout.LoadoutId.Value.ToString(), "--wait", "1"], renderer, CancellationToken.None);

        exitCode.Should().Be(0);
        Output(renderer).Should().Contain("launch in progress");
    }
}
