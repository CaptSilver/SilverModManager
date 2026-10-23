using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Games.TestFramework.Helpers;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.Tests.SkyrimFamily;

/// <summary>
/// Where Skyrim LE, Skyrim VR, Enderal and Enderal SE keep their plugin list and their ini files and saves
/// inside a Proton prefix. Case matters: the prefix is on ext4.
/// </summary>
public class SkyrimFamilyLocationTests(IServiceProvider serviceProvider)
{
    private TGame GameOf<TGame>() where TGame : class, IGame => serviceProvider.GetServices<IGame>().OfType<TGame>().Single();

    [Theory]
    [InlineData(typeof(CreationEngine.SkyrimLE.SkyrimLE), "Skyrim", "Skyrim")]
    [InlineData(typeof(CreationEngine.SkyrimVR.SkyrimVR), "Skyrim VR", "Skyrim VR")]
    [InlineData(typeof(CreationEngine.Enderal.Enderal), "enderal", "Enderal")]
    [InlineData(typeof(CreationEngine.EnderalSE.EnderalSE), "Enderal Special Edition", "Enderal Special Edition")]
    public void ResolvesIntoThePrefix(Type gameType, string appData, string myGames)
    {
        var prefix = ProtonTestPrefix.Create();
        var game = serviceProvider.GetServices<IGame>().Single(g => g.GetType() == gameType);

        var locations = game.GetLocations(prefix.GameFileSystem, prefix.LocatorResult());

        locations[LocationId.AppData].Should().Be(prefix.Home.Combine($"AppData/Local/{appData}"));
        locations[LocationId.Preferences].Should().Be(prefix.Home.Combine($"Documents/My Games/{myGames}"));
    }

    [Fact]
    public void EnderalSE_FromGOG_UsesTheGogFoldersForPluginsAndPreferences()
    {
        var prefix = ProtonTestPrefix.Create();

        var locations = GameOf<CreationEngine.EnderalSE.EnderalSE>().GetLocations(prefix.GameFileSystem, prefix.LocatorResult(GameStore.GOG));

        locations[LocationId.Preferences].Should().Be(prefix.Home.Combine("Documents/My Games/Enderal Special Edition GOG"));
        locations[LocationId.AppData].Should().Be(prefix.Home.Combine("AppData/Local/Enderal Special Edition GOG"));
    }

    [Theory]
    [InlineData(typeof(CreationEngine.SkyrimLE.SkyrimLE), "TESV.exe")]
    [InlineData(typeof(CreationEngine.SkyrimVR.SkyrimVR), "SkyrimVR.exe")]
    [InlineData(typeof(CreationEngine.Enderal.Enderal), "TESV.exe")]
    [InlineData(typeof(CreationEngine.EnderalSE.EnderalSE), "SkyrimSE.exe")]
    public void PrimaryFileIsTheGameExecutable(Type gameType, string exe)
    {
        var game = serviceProvider.GetServices<IGame>().Single(g => g.GetType() == gameType);

        game.GetPrimaryFile(default!).Should().Be(new GamePath(LocationId.Game, exe));
    }
}
