using FluentAssertions;
using SilverModManager.App.UI.Windows.Xdnd;

namespace SilverModManager.UI.Tests.Windows.Xdnd;

/// <summary>
/// The parts of the libX11 transport that run without an X server. The requests themselves are covered by the Xvfb
/// tests; these pin the paths that must never contact a display.
/// </summary>
public class LibX11TransportTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoDisplayNameOpensNothing(string? displayName)
    {
        LibX11Transport.TryOpen(displayName).Should().BeNull();
    }

    [Fact]
    public void FactoryWithoutDisplayNameOpensNothing()
    {
        new LibX11TransportFactory(() => null).TryOpen().Should().BeNull();
    }

    [Fact]
    public void FactoryAsksForTheDisplayNameEachTimeItOpens()
    {
        var asked = 0;
        var factory = new LibX11TransportFactory(() => { asked++; return null; });

        factory.TryOpen();
        factory.TryOpen();

        asked.Should().Be(2);
    }
}
