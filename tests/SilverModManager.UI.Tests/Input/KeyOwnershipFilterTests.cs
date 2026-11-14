using Avalonia.Input;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using SilverModManager.App.UI.Input;
using SilverModManager.Platform.Gamepad;

namespace SilverModManager.UI.Tests.Input;

public class KeyOwnershipFilterTests
{
    private readonly FakeTimeProvider _time = new();

    private KeyOwnershipFilter Create() => new(new GamepadPresence(_time), new GamepadPresence(_time));

    private static KeyEventArgs Key(Key key, KeyDeviceType device = KeyDeviceType.Keyboard) => new()
    {
        RoutedEvent = InputElement.KeyDownEvent,
        Key = key,
        KeyDeviceType = device,
    };

    [Fact]
    public void AKeyboardArrowWithin150msAfterAPadEventIsHandled()
    {
        var filter = Create();
        filter.PadEvent();
        _time.Advance(TimeSpan.FromMilliseconds(150));

        var e = Key(Avalonia.Input.Key.Right);
        filter.OnKeyDown(e);

        e.Handled.Should().BeTrue("the edge is inclusive, so both sides of the hand-over agree");
    }

    [Fact]
    public void AKeyboardArrowAfterTheWindowPassesThrough()
    {
        var filter = Create();
        filter.PadEvent();
        _time.Advance(TimeSpan.FromMilliseconds(151));

        var e = Key(Avalonia.Input.Key.Right);
        filter.OnKeyDown(e);

        e.Handled.Should().BeFalse();
    }

    [Fact]
    public void AGamepadTypedArrowIsNeverFiltered()
    {
        var filter = Create();
        filter.PadEvent();

        var e = Key(Avalonia.Input.Key.Right, KeyDeviceType.Gamepad);
        filter.OnKeyDown(e);

        e.Handled.Should().BeFalse();
    }

    [Theory]
    [InlineData(Avalonia.Input.Key.Up)]
    [InlineData(Avalonia.Input.Key.Down)]
    [InlineData(Avalonia.Input.Key.Left)]
    [InlineData(Avalonia.Input.Key.Right)]
    [InlineData(Avalonia.Input.Key.Enter)]
    [InlineData(Avalonia.Input.Key.Escape)]
    public void EveryNavigationKeyIsFilteredAfterAPadEvent(Key key)
    {
        var filter = Create();
        filter.PadEvent();

        var e = Key(key);
        filter.OnKeyDown(e);

        e.Handled.Should().BeTrue();
    }

    [Fact]
    public void ATypingKeyIsNeverFiltered()
    {
        var filter = Create();
        filter.PadEvent();

        var e = Key(Avalonia.Input.Key.A);
        filter.OnKeyDown(e);

        e.Handled.Should().BeFalse();
    }

    [Fact]
    public void APhysicalNavigationKeyMakesTheKeyboardTheRecentOwner()
    {
        var filter = Create();
        filter.KeyRecent.Should().BeFalse();

        filter.OnKeyDown(Key(Avalonia.Input.Key.Down));

        filter.KeyRecent.Should().BeTrue();
        _time.Advance(TimeSpan.FromMilliseconds(151));
        filter.KeyRecent.Should().BeFalse();
    }

    [Fact]
    public void AFilteredKeyDoesNotCountAsKeyboardUse()
    {
        var filter = Create();
        filter.PadEvent();

        filter.OnKeyDown(Key(Avalonia.Input.Key.Down));

        filter.KeyRecent.Should().BeFalse("the key was the pad's own emulated echo, so the pad's next press must not be dropped for it");
    }
}
