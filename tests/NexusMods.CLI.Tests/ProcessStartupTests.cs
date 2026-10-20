using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NexusMods.App;
using NexusMods.Platform.Proton.Launch;
using NexusMods.SingleProcess;
using NexusMods.SingleProcess.Exceptions;

namespace NexusMods.CLI.Tests;

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
        public List<(MainKind Kind, int Pid)> Described { get; } = [];
        public Func<MainKind, int, string>? Describe { get; set; }
        public bool CanConnect { get; set; } = true;
        public int CliExitCode { get; set; }
        public bool KillThrows { get; set; }
        public bool OpenDatabaseThrows { get; set; }
        public List<string> Errors { get; } = [];
        public List<string> Notifications { get; } = [];

        /// <summary>The outcomes of successive claim attempts; the last one repeats.</summary>
        public List<bool> Claims { get; set; } = [true];
        public List<(MainKind Kind, int Port)> ClaimRequests { get; } = [];
        public Exception? OpenDatabaseException { get; set; }
        public Exception? CliException { get; set; }

        public ProcessStartupSteps Steps() => new()
        {
            BuildHost = _ =>
            {
                Calls.Add("build-host");
                return new HostBuilder()
                    .ConfigureServices(services => services.AddHostedService(_ => new RecordingHostedService(Calls)))
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
            DescribeHeadlessMain = (_, kind, pid) =>
            {
                Described.Add((kind, pid));
                return Describe?.Invoke(kind, pid) ?? new HeadlessMainRunning(kind, pid).Message;
            },
            RunUi = _ => Calls.Add("ui"),
            WriteError = Errors.Add,
        };
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

    [Fact]
    public void RunAgainstALiveWindowRefusesWithTheOpenMessageAndTouchesNothing()
    {
        var recorder = new Recorder { SyncInfo = (Process.GetCurrentProcess(), MainKind.Gui, 1234), CanConnect = false };

        Run(recorder, Command("run")).Should().Be(125);

        recorder.Errors.Should().ContainSingle().Which.Should().Be(ProcessStartup.GuiOpenMessage);
        // Steam starts the launch option without a terminal, so the desktop has to hear about it too.
        recorder.Notifications.Should().ContainSingle().Which.Should().Be(ProcessStartup.GuiOpenMessage);
        recorder.Calls.Should()
            .NotContain("claim").And.NotContain("host-start").And.NotContain("open-database").And.NotContain("run-launch")
            .And.NotContain("can-connect").And.NotContain("kill").And.NotContain("release-main");
    }

    [Fact]
    public void TheOpenMessageDiffersFromTheOpenedMeanwhileMessage()
    {
        ProcessStartup.GuiOpenMessage.Should().NotBe(ProcessStartup.OpenedMeanwhileMessage);
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
}
