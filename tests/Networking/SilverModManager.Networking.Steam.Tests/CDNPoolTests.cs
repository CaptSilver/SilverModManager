using System.Runtime.CompilerServices;
using FluentAssertions;
using SilverModManager.Abstractions.Steam.Values;
using SteamKit2.CDN;
using Xunit;

namespace SilverModManager.Networking.Steam.Tests;

public class CDNPoolTests
{
    [Fact]
    public async Task AServerWithoutAHostThrowsNamingTheServer()
    {
        // The host is checked before the session is touched, so no live session is needed.
        var pool = new CDNPool(null!);
        // Server cannot be built with a null host through its API, so skip the constructors the way a deserialiser would.
        var server = (Server)RuntimeHelpers.GetUninitializedObject(typeof(Server));

        var act = async () => await pool.GetCDNAuthTokenAsync(AppId.From(1), DepotId.From(2), server);

        (await act.Should().ThrowAsync<InvalidDataException>()).WithMessage("*has no host*");
    }
}
