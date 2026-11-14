using FluentAssertions;
using R3;

namespace SilverModManager.Platform.Gamepad.Tests;

public class NullGamepadSourceTests
{
    [Fact]
    public void TheNullSourceAlwaysHasAReason()
    {
        IGamepadSource source = new NullGamepadSource("x");

        source.DisabledReason.Should().Be("x");
        source.Pads.Should().BeEmpty();
    }

    [Fact]
    public async Task TheEventsCompleteWithoutAnyEvent()
    {
        var events = await new NullGamepadSource("x").Events.ToListAsync(TestContext.Current.CancellationToken);

        events.Should().BeEmpty();
    }

    [Fact]
    public void ABlankReasonIsRefused()
    {
        var act = () => new NullGamepadSource(" ");

        act.Should().Throw<ArgumentException>();
    }
}
