using System.Diagnostics;
using System.Runtime.InteropServices;
using FluentAssertions;

namespace NexusMods.Platform.Proton.Tests.Launch;

/// <summary>
/// Each test hosts a LaunchSession over fakes in silvermod-testhost and signals that process, never the xunit one.
/// </summary>
public class LaunchSessionSignalTests : IDisposable
{
    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    private const int SIGTERM = 15, SIGINT = 2;

    private readonly string _runtimeDir = Directory.CreateTempSubdirectory("silvermod-sessionsignal-").FullName;

    public void Dispose() => Directory.Delete(_runtimeDir, recursive: true);

    private static string ReadLine(Process process) =>
        process.StandardOutput.ReadLine() ?? throw new InvalidOperationException($"helper closed its output: {process.StandardError.ReadToEnd()}");

    /// <summary>
    /// Starts the session, waits until it is parked in <paramref name="phase"/>, sends the signals and returns the
    /// helper's final line.
    /// </summary>
    private string RunWithSignals(string phase, int[] signals, bool killFail = false)
    {
        using var host = killFail
            ? TestHost.Start("session", _runtimeDir, phase, "killfail")
            : TestHost.Start("session", _runtimeDir, phase);
        try
        {
            ReadLine(host).Should().Be("ready");
            ReadLine(host).Should().Be($"in {phase}");
            foreach (var signal in signals) kill(host.Id, signal).Should().Be(0, "the helper is our child");

            var result = ReadLine(host);
            host.WaitForExit(TimeSpan.FromSeconds(30)).Should().BeTrue();
            return result;
        }
        finally
        {
            if (!host.HasExited) host.Kill();
        }
    }

    [Fact]
    public void ASignalWhilePreparing_LetsTheApplyFinish_SkipsTheGame_Finishes_AndIs143()
    {
        var result = RunWithSignals("preparing", [SIGTERM]);

        result.Should().Be("result 143 sync,sync", "the apply completes, no gate is prepared, the game never starts and the ingest still runs");
    }

    [Fact]
    public void ASignalWhileRunning_TerminatesTheChildOnce_FinishesOnce_AndIs143()
    {
        var result = RunWithSignals("running", [SIGTERM]);

        result.Should().Be("result 143 sync,prepare,start,terminate,exit,finish,sync");
    }

    [Fact]
    public void ASecondSignalWhileRunning_IsIgnored()
    {
        var result = RunWithSignals("running", [SIGTERM, SIGINT]);

        result.Should().Be("result 143 sync,prepare,start,terminate,exit,finish,sync", "one terminate and one finish, however many signals");
    }

    [Fact]
    public void ASignalWhileFinishing_ChangesNothing_AndTheGamesExitCodeStands()
    {
        var result = RunWithSignals("finishing", [SIGTERM]);

        result.Should().Be("result 0 sync,prepare,start,exit,finish,sync");
    }

    [Fact]
    public void AChildThatSurvivesKillDuringASignal_Is125_WithoutFinishing()
    {
        var result = RunWithSignals("running", [SIGTERM], killFail: true);

        result.Should().Be("result 125 sync,prepare,start,terminate,exit");
    }
}
