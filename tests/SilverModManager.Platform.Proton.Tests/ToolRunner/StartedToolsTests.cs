using FluentAssertions;
using SilverModManager.Platform.Proton.ToolRunner;

namespace SilverModManager.Platform.Proton.Tests.ToolRunnerRegistry;

public class StartedToolsTests
{
    [Fact]
    public void NameOf_IsNullForAPidNobodyRegistered() => new StartedTools().NameOf(42).Should().BeNull();

    [Fact]
    public void NameOf_IsNullAfterTheRegistrationIsDisposed()
    {
        var tools = new StartedTools();
        var registration = tools.Register(42, "xEdit");
        tools.NameOf(42).Should().Be("xEdit");

        registration.Dispose();

        tools.NameOf(42).Should().BeNull();
    }

    [Fact]
    public void TwoToolsWithOnePidKeepTheLatest()
    {
        var tools = new StartedTools();
        var first = tools.Register(42, "xEdit");
        tools.Register(42, "LOOT");

        tools.NameOf(42).Should().Be("LOOT");

        // The earlier tool closing late must not unregister the one that replaced it.
        first.Dispose();
        tools.NameOf(42).Should().Be("LOOT");
    }
}
