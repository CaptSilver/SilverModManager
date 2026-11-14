using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace SilverModManager.Platform.Gamepad.Tests;

public class StickToDirectionTests
{
    private readonly FakeTimeProvider _time = new();

    private StickToDirection Create() => new(_time);

    private static Stick Left(float x, float y = 0) => new(TimeSpan.Zero, StickSide.Left, x, y);

    [Fact]
    public void ADeflectionPastTheDeadZoneIsOneDPadDown()
    {
        var stick = Create();

        stick.Feed(Left(0.6f)).Should().ContainSingle().Which.Should().BeOfType<ButtonDown>().Which.Button.Should().Be(GamepadButton.DPadRight);
        stick.Feed(Left(0.9f)).Should().BeEmpty();
    }

    [Fact]
    public void ADeflectionUnderTheDeadZoneDoesNothing()
    {
        Create().Feed(Left(0.4f)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(-0.8f, 0f, GamepadButton.DPadLeft)]
    [InlineData(0f, 0.8f, GamepadButton.DPadDown)]
    [InlineData(0f, -0.8f, GamepadButton.DPadUp)]
    [InlineData(0.9f, 0.6f, GamepadButton.DPadRight)]
    public void TheLargerAxisPicksTheDirection(float x, float y, GamepadButton expected)
    {
        Create().Feed(Left(x, y)).OfType<ButtonDown>().Should().ContainSingle().Which.Button.Should().Be(expected);
    }

    [Fact]
    public void ReleaseUsesTheLowerThreshold()
    {
        var stick = Create();
        stick.Feed(Left(0.6f));

        stick.Feed(Left(0.4f)).Should().BeEmpty();
        stick.Feed(Left(0.2f)).Should().ContainSingle().Which.Should().BeOfType<ButtonUp>().Which.Button.Should().Be(GamepadButton.DPadRight);
    }

    [Fact]
    public void HeldDeflectionRepeatsAfter400msThenEvery100ms()
    {
        var stick = Create();
        stick.Feed(Left(0.9f));

        _time.Advance(TimeSpan.FromMilliseconds(399));
        stick.Tick().Should().BeEmpty();

        _time.Advance(TimeSpan.FromMilliseconds(1));
        stick.Tick().Should().ContainSingle().Which.Should().BeOfType<ButtonRepeat>().Which.Button.Should().Be(GamepadButton.DPadRight);

        _time.Advance(TimeSpan.FromMilliseconds(100));
        stick.Tick().Should().ContainSingle().Which.Should().BeOfType<ButtonRepeat>();
    }

    [Fact]
    public void ARepeatThatCameDueWhileNobodyTickedIsNotLost()
    {
        var stick = Create();
        stick.Feed(Left(0.9f));

        _time.Advance(TimeSpan.FromMilliseconds(600));

        stick.Tick().Should().HaveCount(3, "due at 400, 500 and 600 ms");
    }

    [Fact]
    public void ReleasingStopsTheRepeat()
    {
        var stick = Create();
        stick.Feed(Left(0.9f));
        stick.Feed(Left(0f));

        _time.Advance(TimeSpan.FromSeconds(1));

        stick.Tick().Should().BeEmpty();
    }

    [Fact]
    public void SwingingToTheOppositeSideReleasesThenPresses()
    {
        var stick = Create();
        stick.Feed(Left(0.9f));

        var events = stick.Feed(Left(-0.9f)).ToList();

        events.Select(e => e.GetType()).Should().Equal(typeof(ButtonUp), typeof(ButtonDown));
        events.OfType<ButtonDown>().Single().Button.Should().Be(GamepadButton.DPadLeft);
    }

    [Fact]
    public void TheRightStickDoesNotSteerFocus()
    {
        Create().Feed(new Stick(TimeSpan.Zero, StickSide.Right, 0.9f, 0)).Should().BeEmpty();
    }
}
