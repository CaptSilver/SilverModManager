using System.Collections.Immutable;
using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.NexusModsApi;
using NSubstitute;

namespace NexusMods.Platform.Proton.Tests.Launch;

public class LaunchSessionTests : IDisposable
{
    private readonly string _runtimeDir = Directory.CreateTempSubdirectory("silvermod-launchsession-").FullName;
    private readonly List<string> _events = [];
    private readonly List<LaunchSession> _sessions = [];
    private readonly StringWriter _error = new();
    private readonly RecordingLogger _log;
    private readonly ActiveLaunchSessions _active = new();
    private readonly FakeSynchronizer _synchronizer;
    private readonly FakeProcessFactory _processes;
    private readonly IDesktopNotifier _notifier = Substitute.For<IDesktopNotifier>();
    private readonly IInstallationLockFactory _lockFactory = Substitute.For<IInstallationLockFactory>();
    private readonly List<ILaunchGate> _gates = [];
    private readonly GameInstallation _installation;
    private readonly LaunchContext _context;

    public LaunchSessionTests()
    {
        _installation = Installation();
        _context = new LaunchContext(_installation, LoadoutId.From(1), "My Loadout", [new WineDllOverride("winmm", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn])]);
        _log = new RecordingLogger();
        _synchronizer = new FakeSynchronizer(_events, this);
        _processes = new FakeProcessFactory(_events, this, _log);
        _lockFactory.Create(Arg.Any<string>(), Arg.Any<string>())
            .Returns(call => new InstallationLock(InstallationLock.PathFor(_runtimeDir, call.ArgAt<string>(0), call.ArgAt<string>(1))));
    }

    public void Dispose()
    {
        foreach (var session in _sessions) session.Release();
        Directory.Delete(_runtimeDir, recursive: true);
    }

    private string LockPath => InstallationLock.PathFor(_runtimeDir, "steam", "42");

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
        var session = new LaunchSession(_lockFactory, _active, _synchronizer, _processes, _gates, _notifier, _log, _error);
        _sessions.Add(session);
        return session;
    }

    private Task<LaunchResult> Run(LaunchSession session) => session.RunAsync(_context, () =>
    {
        _events.Add("resolve");
        return new GameProcessSpec(["game.exe"]);
    });

    private LaunchPhase? Phase()
    {
        using var reader = new InstallationLock(LockPath);
        return reader.ReadState()?.Phase;
    }

    private FakeGate AddGate(string name)
    {
        var gate = new FakeGate(name, _events, this);
        _gates.Add(gate);
        return gate;
    }

    [Fact]
    public async Task ARunThatSucceeds_ReturnsTheGamesExitCode_WithNoMessage()
    {
        _processes.Result = new GameProcessResult(3, null);

        var result = await Run(NewSession());

        result.Should().Be(new LaunchResult(3, null));
        _error.ToString().Should().BeEmpty();
        _notifier.DidNotReceiveWithAnyArgs().Notify(default!, default!);
    }

    [Fact]
    public async Task ARunThatSucceeds_TakesTheLockForTheInstallationsStoreAndId()
    {
        await Run(NewSession());

        _lockFactory.Received(1).Create("steam", "42");
    }

    [Fact]
    public async Task TheStepsRunInOrder_AndTheSpecIsResolvedAfterTheLoadoutIsApplied()
    {
        AddGate("a");
        AddGate("b");

        await Run(NewSession());

        _events.Should().Equal(
            "check:a", "check:b",
            "sync",
            "prepare:a", "prepare:b",
            "resolve",
            "start", "wait",
            "finish:a", "finish:b",
            "sync");
    }

    [Fact]
    public async Task TheStateFileMovesThroughPreparingRunningAndFinishing()
    {
        AddGate("a");
        var session = NewSession();

        await Run(session);

        _synchronizer.PhasesSeen.Should().Equal(LaunchPhase.Preparing, LaunchPhase.Finishing);
        _gates.Cast<FakeGate>().Single().PrepareSaw.Should().Be(LaunchPhase.Preparing);
        _processes.PhaseAtStart.Should().Be(LaunchPhase.Running);
        _gates.Cast<FakeGate>().Single().FinishSaw.Should().Be(LaunchPhase.Finishing);
    }

    [Fact]
    public async Task TheStateFileNamesThisProcessAndTheLoadout()
    {
        var session = NewSession();
        InstallationLockState? state = null;
        _synchronizer.OnSync = () => { using var reader = new InstallationLock(LockPath); state = reader.ReadState(); };

        await Run(session);

        state!.Pid.Should().Be(Environment.ProcessId);
        state.Loadout.Should().Be("My Loadout");
    }

    [Fact]
    public async Task TheStateFileCarriesTheContextsWineDllOverridesInEveryPhase()
    {
        var seen = new List<(LaunchPhase Phase, string Overrides)>();
        void Record()
        {
            using var reader = new InstallationLock(LockPath);
            var state = reader.ReadState()!;
            seen.Add((state.Phase, string.Join(';', state.WineDllOverrides)));
        }
        _synchronizer.OnSync = Record;
        _processes.OnStart = Record;

        await Run(NewSession());

        seen.Should().Equal(
            (LaunchPhase.Preparing, "winmm=n,b"),
            (LaunchPhase.Running, "winmm=n,b"),
            (LaunchPhase.Finishing, "winmm=n,b"));
    }

    [Fact]
    public async Task IsRunning_IsTrueInEveryPhase_AndUntilReleaseIsCalled()
    {
        var gate = AddGate("a");
        var seen = new List<string>();
        _synchronizer.OnSync = () => seen.Add($"sync:{_active.IsRunning(_installation)}");
        _processes.OnStart = () => seen.Add($"start:{_active.IsRunning(_installation)}");
        gate.OnFinish = () => seen.Add($"finish:{_active.IsRunning(_installation)}");
        var session = NewSession();

        _active.IsRunning(_installation).Should().BeFalse();
        await Run(session);
        seen.Should().Equal("sync:True", "start:True", "finish:True", "sync:True");
        _active.IsRunning(_installation).Should().BeTrue("the lock is still held until Release");

        session.Release();

        _active.IsRunning(_installation).Should().BeFalse();
    }

    [Fact]
    public async Task TheLockIsStillHeldAfterTheRun_AndFreeAfterRelease()
    {
        var session = NewSession();
        await Run(session);

        using (var probe = new InstallationLock(LockPath)) probe.Probe().Should().BeTrue();

        session.Release();

        using (var probe = new InstallationLock(LockPath)) probe.Probe().Should().BeFalse();
    }

    [Fact]
    public async Task Release_DeletesTheStateFile()
    {
        var session = NewSession();
        await Run(session);
        File.Exists(Path.ChangeExtension(LockPath, ".state")).Should().BeTrue();

        session.Release();

        File.Exists(Path.ChangeExtension(LockPath, ".state")).Should().BeFalse();
    }

    [Fact]
    public async Task Release_IsSafeToCallTwice_AndBeforeARun()
    {
        var session = NewSession();
        session.Release();

        await Run(session);
        session.Release();
        var again = session.Release;

        again.Should().NotThrow();
    }

    [Fact]
    public async Task ARunnerCannotRunTheSameSessionTwice()
    {
        var session = NewSession();
        await Run(session);

        var act = () => Run(session);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AHeldLock_Is125_NamingThePidAndLoadoutFromTheStateFile()
    {
        using var holder = TestHost.StartHolder(LockPath, seconds: 20);
        try
        {
            var result = await Run(NewSession());

            result.ExitCode.Should().Be(125);
            result.Message.Should().Contain(holder.Id.ToString()).And.Contain("test-loadout");
            _events.Should().BeEmpty("nothing is applied or started while someone else holds the lock");
            _active.IsRunning(_installation).Should().BeFalse();
        }
        finally
        {
            holder.Kill();
        }
    }

    [Fact]
    public async Task AHeldLockWithoutAStateFile_Is125_WithoutAPid()
    {
        using var other = new InstallationLock(LockPath);
        other.TryAcquire().Should().BeTrue();

        var result = await Run(NewSession());

        result.ExitCode.Should().Be(125);
        result.Message.Should().NotBeNullOrEmpty();
        _events.Should().BeEmpty();
    }

    [Fact]
    public async Task AVeto_Is125_WithTheGatesText_AndNothingIsApplied()
    {
        AddGate("a");
        AddGate("b").Veto = new Veto("Install the thing first.");
        AddGate("c");

        var result = await Run(NewSession());

        result.Should().Be(new LaunchResult(125, "Install the thing first."));
        _events.Should().Equal("check:a", "check:b");
        Phase().Should().BeNull("a refused launch never reaches the preparing state");
    }

    [Fact]
    public async Task ACheckThatThrows_Is125_ReportedOnce_AndNothingIsApplied()
    {
        AddGate("a").CheckFailure = new InvalidOperationException("check broke");
        AddGate("b");

        var result = await Run(NewSession());

        result.ExitCode.Should().Be(125);
        result.Message.Should().Contain("check broke");
        _events.Should().Equal("check:a");
        _error.ToString().Should().Be(result.Message + Environment.NewLine);
        Phase().Should().BeNull();
    }

    [Fact]
    public async Task APrepareFailure_Is125_AndFinishNeverRuns()
    {
        var gate = AddGate("a");
        _synchronizer.FailOn = 1;

        var result = await Run(NewSession());

        result.ExitCode.Should().Be(125);
        result.Message.Should().Contain("sync broke");
        _events.Should().Equal("check:a", "sync");
        gate.Finished.Should().BeFalse();
        _processes.Started.Should().BeFalse();
    }

    [Fact]
    public async Task AGatePrepareFailure_Is125_NoGameIsStarted_AndFinishNeverRuns()
    {
        var first = AddGate("a");
        var second = AddGate("b");
        second.PrepareFailure = new InvalidOperationException("gate broke");

        var result = await Run(NewSession());

        result.ExitCode.Should().Be(125);
        result.Message.Should().Contain("gate broke");
        _processes.Started.Should().BeFalse();
        first.Finished.Should().BeFalse();
        second.Finished.Should().BeFalse();
        _events.Count(e => e == "sync").Should().Be(1);
    }

    [Fact]
    public async Task AFinishFailure_Is125_SayingTheLoadoutIsStillApplied()
    {
        AddGate("a").FinishFailure = new InvalidOperationException("finish broke");

        var result = await Run(NewSession());

        result.ExitCode.Should().Be(125);
        result.Message.Should().Contain("finish broke").And.Contain("still applied");
    }

    [Fact]
    public async Task AFailedIngest_Is125_SayingTheLoadoutIsStillApplied()
    {
        _synchronizer.FailOn = 2;

        var result = await Run(NewSession());

        result.ExitCode.Should().Be(125);
        result.Message.Should().Contain("sync broke").And.Contain("still applied");
    }

    [Fact]
    public async Task AGameThatFailsWithItsOwnCode_IsNotTurnedInto125()
    {
        _processes.Result = new GameProcessResult(125, null);

        var result = await Run(NewSession());

        result.Should().Be(new LaunchResult(125, null), "the game's own code is passed through unchanged");
    }

    [Fact]
    public async Task AProcessThatCouldNotBeStarted_Is125_AndTheLaunchIsStillFinished()
    {
        var gate = AddGate("a");
        _processes.StartFailure = new IOException("no such exe");

        var result = await Run(NewSession());

        result.ExitCode.Should().Be(125);
        result.Message.Should().Contain("no such exe");
        gate.Finished.Should().BeTrue("the gates prepared, so they are told the launch is over");
        _events.Count(e => e == "sync").Should().Be(2);
    }

    [Fact]
    public async Task ASpecThatCannotBeResolved_Is125_AndTheLaunchIsStillFinished()
    {
        var gate = AddGate("a");

        var result = await NewSession().RunAsync(_context, GameProcessSpec () => throw new InvalidOperationException("no exe"));

        result.ExitCode.Should().Be(125);
        result.Message.Should().Contain("no exe");
        gate.Finished.Should().BeTrue();
        _processes.Started.Should().BeFalse();
    }

    [Fact]
    public async Task AGameProcessThatSurvivesKill_Is125_NamingThePid_WithoutFinishing()
    {
        var gate = AddGate("a");
        _processes.Result = new GameProcessResult(0, new TrackedProcess(4242, "wineserver"));
        var session = NewSession();

        var result = await Run(session);

        result.ExitCode.Should().Be(125);
        result.Message.Should().Contain("4242");
        gate.Finished.Should().BeFalse();
        _events.Count(e => e == "sync").Should().Be(1, "the game folder is still in use, so nothing is ingested");
        Phase().Should().Be(LaunchPhase.Running);

        session.Release();

        Phase().Should().Be(LaunchPhase.Running, "the state stays so the next start sees the game was never stopped");
        using (var probe = new InstallationLock(LockPath)) probe.Probe().Should().BeFalse("the lock itself is released");
        _active.IsRunning(_installation).Should().BeFalse();
    }

    [Fact]
    public async Task AFailure_IsReportedOnce_ToStderr_AndTheNotifier()
    {
        AddGate("a").Veto = new Veto("Nope.");

        await Run(NewSession());

        _error.ToString().Should().Be("Nope." + Environment.NewLine);
        _notifier.Received(1).Notify(Arg.Any<string>(), "Nope.");
    }

    [Fact]
    public async Task TheResolvedSpec_TheSubstitution_AndTheFinalArgv_AreLoggedBeforeTheGameStarts()
    {
        var exe = FileSystem.Shared.FromUnsanitizedFullPath(Path.Combine(_runtimeDir, "loader.exe"));
        var spec = new LaunchSpec(exe, ["-modded"]);
        var substitution = new ExeSubstitution.Result(["proton", "run", exe.GetFullPath(), "-modded"], null, "1091500");

        await NewSession().RunAsync(_context, () => new LaunchPlan(new GameProcessSpec(["proton", "run", exe.GetFullPath(), "-modded"]), spec, substitution));

        var before = _log.Entries.Take(_processes.LogCountAtStart).ToList();
        before.Should().Contain(e => e.Contains("Launch spec") && e.Contains("loader.exe") && e.Contains("-modded"));
        before.Should().Contain(e => e.Contains("Command substitution") && e.Contains("1091500"));
        before.Should().Contain(e => e.Contains("Starting Test Game") && e.Contains("proton run") && e.Contains("loader.exe -modded"));
    }

    [Fact]
    public async Task ARefusedSubstitution_IsLoggedWithItsReason()
    {
        var substitution = new ExeSubstitution.Result([], "no exe slot in the command", null);

        await NewSession().RunAsync(_context, () => new LaunchPlan(new GameProcessSpec(["game.exe"]), null, substitution));

        _log.Entries.Should().Contain(e => e.Contains("refused: no exe slot in the command"));
    }

    [Fact]
    public async Task ARealRunLogsWhichNexusCredentialItHolds()
    {
        var credentials = Substitute.For<INexusCredentialSummary>();
        credentials.Kind.Returns(NexusCredentialKind.ApiKey);
        credentials.Source.Returns("environment variable NEXUS_API_KEY");
        var session = new LaunchSession(_lockFactory, _active, _synchronizer, _processes, _gates, _notifier, _log, _error, credentials: credentials);
        _sessions.Add(session);

        await Run(session);

        _log.Entries.Should().Contain("Nexus credential: API key from environment variable NEXUS_API_KEY");
    }

    private sealed class RecordingLogger : ILogger<LaunchSession>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(formatter(state, exception));
    }

    private sealed class FakeSynchronizer(List<string> events, LaunchSessionTests test) : ILaunchSynchronizer
    {
        private int _calls;
        public List<LaunchPhase> PhasesSeen { get; } = [];
        public int FailOn { get; set; }
        public Action? OnSync { get; set; }

        public Task Synchronize(LoadoutId loadoutId)
        {
            events.Add("sync");
            _calls++;
            PhasesSeen.Add(test.Phase()!.Value);
            OnSync?.Invoke();
            if (_calls == FailOn) throw new InvalidOperationException("sync broke");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGate(string name, List<string> events, LaunchSessionTests test) : ILaunchGate
    {
        public Veto? Veto { get; set; }
        public Exception? CheckFailure { get; set; }
        public Exception? PrepareFailure { get; set; }
        public Exception? FinishFailure { get; set; }
        public Action? OnFinish { get; set; }
        public LaunchPhase? PrepareSaw { get; private set; }
        public LaunchPhase? FinishSaw { get; private set; }
        public bool Finished { get; private set; }

        public Veto? Check(LaunchContext context)
        {
            events.Add($"check:{name}");
            if (CheckFailure is not null) throw CheckFailure;
            return Veto;
        }

        public Task Prepare(LaunchContext context, CancellationToken cancellationToken)
        {
            events.Add($"prepare:{name}");
            PrepareSaw = test.Phase();
            return PrepareFailure is null ? Task.CompletedTask : Task.FromException(PrepareFailure);
        }

        public Task Finish(LaunchContext context)
        {
            events.Add($"finish:{name}");
            Finished = true;
            FinishSaw = test.Phase();
            OnFinish?.Invoke();
            return FinishFailure is null ? Task.CompletedTask : Task.FromException(FinishFailure);
        }
    }

    [Fact]
    public async Task AnExternalLaunch_IsMarkedRunningBeforeTheCallbackAndShutsTheGameDownAfterIt()
    {
        AddGate("a");
        LaunchPhase? phaseInCallback = null;

        var result = await NewSession().RunExternalAsync(_context, _ =>
        {
            phaseInCallback = Phase();
            _events.Add("callback");
            return Task.CompletedTask;
        });

        result.Should().Be(new LaunchResult(0, null));
        phaseInCallback.Should().Be(LaunchPhase.Running);
        _events.Should().Equal("check:a", "sync", "prepare:a", "callback", "shutdown", "finish:a", "sync");
        _processes.Started.Should().BeFalse("the game is started by someone else");
    }

    [Fact]
    public async Task AnExternalLaunchWithARunSpec_RecordsTheGameFolderRelativeExeAndArgumentsWhileRunningAndFinishing()
    {
        var exe = _installation.Locations[LocationId.Game].Path.Combine("bin/x64/Game.exe");
        var context = _context with { RunSpec = new LaunchSpec(exe, ["-modded"]) };
        var expected = new RecordedLaunch(_runtimeDir, "bin/x64/Game.exe", ["-modded"]);
        var recorded = new List<RecordedLaunch?>();
        AddGate("a").OnFinish = () => recorded.Add(ReadRecorded());

        await NewSession().RunExternalAsync(context, _ =>
        {
            recorded.Add(ReadRecorded());
            return Task.CompletedTask;
        });

        recorded.Should().Equal(expected, expected);
    }

    [Fact]
    public async Task AnExternalLaunchWithoutARunSpec_RecordsNoLaunch()
    {
        RecordedLaunch? recorded = new RecordedLaunch("stale", "stale.exe", []);

        await NewSession().RunExternalAsync(_context, _ =>
        {
            recorded = ReadRecorded();
            return Task.CompletedTask;
        });

        recorded.Should().BeNull();
    }

    private RecordedLaunch? ReadRecorded()
    {
        using var reader = new InstallationLock(LockPath);
        return reader.ReadState()?.RecordedLaunch;
    }

    [Fact]
    public async Task AnExternalLaunch_IsRunningInEveryPhaseUntilReleased()
    {
        var session = NewSession();

        await session.RunExternalAsync(_context, _ =>
        {
            _active.IsRunning(_installation).Should().BeTrue();
            return Task.CompletedTask;
        });

        _active.IsRunning(_installation).Should().BeTrue("the lock is held until Release");
        session.Release();
        _active.IsRunning(_installation).Should().BeFalse();
        File.Exists(Path.ChangeExtension(LockPath, ".state")).Should().BeFalse();
    }

    [Fact]
    public async Task AnExternalLaunch_WhoseCallbackIsCancelled_StillFinishesAndIsNotAFailure()
    {
        using var cts = new CancellationTokenSource();

        var result = await NewSession().RunExternalAsync(_context, token =>
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }, cts.Token);

        result.Message.Should().BeNull();
        _events.Should().EndWith(["shutdown", "sync"]);
        _error.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task AnExternalLaunch_WhoseCallbackThrows_FailsButStillFinishes()
    {
        var result = await NewSession().RunExternalAsync(_context, _ => throw new InvalidOperationException("boom"));

        result.ExitCode.Should().Be(LaunchSession.FailureExitCode);
        result.Message.Should().Contain("boom");
        _events.Should().EndWith(["shutdown", "sync"]);
    }

    [Fact]
    public async Task AnExternalLaunch_WithAProcessThatSurvivesSigkill_SkipsFinishAndKeepsTheStateRunning()
    {
        AddGate("a");
        _processes.Survivor = new TrackedProcess(4242, "wineserver");
        var session = NewSession();

        var result = await session.RunExternalAsync(_context, _ => Task.CompletedTask);
        session.Release();

        result.ExitCode.Should().Be(LaunchSession.FailureExitCode);
        result.Message.Should().Contain("4242");
        _events.Should().NotContain("finish:a");
        _events.Count(e => e == "sync").Should().Be(1, "the game folder is still in use");
        using var reader = new InstallationLock(LockPath);
        reader.ReadState()!.Phase.Should().Be(LaunchPhase.Running);
    }

    [Fact]
    public async Task WrapperActive_FollowsTheWrapperKeyInTheStateFile()
    {
        var session = NewSession();
        session.WrapperActive.Should().BeFalse("nothing has been launched yet");
        bool? before = null, after = null;

        await session.RunExternalAsync(_context, _ =>
        {
            before = session.WrapperActive;
            using var wrapper = new InstallationLock(LockPath);
            wrapper.WriteWrapperKey("wrapper", "999");
            after = session.WrapperActive;
            return Task.CompletedTask;
        });

        before.Should().BeFalse();
        after.Should().BeTrue();
    }

    [Fact]
    public async Task AGameLaunch_ThrowsTheFailureMessageAndReleasesWhenDisposed()
    {
        _processes.Survivor = new TrackedProcess(4242, "wineserver");
        var launch = new GameLaunch(NewSession(), _context);

        var act = async () => await launch.RunAsync(_ => Task.CompletedTask, CancellationToken.None);

        (await act.Should().ThrowAsync<GameLaunchException>()).Which.Message.Should().Contain("4242");
        _active.IsRunning(_installation).Should().BeTrue();
        launch.Dispose();
        _active.IsRunning(_installation).Should().BeFalse();
    }

    private sealed class FakeProcessFactory(List<string> events, LaunchSessionTests test, RecordingLogger log) : IGameProcessFactory
    {
        public int LogCountAtStart { get; private set; }
        public GameProcessResult Result { get; set; } = new(0, null);
        public Exception? StartFailure { get; set; }
        public Action? OnStart { get; set; }
        public bool Started { get; private set; }
        public LaunchPhase? PhaseAtStart { get; private set; }

        public TrackedProcess? Survivor { get; set; }

        public Task<TrackedProcess?> ShutDownGameAsync(GameInstallation installation, CancellationToken cancellationToken)
        {
            events.Add("shutdown");
            return Task.FromResult(Survivor);
        }

        public IGameProcess Start(GameProcessSpec spec, GameInstallation installation)
        {
            if (StartFailure is not null) throw StartFailure;
            events.Add("start");
            Started = true;
            LogCountAtStart = log.Entries.Count;
            PhaseAtStart = test.Phase();
            OnStart?.Invoke();
            return new FakeProcess(events, Result);
        }
    }

    private sealed class FakeProcess(List<string> events, GameProcessResult result) : IGameProcess
    {
        public int Pid => 1234;

        public Task<GameProcessResult> WaitAsync(CancellationToken cancellationToken = default)
        {
            events.Add("wait");
            return Task.FromResult(result);
        }

        public void Terminate() { }

        public void Dispose() { }
    }
}
