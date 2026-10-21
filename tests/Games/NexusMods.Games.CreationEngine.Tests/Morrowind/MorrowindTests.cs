using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Games.TestFramework.Helpers;
using NexusMods.Sdk.Games;
using Xunit;

namespace NexusMods.Games.CreationEngine.Tests.Morrowind;

/// <summary>Morrowind keeps everything, Morrowind.ini and Saves included, in the game folder.</summary>
public class MorrowindTests(IServiceProvider serviceProvider)
{
    private IGame Game => serviceProvider.GetServices<IGame>().OfType<CreationEngine.Morrowind.Morrowind>().Single();

    [Fact]
    public void OnlyTheGameFolderIsALocation()
    {
        var prefix = ProtonTestPrefix.Create();

        var locations = Game.GetLocations(prefix.GameFileSystem, prefix.LocatorResult());

        locations.Keys.Should().BeEquivalentTo([LocationId.Game]);
    }

    [Fact]
    public void PrimaryFileIsTheGameExecutable() =>
        Game.GetPrimaryFile(default!).Should().Be(new GamePath(LocationId.Game, "Morrowind.exe"));

    [Fact]
    public void ModsGoIntoDataFiles() =>
        ((IBethesdaGame)Game).DataPath.Should().Be(new GamePath(LocationId.Game, "Data Files"));

    [Fact]
    public void WritesNoPluginListAndChecksNoMasters()
    {
        Game.Should().NotBeAssignableTo<ICreationEngineGame>();
        ((ALoadoutSynchronizer)Game.Synchronizer).IntrinsicFiles(default).Should().BeEmpty();
        Game.DiagnosticEmitters.Select(e => e.GetType()).Should().NotContain(typeof(Emitters.MissingMasterEmitter));
    }

    [Fact]
    public void IsLaunchedDirectlyWithoutAScriptExtender() =>
        serviceProvider.GetServices<ITool>().OfType<RunGameTool<CreationEngine.Morrowind.Morrowind>>().Should().ContainSingle();

    [Fact]
    public void StoreIdsAreTheOnesMorrowindShipsUnder()
    {
        var ids = ((IGameData)Game).StoreIdentifiers;

        ids.SteamAppIds.Should().Equal(22320u);
        ids.GOGProductIds.Should().Equal(1435828767L);
    }

    [Theory]
    [InlineData("Saves/x.ess", true)]
    [InlineData("Saves", true)]
    [InlineData("Data Files/x.esp", false)]
    [InlineData("Morrowind.ini", false)]
    public void ShouldFilter(string path, bool expected) =>
        Game.Synchronizer.GamePathFilter.ShouldFilter(new GamePath(LocationId.Game, path)).Should().Be(expected);
}
