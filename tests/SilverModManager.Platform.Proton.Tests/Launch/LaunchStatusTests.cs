using System.Text.Json;
using FluentAssertions;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Platform.Proton.Verbs;

namespace SilverModManager.Platform.Proton.Tests.Launch;

public class LaunchStatusTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("silvermod-status-").FullName;
    private readonly string _runtimeDir;
    private readonly string _mountInfo;
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();

    public LaunchStatusTests()
    {
        _runtimeDir = Path.Combine(_root, "run");
        Directory.CreateDirectory(_runtimeDir);
        _mountInfo = Path.Combine(_root, "mountinfo");
        File.WriteAllText(_mountInfo, "22 28 0:21 / /proc rw,nosuid - proc proc rw\n29 22 0:25 / /sys rw - sysfs sysfs rw\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string LockPath(string store = "steam", string id = "42") => InstallationLock.PathFor(_runtimeDir, store, id);

    private int Status(params string[] args) => StatusCommand.Run(args, _runtimeDir, _mountInfo, _output, _error);

    private static string ReadLine(System.Diagnostics.Process host) =>
        host.StandardOutput.ReadLine() ?? throw new InvalidOperationException("helper closed its output");

    [Fact]
    public void WithNoRuntimeFolderNothingIsRunning()
    {
        Directory.Delete(_runtimeDir);

        Status().Should().Be(0);

        _output.ToString().Should().Contain("No game is running through SilverModManager").And.Contain("Mounts: none");
    }

    [Fact]
    public void AFreeLockWithNoStateIsNotASession()
    {
        using (var created = new InstallationLock(LockPath())) created.Probe().Should().BeFalse();

        LaunchStatusReport.Read(_runtimeDir, _mountInfo).Launches.Should().BeEmpty();
    }

    [Theory]
    [InlineData("preparing")]
    [InlineData("running")]
    [InlineData("finishing")]
    public void AHeldSessionShowsItsPidLoadoutAndPhase(string phase)
    {
        using var host = TestHost.Start("session", _runtimeDir, phase);
        try
        {
            ReadLine(host).Should().Be("ready");
            ReadLine(host).Should().Be($"in {phase}");

            Status().Should().Be(0);

            var text = _output.ToString();
            text.Should().Contain("steam 42").And.Contain($"pid {host.Id}").And.Contain("loadout 'test-loadout'").And.Contain($"phase {phase}");
            text.Should().NotContain("leftover");
        }
        finally
        {
            host.Kill();
            host.WaitForExit();
        }
    }

    [Fact]
    public void AStateFileWhoseHolderDiedIsShownAsLeftover()
    {
        using (var holder = TestHost.StartHolder(LockPath(), seconds: 30))
        {
            holder.Kill();
            holder.WaitForExit();
        }

        Status().Should().Be(0);

        _output.ToString().Should().Contain("steam 42").And.Contain("phase running").And.Contain("leftover");
    }

    [Fact]
    public void JsonListsEverySessionAndTheMounts()
    {
        using var holder = TestHost.StartHolder(LockPath("gog", "1207658924"), seconds: 30);
        try
        {
            Status("--json").Should().Be(0);

            using var document = JsonDocument.Parse(_output.ToString());
            var session = document.RootElement.GetProperty("launches").EnumerateArray().Should().ContainSingle().Subject;
            session.GetProperty("store").GetString().Should().Be("gog");
            session.GetProperty("storeId").GetString().Should().Be("1207658924");
            session.GetProperty("pid").GetInt32().Should().Be(holder.Id);
            session.GetProperty("loadout").GetString().Should().Be("test-loadout");
            session.GetProperty("phase").GetString().Should().Be("running");
            session.GetProperty("leftover").GetBoolean().Should().BeFalse();
            document.RootElement.GetProperty("mounts").GetArrayLength().Should().Be(0);
        }
        finally
        {
            holder.Kill();
            holder.WaitForExit();
        }
    }

    [Fact]
    public void OnlyMountsOfTheVirtualFileSystemAreListed_WithEscapedSpacesDecoded()
    {
        File.WriteAllText(_mountInfo,
            "22 28 0:21 / /proc rw - proc proc rw\n" +
            "90 28 0:70 / /run/user/1000/silvermod/Skyrim\\040SE rw,nosuid - fuse.silvermod silvermod rw\n" +
            "91 28 0:71 / /mnt/other rw - fuse.sshfs host:/ rw\n");

        LaunchStatusReport.Read(_runtimeDir, _mountInfo).Mounts.Should().Equal("/run/user/1000/silvermod/Skyrim SE");
    }

    [Fact]
    public void AnUnreadableMountTableIsAnErrorNotAnEmptyList()
    {
        var act = () => LaunchStatusReport.Read(_runtimeDir, Path.Combine(_root, "missing"));

        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void AMountTableThatCannotBeOpenedFailsTheCommandAndSaysWhy()
    {
        // Opening a directory fails the way a permission error does: UnauthorizedAccessException, not IOException.
        StatusCommand.Run([], _runtimeDir, _root, _output, _error).Should().Be(1);

        _error.ToString().Should().Contain("Could not read the launch status");
        _output.ToString().Should().BeEmpty();
    }

    [Fact]
    public void WithoutARuntimeDirTheCommandFailsAndSaysWhy()
    {
        StatusCommand.Run([], runtimeDir: null, _mountInfo, _output, _error).Should().Be(1);

        _error.ToString().Should().Contain("XDG_RUNTIME_DIR");
        _output.ToString().Should().BeEmpty();
    }

    [Fact]
    public void AnUnknownOptionPrintsTheUsage()
    {
        Status("--verbose").Should().Be(1);

        _error.ToString().Should().Contain("--verbose").And.Contain(StatusCommand.Synopsis);
    }
}
