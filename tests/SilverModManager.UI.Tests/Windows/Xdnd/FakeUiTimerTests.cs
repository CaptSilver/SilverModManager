using FluentAssertions;

namespace SilverModManager.UI.Tests.Windows.Xdnd;

public class FakeUiTimerTests
{
    private readonly FakeUiTimer _timer = new();
    private int _fired;

    [Fact]
    public void FireRunsTheCallbackOfTheHandleArmedWithThatDelay()
    {
        _timer.After(TimeSpan.FromSeconds(2), () => _fired += 10);
        _timer.After(TimeSpan.FromMilliseconds(50), () => _fired += 1);

        _timer.Fire(TimeSpan.FromMilliseconds(50));

        _fired.Should().Be(1);
    }

    [Fact]
    public void ADisposedHandleNeverFiresAndFireThrowsWhenNothingIsLive()
    {
        _timer.After(TimeSpan.FromSeconds(2), () => _fired++).Dispose();

        var act = () => _timer.Fire(TimeSpan.FromSeconds(2));

        act.Should().Throw<InvalidOperationException>();
        _fired.Should().Be(0);
    }

    [Fact]
    public void AHandleFiresOnce()
    {
        _timer.After(TimeSpan.FromSeconds(2), () => _fired++);
        _timer.Fire(TimeSpan.FromSeconds(2));

        var act = () => _timer.Fire(TimeSpan.FromSeconds(2));

        act.Should().Throw<InvalidOperationException>();
        _fired.Should().Be(1);
    }

    [Fact]
    public void FireTakesTheOldestLiveHandleOfADelay()
    {
        var first = _timer.After(TimeSpan.FromSeconds(2), () => _fired += 1);
        _timer.After(TimeSpan.FromSeconds(2), () => _fired += 10);
        first.Dispose();

        _timer.Fire(TimeSpan.FromSeconds(2));

        _fired.Should().Be(10);
    }
}
