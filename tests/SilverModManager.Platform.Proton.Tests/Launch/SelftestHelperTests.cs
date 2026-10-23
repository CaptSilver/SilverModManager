using System.Runtime.InteropServices;
using FluentAssertions;
using SilverModManager.Platform.Proton.Launch;

namespace SilverModManager.Platform.Proton.Tests.Launch;

/// <summary>
/// The helper logic runs in silvermod-testhost: it flocks and handles signals, neither of which may happen in the xunit process.
/// </summary>
public class SelftestHelperTests : IDisposable
{
    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    private readonly string _dir = Directory.CreateTempSubdirectory("selftest-helper").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Ping_PrintsPong_AndExitsZero()
    {
        var (exitCode, output) = TestHost.Run("selftest-helper", "ping");

        exitCode.Should().Be(0);
        output.Should().Be("pong");
    }

    [Fact]
    public void WaitForTerm_ExitsWith143_OnSigterm()
    {
        using var host = TestHost.Start("selftest-helper", "wait-for-term");
        try
        {
            host.StandardOutput.ReadLine().Should().Be("ready");

            kill(host.Id, 15).Should().Be(0);

            host.WaitForExit(TimeSpan.FromSeconds(30)).Should().BeTrue();
            host.ExitCode.Should().Be(143);
        }
        finally
        {
            if (!host.HasExited) host.Kill();
        }
    }

    [Fact]
    public void HoldLock_HoldsTheLock_UntilItExits()
    {
        var lockPath = Path.Combine(_dir, "helper.lock");
        using var host = TestHost.Start("selftest-helper", "hold-lock", lockPath, "30");
        try
        {
            host.StandardOutput.ReadLine().Should().Be("locked");

            using var probe = new InstallationLock(lockPath);
            probe.Probe().Should().BeTrue("the helper holds the lock while it sleeps");
        }
        finally
        {
            host.Kill();
            host.WaitForExit();
        }
    }

    [Fact]
    public void HoldLock_ReleasesTheLock_WhenTheTimeIsUp()
    {
        var lockPath = Path.Combine(_dir, "helper.lock");

        var (exitCode, output) = TestHost.Run("selftest-helper", "hold-lock", lockPath, "0.2");

        exitCode.Should().Be(0);
        output.Should().Be("locked");
        using var probe = new InstallationLock(lockPath);
        probe.Probe().Should().BeFalse();
    }

    [Fact]
    public void HoldLock_ExitsWith3_WhenSomeoneElseHoldsTheLock()
    {
        var lockPath = Path.Combine(_dir, "helper.lock");
        using var holder = TestHost.StartHolder(lockPath, 30);
        try
        {
            var (exitCode, _) = TestHost.Run("selftest-helper", "hold-lock", lockPath, "1");

            exitCode.Should().Be(3);
        }
        finally
        {
            holder.Kill();
            holder.WaitForExit();
        }
    }

    [Theory]
    [InlineData]
    [InlineData("nonsense")]
    [InlineData("hold-lock", "/x")]
    [InlineData("hold-lock", "/x", "soon")]
    public void UnknownOrMalformedCommand_PrintsUsage_AndExitsWith2(params string[] args)
    {
        var (exitCode, output) = TestHost.Run(["selftest-helper", .. args]);

        exitCode.Should().Be(2);
        output.Should().Contain(SelftestHelper.Usage);
    }
}
