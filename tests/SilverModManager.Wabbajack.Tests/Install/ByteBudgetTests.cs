using FluentAssertions;
using SilverModManager.Wabbajack.Install;

namespace SilverModManager.Wabbajack.Tests.Install;

public class ByteBudgetTests
{
    private static async Task<bool> Completes(Task task) => await Task.WhenAny(task, Task.Delay(150)) == task;

    [Fact]
    public async Task ReservationsThatFitAreAdmittedAtOnce()
    {
        var budget = new ByteBudget(100);

        using var a = await budget.Reserve(60, CancellationToken.None);
        using var b = await budget.Reserve(40, CancellationToken.None);
    }

    [Fact]
    public async Task AReservationThatDoesNotFitWaitsForARelease()
    {
        var budget = new ByteBudget(100);
        var first = await budget.Reserve(70, CancellationToken.None);

        var second = budget.Reserve(50, CancellationToken.None).AsTask();

        (await Completes(second)).Should().BeFalse("only 30 bytes are free");
        first.Dispose();
        (await Completes(second)).Should().BeTrue();
    }

    [Fact]
    public async Task ASmallReservationDoesNotOvertakeALargerOneThatIsWaiting()
    {
        var budget = new ByteBudget(100);
        var held = await budget.Reserve(80, CancellationToken.None);
        var large = budget.Reserve(60, CancellationToken.None).AsTask();

        var small = budget.Reserve(10, CancellationToken.None).AsTask();

        (await Completes(small)).Should().BeFalse("the large one asked first, and letting small ones pass would starve it");
        held.Dispose();
        (await Completes(large)).Should().BeTrue();
        (await Completes(small)).Should().BeTrue();
    }

    [Fact]
    public async Task AReservationLargerThanTheWholeBudgetIsAdmittedAloneRatherThanNever()
    {
        var budget = new ByteBudget(100);
        var other = await budget.Reserve(10, CancellationToken.None);

        var huge = budget.Reserve(500, CancellationToken.None).AsTask();

        (await Completes(huge)).Should().BeFalse("something else is still running");
        other.Dispose();
        (await Completes(huge)).Should().BeTrue("with nothing else reserved it may take the budget on its own");
        var after = budget.Reserve(1, CancellationToken.None).AsTask();
        (await Completes(after)).Should().BeFalse("while the oversize one runs nothing else is admitted");
        (await huge).Dispose();
        (await Completes(after)).Should().BeTrue();
    }

    [Fact]
    public async Task ACancelledWaiterLeavesTheQueueAndTheOnesBehindItProceed()
    {
        var budget = new ByteBudget(100);
        var held = await budget.Reserve(100, CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        var cancelled = budget.Reserve(60, cancel.Token).AsTask();
        var behind = budget.Reserve(30, CancellationToken.None).AsTask();

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        held.Dispose();
        (await Completes(behind)).Should().BeTrue();
    }

    [Fact]
    public async Task DisposingAReservationTwiceReleasesItOnce()
    {
        var budget = new ByteBudget(100);
        var a = await budget.Reserve(60, CancellationToken.None);
        using var b = await budget.Reserve(40, CancellationToken.None);

        a.Dispose();
        a.Dispose();

        var next = budget.Reserve(60, CancellationToken.None).AsTask();
        (await Completes(next)).Should().BeTrue();
        var over = budget.Reserve(10, CancellationToken.None).AsTask();
        (await Completes(over)).Should().BeFalse("the budget holds 100, not 160");
    }

    [Theory]
    [InlineData(8L * 1024 * 1024 * 1024, 1L * 1024 * 1024 * 1024)]
    [InlineData(64L * 1024 * 1024 * 1024, 4L * 1024 * 1024 * 1024)]
    [InlineData(512L * 1024 * 1024, 64L * 1024 * 1024)]
    public void TheDefaultIsAnEighthOfMemoryButNeverMoreThanFourGigabytes(long memory, long expected)
        => ByteBudget.CapacityFor(memory).Should().Be(expected);
}
