using CliWrap;
using FluentAssertions;
using NexusMods.Sdk;

namespace NexusMods.Sdk.Tests.RuntimeDependency;

/// <summary>
/// A query command that never exits must not hang the dependency check: Flatpak and its host shims can stall
/// indefinitely, and every start of the app runs these probes.
/// </summary>
public class ExecutableRuntimeDependencyTimeoutTests
{
    private static readonly TimeSpan TestPatience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task QueryInstallationInformation_CommandNeverExits_ReportsUnavailableAndCancelsTheCommand()
    {
        var runner = new HangingRunner();
        var dependency = new ProbeUnderTest(runner, TimeSpan.FromMilliseconds(200));

        var result = await dependency.QueryInstallationInformation(CancellationToken.None).AsTask().WaitAsync(TestPatience);

        result.HasValue.Should().BeFalse();
        runner.Calls.Should().Be(1);
        runner.WasCancelled.Should().BeTrue("the runner is what kills the process, and it only does so when its token is cancelled");
    }

    [Fact]
    public async Task QueryInstallationInformation_TimedOutOnce_AsksAgainNextTime()
    {
        var runner = new HangingRunner();
        var dependency = new ProbeUnderTest(runner, TimeSpan.FromMilliseconds(100));

        await dependency.QueryInstallationInformation(CancellationToken.None).AsTask().WaitAsync(TestPatience);
        await dependency.QueryInstallationInformation(CancellationToken.None).AsTask().WaitAsync(TestPatience);

        runner.Calls.Should().Be(2, "a stalled probe says nothing about whether the tool is installed");
    }

    [Fact]
    public async Task QueryInstallationInformation_CallerCancels_StillThrows()
    {
        var dependency = new ProbeUnderTest(new HangingRunner(), TimeSpan.FromMinutes(5));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await dependency.Invoking(d => d.QueryInstallationInformation(cts.Token).AsTask().WaitAsync(TestPatience))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class ProbeUnderTest(IProcessRunner runner, TimeSpan timeout) : ExecutableRuntimeDependency(runner)
    {
        public override string DisplayName => "probe";
        public override string Description => "probe";
        public override Uri Homepage { get; } = new("https://example.invalid/");
        protected override TimeSpan ProbeTimeout => timeout;
        protected override Command BuildQueryCommand(PipeTarget outputPipeTarget) => Cli.Wrap("probe").WithStandardOutputPipe(outputPipeTarget);
        protected override RuntimeDependencyInformation ToInformation(ReadOnlySpan<char> output) => new();
    }

    private sealed class HangingRunner : IProcessRunner
    {
        public int Calls;
        public bool WasCancelled;

        public async Task<CommandResult> RunAsync(Command command, bool logOutput = false, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                throw;
            }
            throw new InvalidOperationException("unreachable");
        }

        public void Run(Command command, bool logOutput = false) => throw new NotSupportedException();
        public Task RunAsync(System.Diagnostics.Process process, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
