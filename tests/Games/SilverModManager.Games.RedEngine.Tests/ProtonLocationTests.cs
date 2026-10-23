using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.Games.TestFramework.Helpers;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.RedEngine.Tests;

public class ProtonLocationTests(IServiceProvider serviceProvider)
{
    [Fact]
    public void Cyberpunk2077_ResolvesIntoThePrefix()
    {
        var prefix = ProtonTestPrefix.Create();
        var game = serviceProvider.GetServices<IGame>().OfType<Cyberpunk2077Game>().Single();

        var locations = game.GetLocations(prefix.GameFileSystem, prefix.LocatorResult());

        locations[LocationId.AppData].Should().Be(prefix.Home.Combine("AppData/Local/CD Projekt Red/Cyberpunk 2077"));
        locations.Should().NotContainKey(LocationId.Saves, "saves are deliberately not managed");
    }
}
