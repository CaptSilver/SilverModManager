using FluentAssertions;
using NexusMods.Networking.NexusWebApi.Auth;

namespace NexusMods.Networking.NexusWebApi.Tests;

public class NexusAuthSettingsTests
{
    [Fact]
    public void OAuthClientIdIsEmptyByDefault()
    {
        new NexusAuthSettings().OAuthClientId.Should().BeEmpty();
    }
}
