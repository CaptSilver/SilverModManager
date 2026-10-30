using FluentAssertions;
using SilverModManager.Wabbajack.Acquire;

namespace SilverModManager.Wabbajack.Tests.Acquire.Hosts;

public class HostThrottleTests
{
    [Fact]
    public async Task AHostNeverHasMoreThanTwoTransfersAtOnce()
    {
        var throttle = new HostThrottle();
        var inside = 0;
        var most = 0;

        await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            using var slot = await throttle.Enter("mega", CancellationToken.None);
            most = Math.Max(most, Interlocked.Increment(ref inside));
            await Task.Delay(20);
            Interlocked.Decrement(ref inside);
        }));

        most.Should().Be(2);
    }

    [Fact]
    public async Task DifferentHostsDoNotWaitForEachOther()
    {
        var throttle = new HostThrottle();
        using var a1 = await throttle.Enter("a", CancellationToken.None);
        using var a2 = await throttle.Enter("a", CancellationToken.None);

        var other = throttle.Enter("b", CancellationToken.None);

        other.IsCompletedSuccessfully.Should().BeTrue();
        (await other).Dispose();
    }

    [Fact]
    public async Task AWaitingTransferIsReleasedWhenAnotherFinishes()
    {
        var throttle = new HostThrottle();
        var first = await throttle.Enter("a", CancellationToken.None);
        using var second = await throttle.Enter("a", CancellationToken.None);
        var third = throttle.Enter("a", CancellationToken.None);
        third.IsCompleted.Should().BeFalse();

        first.Dispose();

        (await third.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task AWaitingTransferCanBeCancelled()
    {
        var throttle = new HostThrottle();
        using var a1 = await throttle.Enter("a", CancellationToken.None);
        using var a2 = await throttle.Enter("a", CancellationToken.None);
        using var cts = new CancellationTokenSource();

        var waiting = throttle.Enter("a", cts.Token);
        await cts.CancelAsync();

        await ((Func<Task>)(async () => await waiting)).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task HostNamesAreMatchedWithoutRegardToCase()
    {
        var throttle = new HostThrottle();
        using var a1 = await throttle.Enter("Mega", CancellationToken.None);
        using var a2 = await throttle.Enter("MEGA", CancellationToken.None);

        var third = throttle.Enter("mega", CancellationToken.None);

        third.IsCompleted.Should().BeFalse();
        a1.Dispose();
        (await third.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }
}
