using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace SilverModManager.Platform.Gamepad.Tests;

public class GamepadPresenceTests
{
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public void NothingTouchedIsNotRecent()
    {
        new GamepadPresence(_time).IsRecent.Should().BeFalse();
    }

    [Fact]
    public void IsRecentIsTrueExactlyAtTheWindowEdge()
    {
        var presence = new GamepadPresence(_time);
        presence.Touched();

        _time.Advance(TimeSpan.FromMilliseconds(150));
        presence.IsRecent.Should().BeTrue();

        _time.Advance(TimeSpan.FromMilliseconds(1));
        presence.IsRecent.Should().BeFalse();
    }

    [Fact]
    public void TouchingAgainRestartsTheWindow()
    {
        var presence = new GamepadPresence(_time);
        presence.Touched();
        _time.Advance(TimeSpan.FromMilliseconds(100));
        presence.Touched();

        _time.Advance(TimeSpan.FromMilliseconds(100));

        presence.IsRecent.Should().BeTrue();
    }
}
