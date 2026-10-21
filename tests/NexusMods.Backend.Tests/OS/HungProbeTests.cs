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
/// A tool probe whose process ignores interrupts, with a child that does too, must still end: the real runner
/// under the real dependency check, since the timeout only helps if the runner can actually kill the tree.
/// These run real <c>sh</c> processes because the tree left behind is the thing under test.
/// </summary>
public class HungProbeTests
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan KillGracePeriod = TimeSpan.FromMilliseconds(500);

    // Sleeping loops, not busy ones: the tree has to be stopped by a signal, not starved out. The child runs in
    // the background, which a non-interactive shell already starts with SIGINT ignored; the trap makes it explicit.
    private const string HungTree = "trap '' INT; echo $$; sh -c \"trap '' INT; echo \\$\\$; while :; do sleep 1; done\" & while :; do sleep 1; done";

    [Fact]
    public async Task QueryInstallationInformation_ProbeAndItsChildIgnoreInterrupts_ReportsUnavailableAndKillsTheWholeTree()
    {
        var output = new StringBuilder();
        var dependency = new HungProbe(CreateRunner(), ProbeTimeout, HungTree, output);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var query = dependency.QueryInstallationInformation(CancellationToken.None).AsTask();
        var pids = await WaitForPids(output, count: 2);
        var tree = pids.SelectMany(pid => ProcessTree.Descendants(pid).Append(pid)).Distinct().ToArray();
        try
        {
            tree.Should().HaveCountGreaterThanOrEqualTo(2);

            var result = await query.WaitAsync(TimeSpan.FromSeconds(30));
            stopwatch.Stop();

            result.HasValue.Should().BeFalse();
            // The timeout, then the grace period, plus the time to start the tree and poll for its pids.
            stopwatch.Elapsed.Should().BeLessThan(ProbeTimeout + KillGracePeriod + TimeSpan.FromSeconds(10));
            await AssertNoneRunning(tree);
        }
        finally
        {
            // The loops never end on their own, so a failed run must not leave them behind.
            KillSurvivors(tree);
        }
    }

    private static void KillSurvivors(int[] pids)
    {
        foreach (var pid in pids)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                process.Kill();
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                // Already gone.
            }
        }
    }

    private static IProcessRunner CreateRunner()
    {
        var services = new ServiceCollection()
            .AddSingleton<ILogger<ProcessRunner>>(NullLogger<ProcessRunner>.Instance)
            .AddSingleton<IFileSystem>(new InMemoryFileSystem(OSInformation.FakeUnix))
            .AddSingleton(Substitute.For<ISettingsManager>())
            .BuildServiceProvider();

        return new ProcessRunner(services) { KillGracePeriod = KillGracePeriod };
    }

    private static async Task<int[]> WaitForPids(StringBuilder output, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            string text;
            lock (output) text = output.ToString();
            var pids = text.Split('\n').SkipLast(1).Select(line => int.TryParse(line, out var pid) ? pid : 0).Where(pid => pid > 0).ToArray();
            if (pids.Length >= count) return pids;
            await Task.Delay(20);
        }
        throw new TimeoutException($"The probe never printed {count} pids");
    }

    private static async Task AssertNoneRunning(int[] pids)
    {
        // Orphans are reaped by init a moment after they die.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (pids.Any(pid => Directory.Exists($"/proc/{pid}")) && DateTime.UtcNow < deadline) await Task.Delay(20);

        pids.Where(pid => Directory.Exists($"/proc/{pid}")).Should().BeEmpty("no process of the probe's tree may outlive it");
    }

    private sealed class HungProbe(IProcessRunner runner, TimeSpan timeout, string script, StringBuilder output) : ExecutableRuntimeDependency(runner)
    {
        public override string DisplayName => "probe";
        public override string Description => "probe";
        public override Uri Homepage { get; } = new("https://example.invalid/");
        protected override TimeSpan ProbeTimeout => timeout;

        protected override Command BuildQueryCommand(PipeTarget outputPipeTarget)
            => Cli.Wrap("sh").WithArguments(["-c", script]).WithStandardOutputPipe(PipeTarget.Merge(outputPipeTarget, PipeTarget.ToStringBuilder(output)));

        protected override RuntimeDependencyInformation ToInformation(ReadOnlySpan<char> output) => new();
    }
}
