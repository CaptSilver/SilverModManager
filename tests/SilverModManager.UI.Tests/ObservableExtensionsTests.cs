using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using FluentAssertions;
using SilverModManager.App.UI.Extensions;

namespace SilverModManager.UI.Tests;

public class ObservableExtensionsTests
{
    [Fact]
    public void ThrottleAfterFirstPassesTheFirstValueAtOnceAndThrottlesTheRest()
    {
        var scheduler = new HistoricalScheduler();
        var source = new Subject<int>();
        var seen = new List<int>();
        using var _ = source.ThrottleAfterFirst(TimeSpan.FromMilliseconds(300), scheduler).Subscribe(seen.Add);

        source.OnNext(1);
        seen.Should().Equal(1);

        source.OnNext(2);
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(299));
        seen.Should().Equal(1);
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(1));
        seen.Should().Equal(1, 2);

        // A value inside the window restarts it, so only the last of a burst comes through.
        source.OnNext(3);
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(100));
        source.OnNext(4);
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(299));
        seen.Should().Equal(1, 2);
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(1));
        seen.Should().Equal(1, 2, 4);
    }
}
