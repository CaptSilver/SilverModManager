using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Games.CreationEngine.PluginLists;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.OblivionRemastered;

/// <summary>
/// Oblivion Remastered is an Unreal game with the old Data folder buried in its Content: saves go under My
/// Games, nothing is registered in AppData, and the plugin list sits in the Data folder.
/// </summary>
public class OblivionRemasteredTests(IServiceProvider serviceProvider)
{
    private IGame Game => serviceProvider.GetServices<IGame>().OfType<CreationEngine.OblivionRemastered.OblivionRemastered>().Single();

    [Fact]
    public void SavesAreFoundInMyGamesAndThereIsNoAppDataLocation()
    {
        var prefix = ProtonTestPrefix.Create();

        var locations = Game.GetLocations(prefix.GameFileSystem, prefix.LocatorResult());

        locations.Keys.Should().BeEquivalentTo([LocationId.Game, LocationId.Preferences]);
        locations[LocationId.Preferences].Should().Be(prefix.Home.Combine("Documents/My Games/Oblivion Remastered"));
    }

    [Fact]
    public void PrimaryFileIsTheLauncherAtTheGameRoot() =>
        Game.GetPrimaryFile(default!).Should().Be(new GamePath(LocationId.Game, "OblivionRemastered.exe"));

    [Fact]
    public void ModsGoIntoTheDeepDataFolder() =>
        ((IBethesdaGame)Game).DataPath.Should().Be(new GamePath(LocationId.Game, "OblivionRemastered/Content/Dev/ObvData/Data"));

    [Fact]
    public void KeepsItsPluginListInTheDataFolderAndChecksMasters()
    {
        var game = (ICreationEngineGame)Game;
        var data = new GamePath(LocationId.Game, "OblivionRemastered/Content/Dev/ObvData/Data");

        game.HeaderFormat.Should().Be(PluginFormat.Tes4Oblivion);
        game.PluginsFile.Should().Be(data / "Plugins.txt");
        game.PluginList(null!).Should().BeOfType<TextfileMechanism>();
        game.PluginList(null!).IntrinsicFiles(game, default, serviceProvider).Select(f => f.Path)
            .Should().Equal(data / "Plugins.txt", data / "loadorder.txt");
        Game.DiagnosticEmitters.Select(e => e.GetType()).Should().Contain(typeof(Emitters.MissingMasterEmitter));
    }

    [Fact]
    public void NoPluginIsAMasterWhateverItsFlagSays()
    {
        var game = (ICreationEngineGame)Game;
        var flagged = new PluginHeader("Flagged.esm", [], 0x1, null, null, null, null, PluginFormat.Tes4Oblivion);

        game.MasterFlagIgnored(null!).Should().BeTrue();
        game.IsMaster(flagged, null!).Should().BeFalse();
    }

    [Fact]
    public void StoreIdsAreTheOnesTheGameShipsUnder()
    {
        var ids = ((IGameData)Game).StoreIdentifiers;

        ids.SteamAppIds.Should().Equal(2623190u);
        ids.GOGProductIds.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Preferences", "Saved/SaveGames/x.sav", true)]
    [InlineData("Preferences", "Saved/SaveGames", true)]
    [InlineData("Preferences", "Saved/Config/x.ini", false)]
    [InlineData("Game", "OblivionRemastered/Content/Dev/ObvData/Oblivion.ini", false)]
    [InlineData("Game", "OblivionRemastered/Content/Dev/ObvData/Data/x.esp", false)]
    public void ShouldFilter(string location, string path, bool expected) =>
        Game.Synchronizer.GamePathFilter.ShouldFilter(new GamePath(LocationId.From(location), path)).Should().Be(expected);
}
