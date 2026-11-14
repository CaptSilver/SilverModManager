using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Library.Catalog;
using SilverModManager.App.UI.LeftMenu.Loadout;
using SilverModManager.App.UI.Pages.Catalog;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.UI.Tests.Pages;

public class CatalogLeftMenuItemsTests(IServiceProvider provider)
{
    private static readonly LoadoutId SomeLoadout = LoadoutId.From(EntityId.From(0xBEEF));

    private static IModCatalog Catalog(string name, bool serves)
    {
        var catalog = Substitute.For<IModCatalog>();
        catalog.DisplayName.Returns(name);
        catalog.Serves(Arg.Any<IGameData>()).Returns(serves);
        return catalog;
    }

    [Fact]
    public void ACatalogThatServesTheGameGetsAnEntryThatOpensItsPageOfTheLoadout()
    {
        var catalogs = new ModCatalogs([Catalog("Thunderstore", true)]);

        var catalog = CatalogLeftMenuItems.Browsable(catalogs, new ThunderstoreStubbedGame(provider)).Should().ContainSingle().Subject;
        var page = CatalogLeftMenuItems.PageFor(catalog, SomeLoadout);

        page.FactoryId.Should().Be(CatalogPageFactory.StaticId);
        page.Context.Should().Be(new CatalogPageContext { LoadoutId = SomeLoadout, Catalog = "Thunderstore" });
    }

    [Fact]
    public void ACatalogThatDoesNotServeTheGameGetsNoEntry()
    {
        var catalogs = new ModCatalogs([Catalog("Thunderstore", false)]);

        CatalogLeftMenuItems.Browsable(catalogs, new NoNexusIdStubbedGame(provider)).Should().BeEmpty();
    }

    [Fact]
    public void NexusModsKeepsItsOwnEntryAndIsNotListedAgain()
    {
        var catalogs = new ModCatalogs([Catalog("Nexus Mods", true), Catalog("Thunderstore", true)]);

        CatalogLeftMenuItems.Browsable(catalogs, new ThunderstoreStubbedGame(provider)).Select(c => c.DisplayName).Should().Equal("Thunderstore");
    }

    [Fact]
    public void ATabSavedByTheEarlierThunderstorePageStillOpensThunderstore()
    {
        new CatalogPageContext { LoadoutId = SomeLoadout }.Catalog.Should().Be("Thunderstore");
    }
}
