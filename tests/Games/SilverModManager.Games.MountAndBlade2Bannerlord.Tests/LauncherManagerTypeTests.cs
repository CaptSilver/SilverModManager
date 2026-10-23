using Bannerlord.LauncherManager;
using FluentAssertions;
using SilverModManager.Games.MountAndBlade2Bannerlord.LauncherManager;

namespace SilverModManager.Games.MountAndBlade2Bannerlord.Tests;

public class LauncherManagerTypeTests
{
    /// <summary>
    /// The factory hands out the app's LauncherManager handler, which carries the app's own name.
    /// </summary>
    [Fact]
    public void Factory_ReturnsTheSilverModManagerHandler()
    {
        typeof(LauncherManagerFactory).GetMethods()
            .Where(m => m.Name == nameof(LauncherManagerFactory.Get))
            .Select(m => m.ReturnType)
            .Should().OnlyContain(t => t == typeof(LauncherManagerSilverModManager));

        typeof(LauncherManagerSilverModManager).BaseType.Should().Be(typeof(LauncherManagerHandler));
    }
}
