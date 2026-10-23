using System.Collections.Immutable;
using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Platform.Proton.Tests.Launch;

/// <summary>
/// A session with no <see cref="StopHandler"/> is stopped by its owner calling <see cref="LaunchSession.RequestStop"/>,
/// which has to behave like the first termination signal.
/// </summary>
public class LaunchSessionRequestStopTests : IDisposable
{
    private readonly string _runtimeDir = Directory.CreateTempSubdirectory("silvermod-requeststop-").FullName;
    private readonly List<string> _events = [];
    private readonly List<LaunchSession> _sessions = [];
    private readonly GameInstallation _installation;
    private readonly LaunchContext _context;
    private readonly IGameProcessFactory _processes = Substitute.For<IGameProcessFactory>();
    private readonly ILaunchSynchronizer _synchronizer = Substitute.For<ILaunchSynchronizer>();
    private readonly IInstallationLockFactory _lockFactory = Substitute.For<IInstallationLockFactory>();
    private readonly TaskCompletionSource<GameProcessResult> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _gameStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public LaunchSessionRequestStopTests()
    {
        _installation = Installation();
        _context = new LaunchContext(_installation, LoadoutId.From(1), "My Loadout", []);
        _lockFactory.Create(Arg.Any<string>(), Arg.Any<string>())
            .Returns(call => new InstallationLock(InstallationLock.PathFor(_runtimeDir, call.ArgAt<string>(0), call.ArgAt<string>(1))));
        _synchronizer.Synchronize(Arg.Any<LoadoutId>()).Returns(_ =>
        {
            _events.Add("sync");
            return Task.CompletedTask;
        });

        var process = Substitute.For<IGameProcess>();
        process.WaitAsync(Arg.Any<CancellationToken>()).Returns(_ => _exit.Task);
        process.When(p => p.Terminate()).Do(_ =>
        {
            _events.Add("terminate");
            _exit.TrySetResult(new GameProcessResult(137, null));
        });
        _processes.Start(Arg.Any<GameProcessSpec>(), Arg.Any<GameInstallation>()).Returns(_ =>
        {
            _events.Add("start");
            _gameStarted.TrySetResult();
            return process;
        });
    }

    public void Dispose()
    {
        foreach (var session in _sessions) session.Release();
        Directory.Delete(_runtimeDir, recursive: true);
    }

    private GameInstallation Installation()
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Test Game");
        var folder = FileSystem.Shared.FromUnsanitizedFullPath(_runtimeDir);
        var result = new GameLocatorResult
        {
            Game = game,
            Path = folder,
            Store = GameStore.Steam,
            StoreIdentifier = "42",
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            Platform = OSPlatform.Windows,
        };
        return new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, folder)));
    }

    private LaunchSession NewSession()
    {
        var session = new LaunchSession(_lockFactory, new ActiveLaunchSessions(), _synchronizer, _processes, [], Substitute.For<IDesktopNotifier>(), NullLogger<LaunchSession>.Instance, TextWriter.Null);
        _sessions.Add(session);
        return session;
    }

    private Task<LaunchResult> Run(LaunchSession session) => session.RunAsync(_context, () => new GameProcessSpec(["game.exe"]));

    [Fact]
    public async Task AStopWhileTheGameRuns_TerminatesIt_TakesItsChangesBack_AndIs143()
    {
        var session = NewSession();
        var run = Run(session);
        await _gameStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        session.RequestStop();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30));

        result.Should().Be(new LaunchResult(LaunchSession.StoppedExitCode, null));
        _events.Should().Equal("sync", "start", "terminate", "sync");
    }

    [Fact]
    public async Task AStopBeforeTheRunBegins_NeverStartsTheGame_AndIs143()
    {
        var session = NewSession();
        session.RequestStop();

        var result = await Run(session);

        result.Should().Be(new LaunchResult(LaunchSession.StoppedExitCode, null));
        _events.Should().NotContain("start");
    }

    [Fact]
    public async Task AStopWhileApplying_LetsTheApplyFinish_SkipsTheGame_AndIs143()
    {
        var session = NewSession();
        var applying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _synchronizer.Synchronize(Arg.Any<LoadoutId>()).Returns(_ =>
        {
            _events.Add("sync");
            if (Interlocked.Increment(ref calls) != 1) return Task.CompletedTask;
            applying.SetResult();
            return release.Task;
        });
        var run = Run(session);
        await applying.Task.WaitAsync(TimeSpan.FromSeconds(30));

        session.RequestStop();
        release.SetResult();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30));

        result.Should().Be(new LaunchResult(LaunchSession.StoppedExitCode, null));
        _events.Should().Equal("sync", "sync");
    }

    [Fact]
    public async Task ASecondStop_TerminatesOnce()
    {
        var session = NewSession();
        var run = Run(session);
        await _gameStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        session.RequestStop();
        session.RequestStop();
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        _events.Count(e => e == "terminate").Should().Be(1);
    }
}
