using FluentAssertions;
using NSubstitute;
using SilverModManager.Abstractions.Library.Catalog;
using SilverModManager.Library.Workshop;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.Library.Tests.Workshop;

public class SteamWorkshopCatalogTests
{
    private static SteamWorkshopCatalog CatalogOf(WorkshopRig rig, IOSInterop os) => new(rig.View, rig.Api, WorkshopSources.RegistryOf(rig.Install), os);

    private static CatalogQuery Query(SteamWorkshopCatalog catalog, string text = "") => new(text, catalog.Sorts[0], null, true);

    [Fact]
    public void ItServesWorkshopGamesOnlyAndSaysSubscribe()
    {
        using var rig = new WorkshopRig();
        var catalog = CatalogOf(rig, Substitute.For<IOSInterop>());

        catalog.DisplayName.Should().Be("Steam Workshop");
        catalog.Serves(rig.Game).Should().BeTrue();
        catalog.Serves(new SilverModManager.Library.Tests.GitHub.FakeGame(GameId.From("Tests.Plain"))).Should().BeFalse();
        catalog.InstallLabel(rig.Game).Should().Be("Subscribe");
    }

    [Fact]
    public async Task SearchListsTheFoldersItemsAsCardsAndFiltersByTitleOrId()
    {
        using var rig = new WorkshopRig();
        rig.Library.Subscribe(100, 1000);
        rig.Library.Subscribe(200, 1000);
        rig.Api.Add(100, "Long War", 500);
        rig.Api.Add(200, "Gotcha Again", 500);
        var catalog = CatalogOf(rig, Substitute.For<IOSInterop>());

        var all = await catalog.Search(rig.Game, Query(catalog), CancellationToken.None);
        var byText = await catalog.Search(rig.Game, Query(catalog, "gotcha"), CancellationToken.None);
        var byId = await catalog.Search(rig.Game, Query(catalog, "100"), CancellationToken.None);

        all.Cards.Select(card => card.Title).Should().Equal("Long War", "Gotcha Again");
        all.NextCursor.Should().BeNull();
        all.Cards[0].Id.Should().Be("100");
        all.Cards[0].Page.Should().Be(new Uri("https://steamcommunity.com/sharedfiles/filedetails/?id=100"));
        byText.Cards.Select(card => card.Id).Should().Equal("200");
        byId.Cards.Select(card => card.Id).Should().Equal("100");
    }

    [Fact]
    public async Task FindAsksSteamForAnItemTheUserHasNotSubscribedTo()
    {
        using var rig = new WorkshopRig();
        rig.Api.Add(555, "Not yet subscribed", 500);
        rig.Api.Add(556, "Another game's item", 500, appId: 1);
        var catalog = CatalogOf(rig, Substitute.For<IOSInterop>());

        (await catalog.Find(rig.Game, "555", CancellationToken.None))!.Title.Should().Be("Not yet subscribed");
        (await catalog.Find(rig.Game, "556", CancellationToken.None)).Should().BeNull("the item is for another game");
        (await catalog.Find(rig.Game, "557", CancellationToken.None)).Should().BeNull("Steam has no such item");
        (await catalog.Find(rig.Game, "not a number", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task SubscribeOpensTheSteamLinkAndReportsSubscribed()
    {
        using var rig = new WorkshopRig();
        rig.Api.Add(555, "Item", 500);
        var os = Substitute.For<IOSInterop>();
        var catalog = CatalogOf(rig, os);
        var card = (await catalog.Find(rig.Game, "555", CancellationToken.None))!;

        var outcome = await catalog.Install(card, default(LoadoutId), CancellationToken.None);

        os.Received(1).OpenUri(new Uri("steam://url/CommunityFilePage/555"));
        outcome.Should().Be(new CatalogInstallOutcome.Subscribed("Steam downloads it; the group updates when it lands"));
    }

    [Fact]
    public void OpenOnWebOpensTheCommunityPageAndNeverTheSteamLink()
    {
        using var rig = new WorkshopRig();
        var os = Substitute.For<IOSInterop>();

        CatalogOf(rig, os).OpenOnWeb(555);

        os.Received(1).OpenUri(new Uri("https://steamcommunity.com/sharedfiles/filedetails/?id=555"));
    }

    [Fact]
    public async Task ACardFromAnotherCatalogIsRefusedLoudly()
    {
        using var rig = new WorkshopRig();
        var catalog = CatalogOf(rig, Substitute.For<IOSInterop>());
        var foreign = new CatalogCard("1", "x", "", "", null, null, "", 0, null, false, null, Substitute.For<ICatalogHandle>());

        var act = () => catalog.Install(foreign, default(LoadoutId), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
