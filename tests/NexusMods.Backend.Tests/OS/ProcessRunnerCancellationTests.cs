using System.Text;
using CliWrap;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Backend.Process;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Settings;
using NSubstitute;

namespace NexusMods.Backend.Tests.OS;

/// <summary>
/// These run real child processes (<c>sh</c>, <c>sleep</c>, <c>cat</c>): the point is what the runner leaves
/// behind after a cancellation, which a fake cannot show.
/// </summary>
public class ProcessRunnerCancellationTests
{
    private static IProcessRunner CreateRunner(TimeSpan killGracePeriod)
    {
        var services = new ServiceCollection()
            .AddSingleton<ILogger<ProcessRunner>>(NullLogger<ProcessRunner>.Instance)
            .AddSingleton<IFileSystem>(new InMemoryFileSystem(OSInformation.FakeUnix))
            .AddSingleton(Substitute.For<ISettingsManager>())
            .BuildServiceProvider();

        return new ProcessRunner(services) { KillGracePeriod = killGracePeriod };
    }

    private static Command Shell(string script, PipeTarget output)
        => Cli.Wrap("sh").WithArguments(["-c", script]).WithStandardOutputPipe(output);

    /// <summary>
    /// Waits until the script has printed its pid, which it does before it blocks.
    /// </summary>
    private static async Task<int> WaitForPid(StringBuilder output) => (await WaitForPids(output, count: 1))[0];

    /// <summary>
    /// Waits until the scripts have printed <paramref name="count"/> pids, one per line.
    /// </summary>
    private static async Task<int[]> WaitForPids(StringBuilder output, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var lines = output.ToString().Split('\n');
            var pids = lines.SkipLast(1).Take(count).Select(line => int.TryParse(line, out var pid) ? pid : 0).ToArray();
            if (pids.Length == count && pids.All(pid => pid > 0)) return pids;
            await Task.Delay(20);
        }
        throw new TimeoutException($"The scripts never printed {count} pids");
    }

    private static bool IsRunning(int pid) => Directory.Exists($"/proc/{pid}");

    [Fact]
    public async Task RunAsync_CancelledWhileTheProcessIgnoresInterrupts_KillsIt()
    {
        var runner = CreateRunner(killGracePeriod: TimeSpan.FromMilliseconds(300));
        var output = new StringBuilder();
        using var cts = new CancellationTokenSource();

        var task = runner.RunAsync(Shell("trap '' INT; echo $$; sleep 1000", PipeTarget.ToStringBuilder(output)), cancellationToken: cts.Token);
        var pid = await WaitForPid(output);
        IsRunning(pid).Should().BeTrue();

        await cts.CancelAsync();
        await task.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();

        IsRunning(pid).Should().BeFalse("a cancelled command must not be abandoned");
    }

    [Fact]
    public async Task RunAsync_Cancelled_AsksTheProcessToStopBeforeKillingIt()
    {
        // The grace period is long: only an interrupt can make the script finish within the test's patience.
        // Tools started through a host shim (host-spawn) can only forward a signal, never a kill, so the
        // interrupt is what reaches the real process.
        var runner = CreateRunner(killGracePeriod: TimeSpan.FromSeconds(60));
        var output = new StringBuilder();
        using var cts = new CancellationTokenSource();

        var script = "trap 'kill $child; echo got-interrupt; exit 0' INT; echo $$; sleep 1000 & child=$!; wait";
        var task = runner.RunAsync(Shell(script, PipeTarget.ToStringBuilder(output)), cancellationToken: cts.Token);
        var pid = await WaitForPid(output);

        await cts.CancelAsync();
        await task.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();

        output.ToString().Should().Contain("got-interrupt");
        IsRunning(pid).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_Cancelled_InterruptsTheChildrenOfTheProcessToo()
    {
        // Inside a distrobox, `flatpak` is a shell script that forks `host-spawn` rather than exec'ing it, and a
        // shell holds an interrupt until its foreground child exits. Only an interrupt sent to the child itself
        // reaches host-spawn, which passes it on to the real process on the host. Killing host-spawn instead
        // leaves that process running on the host for good.
        var runner = CreateRunner(killGracePeriod: TimeSpan.FromSeconds(60));
        var output = new StringBuilder();
        using var cts = new CancellationTokenSource();

        const string shim = "echo $$; sh -c \"$1\"; echo shim-done";
        const string child = "trap 'kill $child; echo got-interrupt; exit 0' INT; echo $$; sleep 1000 & child=$!; wait";
        var command = Cli.Wrap("sh").WithArguments(["-c", shim, "shim", child]).WithStandardOutputPipe(PipeTarget.ToStringBuilder(output));

        var task = runner.RunAsync(command, cancellationToken: cts.Token);
        var pids = await WaitForPids(output, count: 2);
        pids[1].Should().NotBe(pids[0], "the shim must run its child as a separate process");

        await cts.CancelAsync();
        await task.Invoking(t => t.WaitAsync(TimeSpan.FromSeconds(20))).Should().ThrowAsync<OperationCanceledException>();

        output.ToString().Should().Contain("got-interrupt");
        IsRunning(pids[1]).Should().BeFalse();
        IsRunning(pids[0]).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_CommandThatReadsStandardInput_DoesNotBlock()
    {
        var runner = CreateRunner(killGracePeriod: TimeSpan.FromSeconds(1));

        // cat only finishes when its stdin reaches end of file: the probe must never inherit a terminal.
        var result = await runner.RunAsync(Cli.Wrap("cat"), cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

        result.ExitCode.Should().Be(0);
    }
}
