using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using SilverModManager.App;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk;
using SilverModManager.SingleProcess;
using SilverModManager.SingleProcess.Exceptions;

namespace SilverModManager.CLI.Tests;

public class ProcessStartupTests
{
    private sealed class Recorder
    {
        public List<string> Calls { get; } = [];
        public (Process? Process, MainKind Kind, int Port) SyncInfo { get; set; } = (null, MainKind.Gui, 0);

        /// <summary>What every read after the first returns, when someone takes the slot in between.</summary>
        public (Process? Process, MainKind Kind, int Port)? LateSyncInfo { get; set; }
        private int _syncReads;
        public int LaunchExitCode { get; set; }
        public Exception? LaunchException { get; set; }
        public List<LaunchArgs> Launches { get; } = [];

        /// <summary>What joining the window's launch answers: null when it is not joinable, else the child's exit code.</summary>
        public int? PassThroughExitCode { get; set; }
        public Exception? PassThroughException { get; set; }
        public List<int> PassThroughHolders { get; } = [];

        /// <summary>What handing the launch to the window answers. Defaults to a window that cannot be asked.</summary>
        public ForwardedLaunchOutcome ForwardOutcome { get; set; } = new ForwardedLaunchOutcome.Unavailable("it is too old");
        public Exception? ForwardException { get; set; }
        public List<LaunchArgs> Forwards { get; } = [];
        public List<(MainKind Kind, int Pid)> Described { get; } = [];
        public Func<MainKind, int, string>? Describe { get; set; }
        public bool CanConnect { get; set; } = true;
        public int CliExitCode { get; set; }
        public bool KillThrows { get; set; }
        public bool OpenDatabaseThrows { get; set; }
        public List<string> Errors { get; } = [];
        public List<string> Notifications { get; } = [];
        public List<StartupMode> HostModes { get; } = [];
        public List<string> Warnings { get; } = [];
        public Dictionary<string, string> Environment { get; } = [];

        /// <summary>The outcomes of successive claim attempts; the last one repeats.</summary>
        public List<bool> Claims { get; set; } = [true];
        public List<(MainKind Kind, int Port)> ClaimRequests { get; } = [];
        public Exception? OpenDatabaseException { get; set; }
        public Exception? CliException { get; set; }

        public ProcessStartupSteps Steps() => new()
        {
            BuildHost = mode =>
            {
                Calls.Add("build-host");
                HostModes.Add(mode);
                return new HostBuilder()
                    .ConfigureServices(services => services
                        .AddSingleton<IEnvironmentReader>(new FakeEnvironment(Environment))
                        .AddHostedService(_ => new RecordingHostedService(Calls)))
                    .ConfigureLogging(logging => logging.AddProvider(new WarningRecorder(Warnings)))
                    .Build();
            },
            ReadSyncInfo = _ =>
            {
                Calls.Add("read-sync");
                return _syncReads++ == 0 ? SyncInfo : LateSyncInfo ?? SyncInfo;
            },
            CanConnect = (_, _) => { Calls.Add("can-connect"); return Task.FromResult(CanConnect); },
            Kill = _ => { Calls.Add("kill"); if (KillThrows) throw new InvalidOperationException("gone"); },
            OpenDatabase = _ =>
            {
                Calls.Add("open-database");
                if (OpenDatabaseException is not null) throw OpenDatabaseException;
                if (OpenDatabaseThrows) throw new InvalidOperationException("db");
            },
            TryClaimMain = (_, kind, port) =>
            {
                ClaimRequests.Add((kind, port));
                var outcome = Claims[Math.Min(ClaimRequests.Count - 1, Claims.Count - 1)];
                Calls.Add(outcome ? "claim" : "claim-lost");
                return outcome;
            },
            ReleaseMain = _ => Calls.Add("release-main"),
            CloseDatabase = _ => Calls.Add("close-database"),
            ReleaseLaunchSession = _ => Calls.Add("release-session"),
            Notify = (_, message) => Notifications.Add(message),
            InstallGlobalHandlers = _ => Calls.Add("install-handlers"),
            RunCliAsMain = (_, _) =>
            {
                Calls.Add("cli-as-main");
                if (CliException is not null) throw CliException;
                return Task.FromResult(CliExitCode);
            },
            RunCliRemotely = (_, _) => { Calls.Add("cli-remote"); return Task.FromResult(CliExitCode); },
            RunLaunch = (_, launch) =>
            {
                Calls.Add("run-launch");
                Launches.Add(launch);
                if (LaunchException is not null) throw LaunchException;
                return Task.FromResult(LaunchExitCode);
            },
            TryPassThrough = (_, _, holderPid) =>
            {
                Calls.Add("pass-through");
                PassThroughHolders.Add(holderPid);
                if (PassThroughException is not null) throw PassThroughException;
                return Task.FromResult(PassThroughExitCode);
            },
            ForwardLaunch = (_, launch) =>
            {
                Calls.Add("forward-launch");
                Forwards.Add(launch);
                if (ForwardException is not null) throw ForwardException;
                return Task.FromResult(ForwardOutcome);
            },
            DescribeHeadlessMain = (_, kind, pid) =>
            {
                Described.Add((kind, pid));
                return Describe?.Invoke(kind, pid) ?? new HeadlessMainRunning(kind, pid).Message;
            },
            RunUi = _ => Calls.Add("ui"),
            WriteError = Errors.Add,
        };
    }

    private sealed class FakeEnvironment(Dictionary<string, string> values) : IEnvironmentReader
    {
        public string? Get(string name) => values.GetValueOrDefault(name);
    }

    private sealed class WarningRecorder(List<string> warnings) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Recording(warnings);
        public void Dispose() { }

        private sealed class Recording(List<string> warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning) warnings.Add(formatter(state, exception));
            }
        }
    }

    private sealed class RecordingHostedService(List<string> calls) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) { calls.Add("host-start"); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) { calls.Add("host-stop"); return Task.CompletedTask; }
    }

    /// <summary>The arguments of a start with this verb; <c>run</c> needs a loadout and a command to be valid.</summary>
    private static string[] Command(string verb) => verb == "run" ? ["run", "a loadout", "--", "a-game"] : [verb, "arg"];

    private static int Run(Recorder recorder, params string[] args)
        => ProcessStartup.Run(StartupMode.Parse(args), args, recorder.Steps());

    [Fact]
    public void GuiStartWithNoMainBuildsHostOpensDatabaseAndShowsUi()
    {
        var recorder = new Recorder();

        Run(recorder).Should().Be(0);

        recorder.Calls.Should().Equal(
            "build-host", "read-sync", "host-start", "open-database", "install-handlers", "cli-as-main", "ui",
            "host-stop", "close-database", "release-session", "release-main"
        );
    }

    [Fact]
    public void KillerRunsForGuiStartAgainstUnresponsiveMain()
    {
        var recorder = new Recorder
        {
            SyncInfo = (Process.GetCurrentProcess(), MainKind.Gui, 1234),
            CanConnect = false,
        };

        Run(recorder);

        recorder.Calls.Should().ContainInOrder("build-host", "read-sync", "can-connect", "kill", "open-database");
    }

    [Fact]
    public void KillerDoesNotRunWhenMainResponds()
    {
        var recorder = new Recorder { SyncInfo = (Process.GetCurrentProcess(), MainKind.Gui, 1234) };

        Run(recorder);

        recorder.Calls.Should().Contain("can-connect").And.NotContain("kill");
    }

    [Fact]
    public void KillFailureIsSwallowedAndStartupContinues()
    {
        var recorder = new Recorder
        {
            SyncInfo = (Process.GetCurrentProcess(), MainKind.Gui, 1234),
            CanConnect = false,
            KillThrows = true,
        };

        Run(recorder).Should().Be(0);

        recorder.Calls.Should().Contain("open-database");
    }

    [Fact]
    public void AsMainRunsTheVerbAndReturnsItsExitCode()
    {
        var recorder = new Recorder { CliExitCode = 42 };

        Run(recorder, "as-main", "some-verb").Should().Be(42);

        recorder.Calls.Should().Contain("cli-as-main").And.NotContain("ui").And.NotContain("cli-remote");
    }

    [Fact]
    public void ClientStartSkipsDatabaseAndRunsVerbRemotely()
    {
        var recorder = new Recorder { CliExitCode = 7 };

        Run(recorder, "some-verb").Should().Be(7);

        recorder.Calls.Should().Contain("cli-remote").And.NotContain("open-database").And.NotContain("cli-as-main");
    }

    [Fact]
    public void UnhandledFailureWhileRunningReturnsOne()
    {
        var recorder = new Recorder();
        var steps = recorder.Steps() with
        {
            RunCliAsMain = (_, _) => throw new InvalidOperationException("boom"),
        };

        var args = new string[] { "as-main", "some-verb" };
        ProcessStartup.Run(StartupMode.Parse(args), args, steps).Should().Be(1);
    }

    [Fact]
    public void DatabaseFailureIsNotSwallowed()
    {
        var recorder = new Recorder { OpenDatabaseThrows = true };

        var act = () => Run(recorder);

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(MainKind.HeadlessRun)]
    [InlineData(MainKind.HeadlessCli)]
    public void KillerLeavesHeadlessMainAlone(MainKind kind)
    {
        // A headless main has no server to answer the heartbeat, so it must not look unresponsive.
        var recorder = new Recorder
        {
            SyncInfo = (Process.GetCurrentProcess(), kind, 0),
            CanConnect = false,
        };

        Run(recorder, "some-verb");

        recorder.Calls.Should().NotContain("can-connect").And.NotContain("kill");
    }

    [Theory]
    [InlineData(MainKind.HeadlessRun, "A game is running through SilverModManager")]
    [InlineData(MainKind.HeadlessCli, "busy in another terminal")]
    public void ClientPrintsTheHeadlessMessageAndFails(MainKind kind, string expected)
    {
        var recorder = new Recorder();
        var steps = recorder.Steps() with
        {
            // Faulted task, like the real async step, so the exception arrives wrapped.
            RunCliRemotely = (_, _) => Task.FromException<int>(new HeadlessMainRunning(kind, 4242)),
        };

        ProcessStartup.Run(StartupMode.Parse(["some-verb"]), ["some-verb"], steps).Should().NotBe(0);

        recorder.Errors.Should().ContainSingle().Which.Should().Contain(expected).And.Contain("4242");
        recorder.Calls.Should().NotContain("cli-as-main").And.NotContain("open-database");
    }

    [Theory]
    [InlineData(MainKind.HeadlessRun, "A game is running through SilverModManager")]
    [InlineData(MainKind.HeadlessCli, "busy in another terminal")]
    public void GuiStartOnLiveHeadlessMainNotifiesAndExitsWithoutTouchingTheDatabase(MainKind kind, string expected)
    {
        var recorder = new Recorder
        {
            SyncInfo = (Process.GetCurrentProcess(), kind, 0),
            CanConnect = false,
        };

        Run(recorder).Should().Be(0);

        recorder.Notifications.Should().ContainSingle().Which.Should().Contain(expected);
        recorder.Calls.Should()
            .NotContain("open-database").And.NotContain("can-connect").And.NotContain("kill")
            .And.NotContain("host-start").And.NotContain("cli-as-main").And.NotContain("ui")
            .And.NotContain("close-database").And.NotContain("release-main");
    }

    [Fact]
    public void GuiStartOnStaleHeadlessSlotStartsNormally()
    {
        // A dead pid reads back as no process, whatever kind it left behind.
        var recorder = new Recorder { SyncInfo = (null, MainKind.HeadlessRun, 0) };

        Run(recorder).Should().Be(0);

        recorder.Notifications.Should().BeEmpty();
        recorder.Calls.Should().Contain("open-database").And.Contain("ui");
    }

    [Theory]
    [InlineData("as-main", "some-verb")]
    [InlineData("run", "a loadout", "--", "a-game")]
    public void KillerNeverRunsForHeadlessMains(params string[] args)
    {
        var recorder = new Recorder
        {
            SyncInfo = (Process.GetCurrentProcess(), MainKind.Gui, 1234),
            CanConnect = false,
        };

        Run(recorder, args);

        recorder.Calls.Should().NotContain("can-connect").And.NotContain("kill");
    }

    [Theory]
    [InlineData("as-main", MainKind.HeadlessCli)]
    [InlineData("run", MainKind.HeadlessRun)]
    public void HeadlessMainClaimsTheSlotWithItsKindAndNoPortBeforeTheHostStarts(string verb, MainKind expectedKind)
    {
        var recorder = new Recorder();

        Run(recorder, Command(verb));

        recorder.ClaimRequests.Should().Equal((expectedKind, 0));
        recorder.Calls.Should().ContainInOrder("claim", "host-start", "open-database");
    }

    [Fact]
    public void GuiStartDoesNotClaimTheSlotItself()
    {
        // The CLI server owns the claim for a window, since it knows the port.
        var recorder = new Recorder();

        Run(recorder);

        recorder.ClaimRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("as-main", MainKind.Gui, ProcessStartup.OpenedMeanwhileMessage, 1)]
    [InlineData("as-main", MainKind.HeadlessCli, "busy in another terminal", 1)]
    [InlineData("as-main", MainKind.HeadlessRun, "A game is running through SilverModManager", 1)]
    [InlineData("run", MainKind.HeadlessRun, "A game is running through SilverModManager", 125)]
    [InlineData("run", MainKind.Gui, ProcessStartup.OpenedMeanwhileMessage, 125)]
    public void LosingTheClaimPrintsTheWinnersMessageAndNeverStartsTheHost(string verb, MainKind winner, string expected, int exitCode)
    {
        // The slot was free when the start looked, and someone took it before the claim landed.
        var recorder = new Recorder
        {
            Claims = [false],
            SyncInfo = (null, MainKind.Gui, 0),
            LateSyncInfo = (Process.GetCurrentProcess(), winner, 0),
        };

        Run(recorder, Command(verb)).Should().Be(exitCode);

        recorder.Errors.Should().ContainSingle().Which.Should().Contain(expected);
        recorder.Calls.Should()
            .NotContain("host-start").And.NotContain("open-database").And.NotContain("cli-as-main")
            .And.NotContain("release-main");
    }

    [Fact]
    public void ClaimIsRetriedWhenTheSlotHoldsADeadPid()
    {
        var recorder = new Recorder { Claims = [false, false, true], SyncInfo = (null, MainKind.HeadlessCli, 0) };

        Run(recorder, "as-main", "some-verb").Should().Be(0);

        recorder.ClaimRequests.Should().HaveCount(3);
        recorder.Calls.Should().Contain("host-start").And.Contain("cli-as-main");
    }

    [Fact]
    public void ClaimGivesUpAfterThreeAttempts()
    {
        var recorder = new Recorder { Claims = [false], SyncInfo = (null, MainKind.HeadlessCli, 0) };

        Run(recorder, "as-main", "some-verb").Should().Be(1);

        recorder.ClaimRequests.Should().HaveCount(3);
        recorder.Errors.Should().ContainSingle();
        recorder.Calls.Should().NotContain("host-start");
    }

    [Fact]
    public void RealSyncFileHoldsTheHeadlessClaimDuringTheRunAndClearsItAfter()
    {
        var directory = Directory.CreateTempSubdirectory("process-startup-tests").FullName;
        try
        {
            var path = FileSystem.Shared.FromUnsanitizedFullPath(Path.Combine(directory, "test.sync"));
            var syncFile = new SyncFile(NullLogger<SyncFile>.Instance, path);
            (Process? Process, MainKind Kind, int Port) duringRun = default;

            var first = new Recorder();
            var firstSteps = first.Steps() with
            {
                ReadSyncInfo = _ => syncFile.GetSyncInfo(),
                TryClaimMain = (_, kind, port) => syncFile.TrySetMain(kind, port),
                ReleaseMain = _ => syncFile.ReleaseMain(),
                RunCliAsMain = (_, _) =>
                {
                    duringRun = syncFile.GetSyncInfo();
                    return Task.FromResult(0);
                },
            };

            ProcessStartup.Run(StartupMode.Parse(["as-main", "verb"]), ["as-main", "verb"], firstSteps).Should().Be(0);

            duringRun.Kind.Should().Be(MainKind.HeadlessCli);
            duringRun.Port.Should().Be(0);
            duringRun.Process.Should().NotBeNull();
            syncFile.GetSyncInfo().Process.Should().BeNull("the slot is released when the process is done");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("as-main", 1)]
    [InlineData("run", 125)]
    public void LockedDatabasePrintsStartingAndReleasesTheSlot(string verb, int exitCode)
    {
        var recorder = new Recorder
        {
            OpenDatabaseException = new InvalidOperationException("open failed", new IOException("IO error: While lock file: /x/LOCK: Resource temporarily unavailable")),
        };

        Run(recorder, Command(verb)).Should().Be(exitCode);

        recorder.Errors.Should().ContainSingle().Which.Should().Be("SilverModManager is starting...");
        recorder.Calls.Should().NotContain("close-database").And.NotContain("cli-as-main").And.EndWith("release-main");
    }

    [Fact]
    public void LockedDatabaseIsNotSwallowedForTheWindow()
    {
        var recorder = new Recorder { OpenDatabaseException = new IOException("IO error: lock hold by current process") };

        var act = () => Run(recorder);

        act.Should().Throw<IOException>();
        recorder.Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("IO error: While lock file: /d/LOCK: Resource temporarily unavailable", true)]
    [InlineData("IO error: lock hold by current process, acquire time 1 acquiring thread 2", true)]
    [InlineData("disk full", false)]
    public void DatabaseLockIsRecognisedByMessageThroughInnerExceptions(string message, bool expected)
    {
        ProcessStartup.IsDatabaseLocked(new Exception("outer", new Exception(message))).Should().Be(expected);
    }

    [Fact]
    public void TeardownRunsDatabaseThenSessionThenSlotAfterTheHostStops()
    {
        var recorder = new Recorder();

        Run(recorder, Command("run"));

        recorder.Calls.Should().ContainInOrder("host-stop", "close-database", "release-session", "release-main");
    }

    [Fact]
    public void TeardownStillReleasesTheSlotWhenEarlierStepsThrow()
    {
        var recorder = new Recorder();
        var steps = recorder.Steps() with
        {
            CloseDatabase = _ => throw new InvalidOperationException("close"),
            ReleaseLaunchSession = _ => throw new InvalidOperationException("session"),
        };

        var args = Command("run");
        ProcessStartup.Run(StartupMode.Parse(args), args, steps).Should().Be(0);

        recorder.Calls.Should().EndWith("release-main");
    }

    [Fact]
    public void TeardownRunsWhenTheVerbFails()
    {
        var recorder = new Recorder { CliException = new InvalidOperationException("boom") };

        Run(recorder, "as-main", "verb").Should().Be(1);

        recorder.Calls.Should().ContainInOrder("close-database", "release-session", "release-main");
    }

    [Fact]
    public void ClientStartTouchesNeitherSlotNorDatabaseTeardown()
    {
        var recorder = new Recorder();

        Run(recorder, "some-verb");

        recorder.Calls.Should()
            .NotContain("claim").And.NotContain("release-main")
            .And.NotContain("close-database").And.NotContain("release-session");
    }

    [Fact]
    public void RunWithoutTheSeparatorExits125WithTheSynopsisAndStartsNothing()
    {
        var recorder = new Recorder();

        Run(recorder, "run", "a loadout").Should().Be(125);

        recorder.Errors.Should().ContainSingle().Which.Should().Contain(LaunchArgs.Synopsis);
        recorder.Calls.Should().BeEmpty();
    }

    [Fact]
    public void RunWithNoMainHandsTheParsedArgumentsToTheLaunchAndReturnsItsExitCode()
    {
        var recorder = new Recorder { LaunchExitCode = 37 };

        Run(recorder, "run", "a loadout", "--game", "489830", "--", "proton", "--", "game.exe").Should().Be(37);

        var launch = recorder.Launches.Should().ContainSingle().Subject;
        launch.LoadoutName.Should().Be("a loadout");
        launch.Game.Should().Be("489830");
        launch.Command.Should().Equal("proton", "--", "game.exe");
        recorder.Calls.Should().ContainInOrder("claim", "host-start", "open-database", "run-launch", "host-stop", "close-database", "release-session", "release-main");
        recorder.Calls.Should().NotContain("cli-as-main").And.NotContain("ui").And.NotContain("cli-remote");
    }

    [Fact]
    public void RunThatThrowsExits125AndStillTearsDown()
    {
        var recorder = new Recorder { LaunchException = new InvalidOperationException("boom") };

        Run(recorder, Command("run")).Should().Be(125);

        // Steam shows neither stderr nor the log, so the failure has to reach the desktop as well.
        recorder.Errors.Should().ContainSingle().Which.Should().Contain("boom");
        recorder.Notifications.Should().ContainSingle().Which.Should().Contain("boom");
        recorder.Calls.Should().ContainInOrder("close-database", "release-session", "release-main");
    }

    [Fact]
    public void LosingTheClaimToAHeadlessMainPrintsThatMainsDescription()
    {
        var recorder = new Recorder
        {
            Claims = [false],
            LateSyncInfo = (Process.GetCurrentProcess(), MainKind.HeadlessRun, 0),
            Describe = (k, pid) => $"described {k} {pid}",
        };

        Run(recorder, Command("run")).Should().Be(125);

        recorder.Errors.Should().ContainSingle().Which.Should().Be($"described HeadlessRun {Environment.ProcessId}");
    }

    [Fact]
    public void GuiStartOnLiveHeadlessMainNotifiesWithThatMainsDescription()
    {
        var recorder = new Recorder
        {
            SyncInfo = (Process.GetCurrentProcess(), MainKind.HeadlessRun, 0),
            Describe = (k, pid) => $"described {k} {pid}",
        };

        Run(recorder);

        recorder.Notifications.Should().ContainSingle().Which.Should().Be($"described HeadlessRun {Environment.ProcessId}");
    }

    [Fact]
    public void RunOnStaleSlotClaimsAndLaunches()
    {
        var recorder = new Recorder { SyncInfo = (null, MainKind.HeadlessRun, 0) };

        Run(recorder, Command("run")).Should().Be(0);

        recorder.Calls.Should().Contain("claim").And.Contain("run-launch");
        recorder.Errors.Should().BeEmpty();
    }

    private static readonly string[] NothingStarted = ["claim", "host-start", "open-database", "run-launch", "can-connect", "kill", "release-main"];

    [Fact]
    public void RunAgainstALiveWindowHandsTheLaunchToItAndExitsWithTheGamesExitCode()
    {
        var recorder = new Recorder
        {
            SyncInfo = (Process.GetCurrentProcess(), MainKind.Gui, 1234),
            ForwardOutcome = new ForwardedLaunchOutcome.Exited(7, null),
        };

        Run(recorder, Command("run")).Should().Be(7);

        recorder.Forwards.Should().ContainSingle().Which.LoadoutName.Should().Be("a loadout");
        recorder.Errors.Should().BeEmpty();
        recorder.Notifications.Should().BeEmpty();
        recorder.Calls.Should().NotContain(NothingStarted, "the window keeps the database and the game folder");
    }

    [Fact]
    public void AForwardedLaunchThatFailedPrintsTheWindowsMessageButDoesNotNotifyAgain()
    {
        var recorder = new Recorder
        {
            SyncInfo = (Process.GetCurrentProcess(), MainKind.Gui, 1234),
            ForwardOutcome = new ForwardedLaunchOutcome.Exited(125, "Test Game is already being launched by another SilverModManager process."),
        };

        Run(recorder, Command("run")).Should().Be(125);

        recorder.Errors.Should().ContainSingle().Which.Should().Be("Test Game is already being launched by another SilverModManager process.");
        recorder.Notifications.Should().BeEmpty("the window already told the desktop");
        recorder.Calls.Should().NotContain(NothingStarted);
    }

    [Fact]
    public void ALaunchTheWindowDeclinesIsRefusedWithItsReason_OnStderrAndTheDesktop()
    {
        var recorder = new Recorder
        {
            SyncInfo = (Process.GetCurrentProcess(), MainKind.Gui, 1234),
            ForwardOutcome = new ForwardedLaunchOutcome.Declined("SilverModManager is applying a loadout to Test Game."),
        };

        Run(recorder, Command("run")).Should().Be(125);

        var expected = ProcessStartup.GuiDeclinedMessage("SilverModManager is applying a loadout to Test Game.");
        expected.Should().Contain("is applying a loadout to Test Game");
        recorder.Errors.Should().ContainSingle().Which.Should().Be(expected);
        // Steam starts the launch option without a terminal, so the desktop has to hear about it too.
        recorder.Notifications.Should().ContainSingle().Which.Should().Be(expected);
        recorder.Calls.Should().NotContain(NothingStarted);
    }

    [Fact]
    public void AWindowThatCannotBeAskedIsRefusedWithTheReason_AndTouchesNothing()
    {
        var recorder = new Recorder
        {
            SyncInfo = (Process.GetCurrentProcess(), MainKind.Gui, 1234),
            ForwardOutcome = new ForwardedLaunchOutcome.Unavailable("it is probably an older build"),
            CanConnect = false,
        };

        Run(recorder, Command("run")).Should().Be(125);

        var expected = ProcessStartup.GuiUnavailableMessage("it is probably an older build");
        expected.Should().Contain("older build").And.Contain("Close SilverModManager");
        recorder.Errors.Should().ContainSingle().Which.Should().Be(expected);
        recorder.Notifications.Should().ContainSingle().Which.Should().Be(expected);
        recorder.Calls.Should().NotContain(NothingStarted);
    }

    [Fact]
    public void AForwardThatThrowsIsAFailedLaunch_NotAHeadlessStart()
    {
        var recorder = new Recorder
        {
            SyncInfo = (Process.GetCurrentProcess(), MainKind.Gui, 1234),
            ForwardException = new InvalidOperationException("no main process"),
        };

        Run(recorder, Command("run")).Should().Be(125);

        recorder.Errors.Should().ContainSingle().Which.Should().Contain("no main process");
        recorder.Notifications.Should().ContainSingle().Which.Should().Contain("no main process");
        recorder.Calls.Should().NotContain(NothingStarted);
    }

    [Fact]
    public void AWindowThatClosesBeforeTheLaunchIsHandedOver_FallsBackToARunOfItsOwn()
    {
        var recorder = new Recorder
        {
            SyncInfo = (Process.GetCurrentProcess(), MainKind.Gui, 1234),
            ForwardException = new NoMainProcessStarted(),
            LaunchExitCode = 3,
        };

        Run(recorder, Command("run")).Should().Be(3);

        recorder.Forwards.Should().ContainSingle();
        recorder.Calls.Should().ContainInOrder("forward-launch", "claim", "run-launch", "release-main");
        recorder.Errors.Should().BeEmpty();
        recorder.Notifications.Should().BeEmpty();
    }

    [Fact]
    public void AWindowThatClosesBeforeTheLaunchIsHandedOver_AndIsReplacedByAnotherMain_IsRefusedAsOpenedMeanwhile()
    {
        var recorder = new Recorder
        {
            SyncInfo = (Process.GetCurrentProcess(), MainKind.Gui, 1234),
            ForwardException = new NoMainProcessStarted(),
            Claims = [false],
            LateSyncInfo = (Process.GetCurrentProcess(), MainKind.Gui, 4321),
        };

        Run(recorder, Command("run")).Should().Be(125);

        recorder.Errors.Should().ContainSingle().Which.Should().Be(ProcessStartup.OpenedMeanwhileMessage);
        recorder.Calls.Should().NotContain("run-launch");
    }

    [Fact]
    public void ASecretsFileOnAForwardedLaunchIsIgnoredWithAWarning()
    {
        var recorder = new Recorder
        {
            SyncInfo = (Process.GetCurrentProcess(), MainKind.Gui, 1234),
            ForwardOutcome = new ForwardedLaunchOutcome.Exited(0, null),
        };

        Run(recorder, "run", "a loadout", "--secrets-file", "/tmp/secrets.env", "--", "a-game").Should().Be(0);

        recorder.Warnings.Should().ContainSingle(w => w.Contains("--secrets-file") && w.Contains("ignored"));
    }

    [Fact]
    public void RunAgainstTheWindowsOwnLaunchPassesThroughWithTheChildsExitCodeAndStartsNothing()
    {
        var recorder = new Recorder { SyncInfo = (Process.GetCurrentProcess(), MainKind.Gui, 1234), PassThroughExitCode = 7 };

        Run(recorder, Command("run")).Should().Be(7);

        recorder.PassThroughHolders.Should().Equal(Environment.ProcessId);
        recorder.Calls.Should().NotContain("forward-launch", "the window already owns this launch");
        recorder.Errors.Should().BeEmpty();
        recorder.Notifications.Should().BeEmpty();
        recorder.Calls.Should()
            .NotContain("claim").And.NotContain("host-start").And.NotContain("open-database").And.NotContain("run-launch")
            .And.NotContain("can-connect").And.NotContain("kill").And.NotContain("release-main");
    }

    [Fact]
    public void DryRunAgainstALiveWindowNeverPassesThroughOrForwards_SoNothingIsStarted()
    {
        var recorder = new Recorder
        {
            SyncInfo = (Process.GetCurrentProcess(), MainKind.Gui, 1234),
            PassThroughExitCode = 0,
            ForwardOutcome = new ForwardedLaunchOutcome.Exited(0, null),
        };

        Run(recorder, "run", "a loadout", "--dry-run", "--", "a-game").Should().Be(125);

        recorder.Calls.Should().NotContain("pass-through").And.NotContain("forward-launch").And.NotContain("run-launch");
        recorder.Errors.Should().ContainSingle().Which.Should().Be(ProcessStartup.DryRunNeedsHeadlessMessage);
    }

    [Fact]
    public void RunAgainstAWindowThatDoesNotOwnThisLaunchTriesPassThroughFirst_ThenForwards()
    {
        var recorder = new Recorder
        {
            SyncInfo = (Process.GetCurrentProcess(), MainKind.Gui, 1234),
            PassThroughExitCode = null,
            ForwardOutcome = new ForwardedLaunchOutcome.Exited(3, null),
        };

        Run(recorder, Command("run")).Should().Be(3);

        recorder.Calls.Should().ContainInOrder("pass-through", "forward-launch");
        recorder.Calls.Should().NotContain("run-launch");
    }

    [Theory]
    [InlineData(MainKind.HeadlessRun)]
    [InlineData(MainKind.HeadlessCli)]
    public void RunAgainstAHeadlessMainNeverTriesToPassThrough(MainKind kind)
    {
        var recorder = new Recorder { SyncInfo = (Process.GetCurrentProcess(), kind, 0), PassThroughExitCode = 0 };

        Run(recorder, Command("run")).Should().Be(125);

        recorder.Calls.Should().NotContain("pass-through").And.NotContain("forward-launch");
    }

    [Fact]
    public void APassThroughThatCannotStartTheCommandReportsItAsAFailedLaunch()
    {
        var recorder = new Recorder
        {
            SyncInfo = (Process.GetCurrentProcess(), MainKind.Gui, 1234),
            PassThroughException = new InvalidOperationException("no such program"),
        };

        Run(recorder, Command("run")).Should().Be(125);

        recorder.Errors.Should().ContainSingle().Which.Should().Contain("no such program");
        recorder.Notifications.Should().ContainSingle().Which.Should().Contain("no such program");
        recorder.Calls.Should().NotContain("run-launch");
    }

    [Fact]
    public void TheMessagesForAnOpenWindowDifferFromTheOpenedMeanwhileMessage()
    {
        ProcessStartup.GuiDeclinedMessage("x").Should().NotBe(ProcessStartup.OpenedMeanwhileMessage);
        ProcessStartup.GuiUnavailableMessage("x").Should().NotBe(ProcessStartup.OpenedMeanwhileMessage);
        ProcessStartup.DryRunNeedsHeadlessMessage.Should().NotBe(ProcessStartup.OpenedMeanwhileMessage);
    }

    [Theory]
    [InlineData(MainKind.HeadlessRun)]
    [InlineData(MainKind.HeadlessCli)]
    public void RunAgainstALiveHeadlessMainRefusesWithThatMainsDescriptionAndTouchesNothing(MainKind kind)
    {
        var recorder = new Recorder
        {
            SyncInfo = (Process.GetCurrentProcess(), kind, 0),
            Describe = (k, pid) => $"described {k} {pid}",
        };

        Run(recorder, Command("run")).Should().Be(125);

        var expected = $"described {kind} {Environment.ProcessId}";
        recorder.Described.Should().Equal((kind, Environment.ProcessId));
        recorder.Errors.Should().ContainSingle().Which.Should().Be(expected);
        recorder.Notifications.Should().ContainSingle().Which.Should().Be(expected);
        recorder.Calls.Should()
            .NotContain("claim").And.NotContain("host-start").And.NotContain("open-database")
            .And.NotContain("run-launch").And.NotContain("release-main");
    }

    [Theory]
    [InlineData("protocol-invoke", true)]
    [InlineData("some-verb", false)]
    public void OnlyAProtocolLinkTellsTheDesktopThatAHeadlessMainRefusedIt(string verb, bool notified)
    {
        // A link is clicked in a browser, which shows no stderr.
        var recorder = new Recorder { Describe = (k, pid) => $"described {k} {pid}" };
        var steps = recorder.Steps() with
        {
            RunCliRemotely = (_, _) => Task.FromException<int>(new HeadlessMainRunning(MainKind.HeadlessRun, 4242)),
        };
        var args = new[] { verb, "nxm://skyrim/mods/1" };

        ProcessStartup.Run(StartupMode.Parse(args), args, steps).Should().NotBe(0);

        recorder.Errors.Should().ContainSingle().Which.Should().Be("described HeadlessRun 4242");
        if (notified) recorder.Notifications.Should().ContainSingle().Which.Should().Be("described HeadlessRun 4242");
        else recorder.Notifications.Should().BeEmpty();
    }

    [Theory]
    [InlineData("status")]
    [InlineData("run", "--help")]
    [InlineData("selftest-helper", "ping")]
    public void ModesAnsweredWithoutAHostAreRefusedBeforeBuildingOne(params string[] args)
    {
        // Program.Main answers these itself; reaching here would build a host and exit 0 having done nothing.
        var recorder = new Recorder();

        var act = () => ProcessStartup.Run(StartupMode.Parse(args), args, recorder.Steps());

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{args[0]}*");
        recorder.Calls.Should().BeEmpty();
    }

    [Fact]
    public void ARunsSecretsFileReachesTheHostBeforeItIsBuilt()
    {
        var recorder = new Recorder();

        Run(recorder, "run", "a loadout", "--secrets-file", "/run/secrets.env", "--", "a-game").Should().Be(0);

        recorder.HostModes.Should().ContainSingle().Which.SecretsFile!.Value.GetFullPath().Should().Be("/run/secrets.env");
        recorder.Launches.Should().ContainSingle().Which.SecretsFile!.Value.GetFullPath().Should().Be("/run/secrets.env");
    }

    [Fact]
    public void ARunWithoutASecretsFileLeavesTheHostsSecretsFileUnset()
    {
        var recorder = new Recorder();

        Run(recorder, Command("run"));

        recorder.HostModes.Should().ContainSingle().Which.SecretsFile.Should().BeNull();
    }

    [Fact]
    public void ASecretsFileOnARunThatPassesThroughToTheWindowIsIgnoredWithOneWarning()
    {
        var recorder = new Recorder { SyncInfo = (Process.GetCurrentProcess(), MainKind.Gui, 1234), PassThroughExitCode = 0 };

        Run(recorder, "run", "a loadout", "--secrets-file", "/run/secrets.env", "--", "a-game").Should().Be(0);

        recorder.Warnings.Should().ContainSingle().Which.Should().Contain("/run/secrets.env").And.Contain("ignored").And.Contain("window");
    }

    [Fact]
    public void APassThroughWithoutASecretsFileWarnsAboutNone()
    {
        var recorder = new Recorder { SyncInfo = (Process.GetCurrentProcess(), MainKind.Gui, 1234), PassThroughExitCode = 0 };

        Run(recorder, Command("run")).Should().Be(0);

        recorder.Warnings.Should().BeEmpty();
    }

    [Theory]
    [InlineData("nexus-whoami")]
    [InlineData("secrets-status")]
    public void ACredentialVerbWithASecretsFileReachesTheHostBeforeItIsBuilt(string verb)
    {
        var recorder = new Recorder();

        Run(recorder, verb, "--secrets-file", "/run/secrets.env").Should().Be(0);

        recorder.HostModes.Should().ContainSingle().Which.SecretsFile!.Value.GetFullPath().Should().Be("/run/secrets.env");
        recorder.Calls.Should().Contain("cli-remote");
    }

    [Theory]
    [InlineData("nexus-whoami")]
    [InlineData("secrets-status")]
    public void ACredentialVerbWithASecretsFileRefusesWhenAMainIsAliveInsteadOfAnsweringFromItsStore(string verb)
    {
        var recorder = new Recorder { SyncInfo = (Process.GetCurrentProcess(), MainKind.Gui, 1234) };

        Run(recorder, verb, "--secrets-file", "/run/secrets.env").Should().Be(1);

        recorder.Errors.Should().ContainSingle().Which.Should().Be(ProcessStartup.SecretsFileNeedsHeadlessMessage);
        ProcessStartup.SecretsFileNeedsHeadlessMessage.Should().Be("a secrets file applies to a headless process; close the window first");
        recorder.Calls.Should().NotContain("cli-remote").And.NotContain("kill").And.NotContain("can-connect");
    }

    [Fact]
    public void ACredentialVerbRefusesForTheSecretsFileVariableToo()
    {
        var recorder = new Recorder { SyncInfo = (Process.GetCurrentProcess(), MainKind.HeadlessCli, 0) };
        recorder.Environment["SILVERMM_SECRETS_FILE"] = "/run/secrets.env";

        Run(recorder, "nexus-whoami").Should().Be(1);

        recorder.Errors.Should().ContainSingle().Which.Should().Be(ProcessStartup.SecretsFileNeedsHeadlessMessage);
        recorder.Calls.Should().NotContain("cli-remote");
    }

    [Fact]
    public void ACredentialVerbWithNoOverrideIsForwardedToTheLiveMainAsBefore()
    {
        var recorder = new Recorder { SyncInfo = (Process.GetCurrentProcess(), MainKind.Gui, 1234) };

        Run(recorder, "secrets-status").Should().Be(0);

        recorder.Errors.Should().BeEmpty();
        recorder.Calls.Should().Contain("cli-remote");
    }

    [Fact]
    public void AnEmptySecretsFileVariableIsNotAnOverride()
    {
        var recorder = new Recorder { SyncInfo = (Process.GetCurrentProcess(), MainKind.Gui, 1234) };
        recorder.Environment["SILVERMM_SECRETS_FILE"] = "";

        Run(recorder, "nexus-whoami").Should().Be(0);

        recorder.Calls.Should().Contain("cli-remote");
    }

    [Fact]
    public void AnOverrideOnAnotherVerbIsForwardedAsBefore()
    {
        var recorder = new Recorder { SyncInfo = (Process.GetCurrentProcess(), MainKind.Gui, 1234) };
        recorder.Environment["SILVERMM_SECRETS_FILE"] = "/run/secrets.env";

        Run(recorder, "loadouts", "list").Should().Be(0);

        recorder.Calls.Should().Contain("cli-remote");
    }

    [Fact]
    public void AHeadlessMainRunningTheVerbIsNotRefusedBySinceItBuiltItsStoreFromTheOption()
    {
        var recorder = new Recorder();

        Run(recorder, "as-main", "nexus-whoami", "--secrets-file", "/run/secrets.env").Should().Be(0);

        recorder.HostModes.Should().ContainSingle().Which.SecretsFile!.Value.GetFullPath().Should().Be("/run/secrets.env");
        recorder.Calls.Should().Contain("cli-as-main");
        recorder.Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("--secrets-file")]
    [InlineData("--secrets-file=")]
    public void AMalformedSecretsFileOnACredentialVerbIsTurnedAwayBeforeAnyHostIsBuilt(string option)
    {
        var recorder = new Recorder();

        Run(recorder, "nexus-whoami", option).Should().Be(1);

        recorder.Errors.Should().ContainSingle().Which.Should().Contain("--secrets-file needs a value");
        recorder.Calls.Should().BeEmpty();
    }
}
