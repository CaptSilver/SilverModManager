using System.Diagnostics;
using System.Runtime.InteropServices;
using FluentAssertions;

namespace NexusMods.Platform.Proton.Tests.Launch;

/// <summary>
/// Signals only ever go to the silvermod-testhost helper, never to the xunit process.
/// </summary>
public class StopHandlerTests
{
    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    private const int SIGINT = 2, SIGTERM = 15;

    private static void Send(Process process, int signal) =>
        kill(process.Id, signal).Should().Be(0, "the helper is our child");

    private static string ReadLine(Process process) =>
        process.StandardOutput.ReadLine() ?? throw new InvalidOperationException($"helper closed its output: {process.StandardError.ReadToEnd()}");

    [Fact]
    public void FirstSignal_IsRecordedWithItsPhase_AndCancelsThePreparingToken()
    {
        using var host = TestHost.Start("stop-handler");
        try
        {
            ReadLine(host).Should().Be("ready");

            Send(host, SIGTERM);

            ReadLine(host).Should().Be("signal SIGTERM Preparing cancelled=True");
        }
        finally
        {
            host.Kill();
            host.WaitForExit();
        }
    }

    [Fact]
    public void SecondSignal_IsIgnored_AndTheProcessStaysUp()
    {
        using var host = TestHost.Start("stop-handler");
        ReadLine(host).Should().Be("ready");

        Send(host, SIGTERM);
        ReadLine(host).Should().StartWith("signal SIGTERM");
        Send(host, SIGINT);

        ReadLine(host).Should().Be("count 2 signal SIGTERM");
        host.WaitForExit(TimeSpan.FromSeconds(30)).Should().BeTrue();
        host.ExitCode.Should().Be(0);
    }

    [Fact]
    public void NoSignalHostLifetime_DoesNotRaiseApplicationStopping_OnSigterm()
    {
        var (output, exitCode) = SigtermHost("nosignal");

        output.Should().Be("running");
        exitCode.Should().Be(0);
    }

    [Fact]
    public void ConsoleLifetime_RaisesApplicationStopping_OnSigterm()
    {
        var (output, exitCode) = SigtermHost("console");

        output.Should().Be("stopping");
        exitCode.Should().Be(0);
    }

    private static (string Output, int ExitCode) SigtermHost(string kind)
    {
        using var host = TestHost.Start("host-lifetime", kind);
        try
        {
            ReadLine(host).Should().Be("ready");
            Send(host, SIGTERM);
            host.WaitForExit(TimeSpan.FromSeconds(30)).Should().BeTrue();
            return (host.StandardOutput.ReadToEnd().Trim(), host.ExitCode);
        }
        finally
        {
            if (!host.HasExited) host.Kill();
        }
    }
}
