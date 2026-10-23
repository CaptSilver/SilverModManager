using FluentAssertions;
using SilverModManager.Networking.NexusWebApi.Auth;

namespace SilverModManager.Networking.NexusWebApi.Tests;

public class NexusAuthSettingsTests
{
    [Fact]
    public void OAuthClientIdIsEmptyByDefault()
    {
        new NexusAuthSettings().OAuthClientId.Should().BeEmpty();
    }
}
