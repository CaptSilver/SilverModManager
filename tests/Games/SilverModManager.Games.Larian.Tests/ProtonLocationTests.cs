using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.Larian.BaldursGate3;
using SilverModManager.Games.TestFramework.Helpers;

namespace SilverModManager.Games.Larian.Tests;

public class ProtonLocationTests(IServiceProvider serviceProvider)
{
    [Fact]
    public void BaldursGate3_ResolvesIntoThePrefix()
    {
        var prefix = ProtonTestPrefix.Create();
        var game = serviceProvider.GetServices<IGame>().OfType<global::SilverModManager.Games.Larian.BaldursGate3.BaldursGate3>().Single();
        var larian = prefix.Home.Combine("AppData/Local/Larian Studios/Baldur's Gate 3");

        var locations = game.GetLocations(prefix.GameFileSystem, prefix.LocatorResult());

        locations[Bg3Constants.ModsLocationId].Should().Be(larian.Combine("Mods"));
        locations[Bg3Constants.PlayerProfilesLocationId].Should().Be(larian.Combine("PlayerProfiles/Public"));
        locations[Bg3Constants.ScriptExtenderConfigLocationId].Should().Be(larian.Combine("ScriptExtender"));
    }
}
