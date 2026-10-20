using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using NexusMods.App;
using NexusMods.SingleProcess;
using NexusMods.SingleProcess.Exceptions;

namespace NexusMods.CLI.Tests;

public class ProcessStartupTests
{
    private sealed class Recorder
    {
        public List<string> Calls { get; } = [];
        public (Process? Process, MainKind Kind, int Port) SyncInfo { get; set; } = (null, MainKind.Gui, 0);
        public bool CanConnect { get; set; } = true;
        public int CliExitCode { get; set; }
        public bool KillThrows { get; set; }
        public bool OpenDatabaseThrows { get; set; }
        public List<string> Errors { get; } = [];

        public ProcessStartupSteps Steps() => new()
        {
            BuildHost = _ => { Calls.Add("build-host"); return new HostBuilder().Build(); },
            ReadSyncInfo = _ => { Calls.Add("read-sync"); return SyncInfo; },
            CanConnect = (_, _) => { Calls.Add("can-connect"); return Task.FromResult(CanConnect); },
            Kill = _ => { Calls.Add("kill"); if (KillThrows) throw new InvalidOperationException("gone"); },
            OpenDatabase = _ => { Calls.Add("open-database"); if (OpenDatabaseThrows) throw new InvalidOperationException("db"); },
            InstallGlobalHandlers = _ => Calls.Add("install-handlers"),
            RunCliAsMain = (_, _) => { Calls.Add("cli-as-main"); return Task.FromResult(CliExitCode); },
            RunCliRemotely = (_, _) => { Calls.Add("cli-remote"); return Task.FromResult(CliExitCode); },
            RunUi = _ => Calls.Add("ui"),
            WriteError = Errors.Add,
        };
    }

    private static int Run(Recorder recorder, params string[] args)
        => ProcessStartup.Run(StartupMode.Parse(args), args, recorder.Steps());

    [Fact]
    public void GuiStartWithNoMainBuildsHostOpensDatabaseAndShowsUi()
    {
        var recorder = new Recorder();

        Run(recorder).Should().Be(0);

        recorder.Calls.Should().Equal("build-host", "read-sync", "open-database", "install-handlers", "cli-as-main", "ui");
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
}
