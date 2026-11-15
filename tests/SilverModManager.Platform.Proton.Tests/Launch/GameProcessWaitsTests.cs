using FluentAssertions;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Tests.Launch;

public class GameProcessWaitsTests
{
    private sealed class FakeGameProcess : IGameProcess
    {
        public readonly TaskCompletionSource<GameProcessResult> Wait = new();
        public int Terminations;
        public IProgress<RunningProcesses>? Progress;
        public int Pid => 1;

        public Task<GameProcessResult> WaitAsync(CancellationToken cancellationToken = default, IProgress<RunningProcesses>? progress = null)
        {
            Progress = progress;
            cancellationToken.Register(() => Wait.TrySetCanceled(cancellationToken));
            return Wait.Task;
        }

        public void Terminate() => Terminations++;
        public void Dispose() { }
    }

    [Fact]
    public async Task AWaitThatThrows_TerminatesTheGameAndRethrowsTheSameException()
    {
        var process = new FakeGameProcess();
        var failure = new InvalidOperationException("scan failed");
        process.Wait.SetException(failure);

        var thrown = await process.Invoking(p => p.WaitOrTerminateAsync(CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();

        thrown.Which.Should().BeSameAs(failure);
        process.Terminations.Should().Be(1);
    }

    [Fact]
    public async Task ACancelledWait_TerminatesTheGameAndThrowsOperationCanceled()
    {
        var process = new FakeGameProcess();
        using var cts = new CancellationTokenSource();
        var wait = process.WaitOrTerminateAsync(cts.Token);

        await cts.CancelAsync();

        await wait.Invoking(w => w).Should().ThrowAsync<OperationCanceledException>();
        process.Terminations.Should().Be(1);
    }

    [Fact]
    public async Task AWaitThatCompletes_ReturnsTheResultAndLeavesTheGameAlone()
    {
        var process = new FakeGameProcess();
        var result = new GameProcessResult(3, null);
        process.Wait.SetResult(result);

        (await process.WaitOrTerminateAsync(CancellationToken.None)).Should().BeSameAs(result);
        process.Terminations.Should().Be(0);
    }

    [Fact]
    public async Task TheProgressIsPassedToTheWait()
    {
        var process = new FakeGameProcess();
        process.Wait.SetResult(new GameProcessResult(0, null));
        var progress = new Progress<RunningProcesses>();

        await process.WaitOrTerminateAsync(CancellationToken.None, progress);

        process.Progress.Should().BeSameAs(progress);
    }
}
