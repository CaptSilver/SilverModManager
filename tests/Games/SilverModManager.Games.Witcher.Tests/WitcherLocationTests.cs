using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.Games.Witcher.Witcher1;
using SilverModManager.Games.Witcher.Witcher2;
using SilverModManager.Games.Witcher.Witcher3;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.Witcher.Tests;

public class WitcherLocationTests(IServiceProvider serviceProvider)
{
    private TGame GameOf<TGame>() where TGame : IGame => serviceProvider.GetServices<IGame>().OfType<TGame>().Single();

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

        GameOf<Witcher1Game>().GetLocations(prefix.GameFileSystem, prefix.LocatorResult()).Keys.Should().Equal(LocationId.Game);
    }

    [Fact]
    public void Witcher2_MapsOnlyTheGameFolder()
    {
        var prefix = ProtonTestPrefix.Create(20920);

        GameOf<Witcher2Game>().GetLocations(prefix.GameFileSystem, prefix.LocatorResult()).Keys.Should().Equal(LocationId.Game);
    }

    [Fact]
    public void StoreIdentifiers_AreTheSteamAppIds()
    {
        GameOf<Witcher1Game>().StoreIdentifiers.SteamAppIds.Should().Equal(20900u);
        GameOf<Witcher2Game>().StoreIdentifiers.SteamAppIds.Should().Equal(20920u);

        foreach (var ids in new[] { GameOf<Witcher1Game>().StoreIdentifiers, GameOf<Witcher2Game>().StoreIdentifiers })
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
        Witcher1Game.NexusModsGameId.Value.Should().Be(NexusModsGameId.From(150));
        Witcher2Game.NexusModsGameId.Value.Should().Be(NexusModsGameId.From(153));
        Witcher3Game.NexusModsGameId.Value.Should().Be(NexusModsGameId.From(952));
    }

    [Fact]
    public void Witcher1AndWitcher2_RegisterNoSortOrderVariety()
    {
        // The generic mod priority page keys on a game having no variety of its own.
        var witcher1 = (SortOrderManager)GameOf<Witcher1Game>().SortOrderManager;
        witcher1.GetSortOrderVarieties().Should().BeEmpty();

        var witcher2 = (SortOrderManager)GameOf<Witcher2Game>().SortOrderManager;
        witcher2.GetSortOrderVarieties().Should().BeEmpty();
    }
}
