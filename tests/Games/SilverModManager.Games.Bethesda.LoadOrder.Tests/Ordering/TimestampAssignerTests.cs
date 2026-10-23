using FluentAssertions;
using SilverModManager.Games.Bethesda.LoadOrder.Ordering;

namespace SilverModManager.Games.Bethesda.LoadOrder.Tests.Ordering;

public class TimestampAssignerTests
{
    private static readonly DateTimeOffset T0 = new(2024, 5, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EmptyInputWithNoPluginsGivesNothing() => TimestampAssigner.Assign([], 0).Should().BeEmpty();

    [Fact]
    public void DistinctTimestampsComeBackAscending()
    {
        var result = TimestampAssigner.Assign([T0.AddHours(3), T0, T0.AddHours(1)], 3);
        result.Should().Equal(T0, T0.AddHours(1), T0.AddHours(3));
    }

    [Fact]
    public void DuplicatesArePaddedSixtySecondsPastTheLast()
    {
        var result = TimestampAssigner.Assign([T0, T0, T0.AddHours(1), T0.AddHours(1)], 4);
        result.Should().Equal(T0, T0.AddHours(1), T0.AddHours(1).AddSeconds(60), T0.AddHours(1).AddSeconds(120));
    }

    [Fact]
    public void AllEqualTimestampsBecomeOneMinuteApart()
    {
        var result = TimestampAssigner.Assign([T0, T0, T0], 3);
        result.Should().Equal(T0, T0.AddSeconds(60), T0.AddSeconds(120));
    }

    [Fact]
    public void NoExistingTimestampsPadFromTheEpoch()
    {
        var result = TimestampAssigner.Assign([], 2);
        result.Should().Equal(DateTimeOffset.UnixEpoch.AddSeconds(60), DateTimeOffset.UnixEpoch.AddSeconds(120));
    }

    [Fact]
    public void ResultAlwaysHasOneTimestampPerPlugin()
    {
        TimestampAssigner.Assign([T0, T0.AddHours(1), T0.AddHours(2)], 2).Should().HaveCount(2);
    }
}
