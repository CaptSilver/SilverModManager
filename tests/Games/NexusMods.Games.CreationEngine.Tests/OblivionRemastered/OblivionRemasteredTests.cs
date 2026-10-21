using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Games.TestFramework.Helpers;
using NexusMods.Sdk.Games;
using Xunit;

namespace NexusMods.Games.CreationEngine.Tests.OblivionRemastered;

/// <summary>
/// Oblivion Remastered is an Unreal game with the old Data folder buried in its Content: saves go under My
/// Games, nothing is registered in AppData, and the game ships its own plugin list.
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
    public void WritesNoPluginListAndChecksNoMasters()
    {
        Game.Should().NotBeAssignableTo<ICreationEngineGame>();
        ((ALoadoutSynchronizer)Game.Synchronizer).IntrinsicFiles(default).Should().BeEmpty();
        Game.DiagnosticEmitters.Select(e => e.GetType()).Should().NotContain(typeof(Emitters.MissingMasterEmitter));
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
