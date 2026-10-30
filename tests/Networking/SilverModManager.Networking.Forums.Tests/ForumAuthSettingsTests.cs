using FluentAssertions;

namespace SilverModManager.Networking.Forums.Tests;

public class ForumAuthSettingsTests
{
    [Fact]
    public void ClientIdOverridesAreEmptyByDefault()
    {
        var settings = new ForumAuthSettings();

        settings.LoversLabClientId.Should().BeEmpty();
        settings.VectorPlexusClientId.Should().BeEmpty();
    }
}
