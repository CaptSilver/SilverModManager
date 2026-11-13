using FluentAssertions;
using SilverModManager.Platform.Proton.Launch;

namespace SilverModManager.Platform.Proton.Tests.Launch;

public class HdrLaunchEnvironmentTests
{
    [Fact]
    public void Off_AddsNothing() => HdrLaunchEnvironment.For(enabled: false).Should().BeEmpty();

    [Fact]
    public void On_AddsExactlyTheWaylandAndHdrVariables()
    {
        HdrLaunchEnvironment.For(enabled: true).Should().Equal(
            new KeyValuePair<string, string>("PROTON_ENABLE_WAYLAND", "1"),
            new KeyValuePair<string, string>("PROTON_ENABLE_HDR", "1"));
    }
}
