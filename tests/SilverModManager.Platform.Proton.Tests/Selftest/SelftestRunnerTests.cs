using System.ComponentModel;
using CliWrap;
using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Selftest;
using SilverModManager.Platform.Proton.Steam;
using SilverModManager.Sdk;
using NSubstitute;

namespace SilverModManager.Platform.Proton.Tests.Selftest;

/// <summary>
/// The selftest checks, run against silvermod-testhost standing in for the shipped binary. The flock and
/// signal checks start it as a child process; the dependency probes go through a fake process runner.
/// </summary>
public class SelftestRunnerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("silvermod-selftest-").FullName;
    private readonly IFileSystem _fs = FileSystem.Shared;
    private readonly Dictionary<string, string> _env = new();
    private readonly HashSet<string> _installed = [];
    private readonly HashSet<string> _throwing = [];
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();

    public SelftestRunnerTests()
    {
        _processRunner
            .RunAsync(Arg.Any<Command>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var command = call.Arg<Command>();
                if (_throwing.Contains(command.TargetFilePath)) throw new Win32Exception(2, "No such file or directory");
                var exitCode = _installed.Contains(command.TargetFilePath) ? 0 : 1;
                return new CommandResult(exitCode, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            });
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string P(string relative) => System.IO.Path.Combine(_root, relative);

    private string Script(string name, string body)
    {
        var path = P(name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
        return path;
    }

    /// <summary>
    /// A stand-in for the shipped binary: every argument goes to the test host, which knows the helper commands.
    /// </summary>
    private string Binary() => Script("app/binary", $"exec dotnet '{Launch.TestHost.Dll}' \"$@\"");

    private LauncherShim ShimFor(string target)
    {
        var shim = new LauncherShim(_fs.FromUnsanitizedFullPath(P("data")));
        shim.Write(_fs.FromUnsanitizedFullPath(target));
        return shim;
    }

    private SelftestRunner Runner(string binary, LauncherShim? shim = null, string? devFuse = null)
    {
        var environment = Substitute.For<IEnvironmentReader>();
        environment.Get(Arg.Any<string>()).Returns(call => _env.GetValueOrDefault(call.Arg<string>()));
        return new SelftestRunner(
            _fs.FromUnsanitizedFullPath(binary),
            shim ?? ShimFor(Binary()),
            environment,
            _processRunner,
            scratchRoot: P("scratch"),
            devFusePath: devFuse ?? P("dev-fuse"),
            timeout: TimeSpan.FromSeconds(60));
    }

    private async Task<SelftestCheck> Check(SelftestRunner runner, string name)
    {
        var report = await runner.RunAsync();
        return report.Checks.Single(c => c.Name == name);
    }

    [Fact]
    public async Task Shim_Passes_WhenItExecsTheBinaryAndGetsPong()
    {
        var check = await Check(Runner(Binary()), SelftestRunner.ShimCheck);

        check.Status.Should().Be(SelftestStatus.Passed, check.Detail);
        check.Required.Should().BeTrue();
    }

    [Fact]
    public async Task Shim_Fails_WhenItDoesNotExist()
    {
        var shim = new LauncherShim(_fs.FromUnsanitizedFullPath(P("no-data")));

        var check = await Check(Runner(Binary(), shim), SelftestRunner.ShimCheck);

        check.Status.Should().Be(SelftestStatus.Failed);
        check.Detail.Should().Contain(shim.ShimPath.ToString());
    }

    [Fact]
    public async Task Shim_Fails_WhenItsTargetDoesNotAnswer()
    {
        var silent = Script("silent", "exit 0");

        var check = await Check(Runner(silent, ShimFor(silent)), SelftestRunner.ShimCheck);

        check.Status.Should().Be(SelftestStatus.Failed);
    }

    [Fact]
    public async Task Shim_Fails_WhenItsTargetIsGone()
    {
        var check = await Check(Runner(P("gone"), ShimFor(P("gone"))), SelftestRunner.ShimCheck);

        check.Status.Should().Be(SelftestStatus.Failed);
    }

    [Fact]
    public async Task Shim_Fails_WhenItExecsAnotherWorkingBinary()
    {
        // An older install that still runs answers the ping too; only the path tells it apart.
        var older = Script("older/binary", $"exec dotnet '{Launch.TestHost.Dll}' \"$@\"");

        var check = await Check(Runner(Binary(), ShimFor(older)), SelftestRunner.ShimCheck);

        check.Status.Should().Be(SelftestStatus.Failed);
        check.Detail.Should().Contain(P("app/binary"));
    }

    [Fact]
    public async Task Lock_Passes_WhenTheHelperHoldsItAndThenLetsGo()
    {
        var check = await Check(Runner(Binary()), SelftestRunner.LockCheck);

        check.Status.Should().Be(SelftestStatus.Passed, check.Detail);
        check.Required.Should().BeTrue();
    }

    [Fact]
    public async Task Lock_Fails_WhenTheHelperSaysLockedButHoldsNothing()
    {
        var liar = Script("liar", "echo locked; sleep 3");

        var check = await Check(Runner(liar), SelftestRunner.LockCheck);

        check.Status.Should().Be(SelftestStatus.Failed);
        check.Detail.Should().Contain("not held");
    }

    [Fact]
    public async Task Lock_Fails_WhenTheHelperNeverReportsTheLock()
    {
        var check = await Check(Runner(Script("quiet", "exit 3")), SelftestRunner.LockCheck);

        check.Status.Should().Be(SelftestStatus.Failed);
    }

    [Fact]
    public async Task Lock_Fails_WhenTheBinaryCannotBeStarted()
    {
        var check = await Check(Runner(P("missing-binary")), SelftestRunner.LockCheck);

        check.Status.Should().Be(SelftestStatus.Failed);
        check.Detail.Should().Contain("missing-binary");
    }

    [Fact]
    public async Task Lock_UsesAFolderUnderTheScratchRoot_AndCleansUp()
    {
        await Runner(Binary()).RunAsync();

        Directory.EnumerateFileSystemEntries(P("scratch")).Should().BeEmpty();
    }

    [Fact]
    public async Task Signal_Passes_WhenSigtermEndsTheHelperWith143()
    {
        var check = await Check(Runner(Binary()), SelftestRunner.SignalCheck);

        check.Status.Should().Be(SelftestStatus.Passed, check.Detail);
        check.Required.Should().BeTrue();
    }

    [Fact]
    public async Task Signal_Fails_WhenTheHelperExitsWithAnotherCode()
    {
        var stubborn = Script("stubborn", "trap 'exit 7' TERM; echo ready; while :; do sleep 0.1; done");

        var check = await Check(Runner(stubborn), SelftestRunner.SignalCheck);

        check.Status.Should().Be(SelftestStatus.Failed);
        check.Detail.Should().Contain("7");
    }

    [Fact]
    public async Task Signal_Fails_WhenTheHelperNeverGetsReady()
    {
        var check = await Check(Runner(Script("dead", "exit 1")), SelftestRunner.SignalCheck);

        check.Status.Should().Be(SelftestStatus.Failed);
    }

    [Fact]
    public async Task Signal_GoesThroughTheAppImage_WhenAppImageIsSet()
    {
        _env["APPIMAGE"] = Binary();

        // The binary the process is running as is only a stand-in path here: the AppImage is what gets spawned.
        var check = await Check(Runner(P("not-the-appimage")), SelftestRunner.SignalCheck);

        check.Status.Should().Be(SelftestStatus.Passed, check.Detail);
        check.Detail.Should().Contain("AppImage");
    }

    [Fact]
    public async Task Signal_ReportsAnAppImageThatEatsTheSignal()
    {
        _env["APPIMAGE"] = Script("appimage", "trap 'exit 0' TERM; echo ready; while :; do sleep 0.1; done");

        var check = await Check(Runner(Binary()), SelftestRunner.SignalCheck);

        check.Status.Should().Be(SelftestStatus.Failed);
        check.Detail.Should().Contain("AppImage");
    }

    [Fact]
    public async Task Signal_Fails_WhenTheHelperExitsBeforeTheSignal()
    {
        // Ignoring TERM keeps an early signal from killing the script: a death by SIGTERM reports 143,
        // the same code the real stop handler uses, and would read as a pass.
        var check = await Check(Runner(Script("quitter", "trap '' TERM; echo ready; exit 0")), SelftestRunner.SignalCheck);

        check.Status.Should().Be(SelftestStatus.Failed);
    }

    [Fact]
    public async Task Terminate_ReturnsFalse_WhenTheProcessHasAlreadyExited()
    {
        using var helper = HelperProcess.Start("/bin/true");
        (await helper.WaitForExitAsync(TimeSpan.FromSeconds(30), CancellationToken.None)).Should().Be(0);

        helper.Terminate().Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_Throws_WhenCancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var run = () => Runner(Binary()).RunAsync(cts.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task NotifySend_Passes_WhenOnPath_WithoutRunningIt()
    {
        var marker = P("notify-ran");
        Script("bin/notify-send", $"touch '{marker}'");
        _env["PATH"] = $"{P("empty")}:{P("bin")}";

        var check = await Check(Runner(Binary()), SelftestRunner.NotifySendCheck);

        check.Status.Should().Be(SelftestStatus.Passed);
        check.Detail.Should().Contain(P("bin/notify-send"));
        File.Exists(marker).Should().BeFalse("the selftest looks the program up and never runs it");
    }

    [Fact]
    public async Task NotifySend_Fails_WhenNotOnPath()
    {
        _env["PATH"] = P("bin");

        var check = await Check(Runner(Binary()), SelftestRunner.NotifySendCheck);

        check.Status.Should().Be(SelftestStatus.Failed);
    }

    [Fact]
    public async Task NotifySend_Fails_WhenTheFileIsNotExecutable()
    {
        Directory.CreateDirectory(P("bin"));
        File.WriteAllText(P("bin/notify-send"), "");
        _env["PATH"] = P("bin");

        var check = await Check(Runner(Binary()), SelftestRunner.NotifySendCheck);

        check.Status.Should().Be(SelftestStatus.Failed);
    }

    [Fact]
    public async Task NotifySend_Fails_WhenPathIsUnset()
    {
        var check = await Check(Runner(Binary()), SelftestRunner.NotifySendCheck);

        check.Status.Should().Be(SelftestStatus.Failed);
    }

    [Fact]
    public async Task Fuse_Passes_WhenTheDeviceIsThere()
    {
        File.WriteAllText(P("dev-fuse"), "");

        var check = await Check(Runner(Binary()), SelftestRunner.FuseCheck);

        check.Status.Should().Be(SelftestStatus.Passed);
        check.Required.Should().BeFalse();
    }

    [Fact]
    public async Task Fuse_IsReportedAbsentNotFailed_WhenTheDeviceIsMissing()
    {
        var check = await Check(Runner(Binary()), SelftestRunner.FuseCheck);

        check.Status.Should().Be(SelftestStatus.Absent);
        check.Required.Should().BeFalse();
    }

    [Theory]
    [InlineData(SelftestRunner.UmuCheck, "umu-run")]
    [InlineData(SelftestRunner.WineCheck, "wine")]
    [InlineData(SelftestRunner.FlatpakCheck, "flatpak")]
    public async Task Probe_Passes_WhenTheProgramAnswers(string name, string program)
    {
        _installed.Add(program);

        var check = await Check(Runner(Binary()), name);

        check.Status.Should().Be(SelftestStatus.Passed);
        check.Required.Should().BeFalse();
    }

    [Theory]
    [InlineData(SelftestRunner.UmuCheck, "umu-run")]
    [InlineData(SelftestRunner.WineCheck, "wine")]
    [InlineData(SelftestRunner.FlatpakCheck, "flatpak")]
    public async Task Probe_IsAbsent_WhenTheProgramFailsOrCannotStart(string name, string program)
    {
        (await Check(Runner(Binary()), name)).Status.Should().Be(SelftestStatus.Absent);

        _throwing.Add(program);
        (await Check(Runner(Binary()), name)).Status.Should().Be(SelftestStatus.Absent);
    }

    [Fact]
    public async Task Report_ListsEveryCheckInOrder_AndPassesWhenTheRequiredOnesDo()
    {
        Script("bin/notify-send", "exit 0");
        _env["PATH"] = P("bin");

        var report = await Runner(Binary()).RunAsync();

        report.Checks.Select(c => c.Name).Should().Equal(
            SelftestRunner.ShimCheck, SelftestRunner.LockCheck, SelftestRunner.SignalCheck, SelftestRunner.NotifySendCheck,
            SelftestRunner.FuseCheck, SelftestRunner.UmuCheck, SelftestRunner.WineCheck, SelftestRunner.FlatpakCheck);
        report.Passed.Should().BeTrue(string.Join("; ", report.Checks.Where(c => c.Status == SelftestStatus.Failed).Select(c => $"{c.Name}: {c.Detail}")));
        report.ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task Report_FailsWithExitCode1_WhenARequiredCheckFails()
    {
        var report = await Runner(Binary()).RunAsync();

        report.Checks.Single(c => c.Name == SelftestRunner.NotifySendCheck).Status.Should().Be(SelftestStatus.Failed);
        report.Passed.Should().BeFalse();
        report.ExitCode.Should().Be(1);
    }

    [Fact]
    public void Report_OptionalChecksNeverFailIt()
    {
        var report = new SelftestReport([
            new SelftestCheck("a", SelftestStatus.Passed, true, "fine"),
            new SelftestCheck("b", SelftestStatus.Absent, false, "missing"),
            new SelftestCheck("c", SelftestStatus.Failed, false, "broken"),
        ]);

        report.Passed.Should().BeTrue();
        report.ExitCode.Should().Be(0);
    }

    [Fact]
    public void Report_JsonCarriesTheOutcomeAndEachCheck()
    {
        var report = new SelftestReport([
            new SelftestCheck("a", SelftestStatus.Failed, true, "broken"),
            new SelftestCheck("b", SelftestStatus.Absent, false, "missing"),
        ]);

        using var json = System.Text.Json.JsonDocument.Parse(report.ToJson());

        json.RootElement.GetProperty("passed").GetBoolean().Should().BeFalse();
        var checks = json.RootElement.GetProperty("checks");
        checks.GetArrayLength().Should().Be(2);
        checks[0].GetProperty("name").GetString().Should().Be("a");
        checks[0].GetProperty("status").GetString().Should().Be("failed");
        checks[0].GetProperty("required").GetBoolean().Should().BeTrue();
        checks[1].GetProperty("status").GetString().Should().Be("absent");
    }

    [Fact]
    public void Report_TextHasOneLinePerCheckAndAVerdict()
    {
        var report = new SelftestReport([
            new SelftestCheck("a", SelftestStatus.Passed, true, "fine"),
            new SelftestCheck("b", SelftestStatus.Failed, true, "broken"),
            new SelftestCheck("c", SelftestStatus.Absent, false, "missing"),
        ]);

        report.ToLines().Should().Equal(
            "[ ok ] a: fine",
            "[FAIL] b: broken",
            "[ -- ] c: missing",
            "Some required checks failed.");
    }
}
