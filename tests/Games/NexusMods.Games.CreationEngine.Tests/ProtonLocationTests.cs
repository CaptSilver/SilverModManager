using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Games.TestFramework.Helpers;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.Tests;

/// <summary>
/// Under Proton the known folders have to land in the prefix's Windows home, since the
/// plugin list and the game's ini files are read from there.
/// </summary>
public class ProtonLocationTests(IServiceProvider serviceProvider)
{
    [Fact]
    public void SkyrimSE_ResolvesIntoThePrefix()
    {
        var prefix = ProtonTestPrefix.Create();
        var game = serviceProvider.GetServices<IGame>().OfType<global::NexusMods.Games.CreationEngine.SkyrimSE.SkyrimSE>().Single();

        var locations = game.GetLocations(prefix.GameFileSystem, prefix.LocatorResult());

        locations[LocationId.AppData].Should().Be(prefix.Home.Combine("AppData/Local/Skyrim Special Edition"));
        locations[LocationId.Preferences].Should().Be(prefix.Home.Combine("Documents/My Games/Skyrim Special Edition"));
    }

    [Fact]
    public void SkyrimSE_FromGOG_UsesTheGogPreferencesFolder()
    {
        var prefix = ProtonTestPrefix.Create();
        var game = serviceProvider.GetServices<IGame>().OfType<global::NexusMods.Games.CreationEngine.SkyrimSE.SkyrimSE>().Single();

        var locations = game.GetLocations(prefix.GameFileSystem, prefix.LocatorResult(GameStore.GOG));

        locations[LocationId.Preferences].Should().Be(prefix.Home.Combine("Documents/My Games/Skyrim Special Edition GOG"));
    }

    [Fact]
    public void Fallout4_ResolvesIntoThePrefix()
    {
        var prefix = ProtonTestPrefix.Create();
        var game = serviceProvider.GetServices<IGame>().OfType<global::NexusMods.Games.CreationEngine.Fallout4.Fallout4>().Single();

        var locations = game.GetLocations(prefix.GameFileSystem, prefix.LocatorResult());

        locations[LocationId.AppData].Should().Be(prefix.Home.Combine("AppData/Local/Fallout4"));
        locations[LocationId.Preferences].Should().Be(prefix.Home.Combine("Documents/My Games/Fallout4"));
    }
}
