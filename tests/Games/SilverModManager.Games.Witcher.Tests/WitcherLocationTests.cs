using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.Games.Generic.Simple;
using SilverModManager.Games.Witcher.Witcher3;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.Witcher.Tests;

public class WitcherLocationTests(IServiceProvider serviceProvider)
{
    private TGame GameOf<TGame>() where TGame : IGame => serviceProvider.GetServices<IGame>().OfType<TGame>().Single();

    // The Witcher 1 and 2 games are instances of one class, so they are told apart by id.
    private IGame GameOf(GameId gameId) => serviceProvider.GetServices<IGame>().Single(game => game.GameId == gameId);

    private static GameId Witcher1 => SimpleGames.Witcher1.GameId;
    private static GameId Witcher2 => SimpleGames.Witcher2.GameId;

    [Fact]
    public void GameIds_AreTheOnesLoadoutsAlreadyPersist()
    {
        // Pinned as literals: a loadout stores the id string, so a rename would orphan every Witcher 1 and 2 loadout.
        Witcher1.ToString().Should().Be("Witcher.Witcher1");
        Witcher2.ToString().Should().Be("Witcher.Witcher2");
        GameOf(Witcher1).GameId.ToString().Should().Be("Witcher.Witcher1");
        GameOf(Witcher2).GameId.ToString().Should().Be("Witcher.Witcher2");
    }

    [Fact]
    public void DisplayNames_AreUnchanged()
    {
        GameOf(Witcher1).DisplayName.Should().Be("The Witcher");
        GameOf(Witcher2).DisplayName.Should().Be("The Witcher 2: Assassins of Kings");
    }

    [Fact]
    public void PrimaryFiles_AreUnchanged()
    {
        GameOf(Witcher1).GetPrimaryFile(null!).Should().Be(new GamePath(LocationId.Game, "System/witcher.exe"));
        GameOf(Witcher2).GetPrimaryFile(null!).Should().Be(new GamePath(LocationId.Game, "bin/witcher2.exe"));
    }

    [Fact]
    public void Witcher3_MapsDocumentsInsideThePrefix()
    {
        var prefix = ProtonTestPrefix.Create(292030);

        var locations = GameOf<Witcher3Game>().GetLocations(prefix.GameFileSystem, prefix.LocatorResult());

        locations[WitcherConstants.WitcherDocuments].Should().Be(prefix.Home.Combine("Documents/The Witcher 3"));
        locations.Should().ContainKey(LocationId.Game).And.NotContainKey(LocationId.Saves);
    }

    [Fact]
    public void Witcher1_MapsOnlyTheGameFolder()
    {
        var prefix = ProtonTestPrefix.Create(20900);

        GameOf(Witcher1).GetLocations(prefix.GameFileSystem, prefix.LocatorResult()).Keys.Should().Equal(LocationId.Game);
    }

    [Fact]
    public void Witcher2_MapsOnlyTheGameFolder()
    {
        var prefix = ProtonTestPrefix.Create(20920);

        GameOf(Witcher2).GetLocations(prefix.GameFileSystem, prefix.LocatorResult()).Keys.Should().Equal(LocationId.Game);
    }

    [Fact]
    public void StoreIdentifiers_AreTheSteamAppIds()
    {
        GameOf(Witcher1).StoreIdentifiers.SteamAppIds.Should().Equal(20900u);
        GameOf(Witcher2).StoreIdentifiers.SteamAppIds.Should().Equal(20920u);

        foreach (var ids in new[] { GameOf(Witcher1).StoreIdentifiers, GameOf(Witcher2).StoreIdentifiers })
        {
            ids.GOGProductIds.Should().BeEmpty();
            ids.EGSCatalogItemId.Should().BeEmpty();
        }

        var witcher3 = GameOf<Witcher3Game>().StoreIdentifiers;
        witcher3.SteamAppIds.Should().BeEquivalentTo([499450u, 292030u]);
        witcher3.GOGProductIds.Should().BeEquivalentTo([1207664663L, 1495134320L, 1207664643L, 1640424747L]);
        witcher3.EGSCatalogItemId.Should().Equal("725a22e15ed74735bb0d6a19f3cc82d0");
    }

    [Fact]
    public void NexusModsGameIds_ArePinned()
    {
        GameOf(Witcher1).NexusModsGameId.Value.Should().Be(NexusModsGameId.From(150));
        GameOf(Witcher2).NexusModsGameId.Value.Should().Be(NexusModsGameId.From(153));
        Witcher3Game.NexusModsGameId.Value.Should().Be(NexusModsGameId.From(952));
    }

    [Fact]
    public void Witcher1AndWitcher2_RegisterNoSortOrderVariety()
    {
        // The generic mod priority page keys on a game having no variety of its own.
        var witcher1 = (SortOrderManager)GameOf(Witcher1).SortOrderManager;
        witcher1.GetSortOrderVarieties().Should().BeEmpty();

        var witcher2 = (SortOrderManager)GameOf(Witcher2).SortOrderManager;
        witcher2.GetSortOrderVarieties().Should().BeEmpty();
    }
}
