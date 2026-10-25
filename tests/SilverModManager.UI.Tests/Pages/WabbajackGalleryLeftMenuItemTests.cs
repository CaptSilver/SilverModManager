using FluentAssertions;
using SilverModManager.App.UI.LeftMenu.Loadout;
using SilverModManager.App.UI.Pages.WabbajackGallery;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.UI.Tests.Pages;

public class WabbajackGalleryLeftMenuItemTests(IServiceProvider provider)
{
    private static readonly LoadoutId SomeLoadout = LoadoutId.From(EntityId.From(0xBEEF));

    [Theory]
    [InlineData("CreationEngine.SkyrimSE")]
    [InlineData("CreationEngine.Fallout4")]
    [InlineData("RedEngine.Cyberpunk2077")]
    public void AGameWithGalleryLists_GetsAnEntryThatOpensThePageOfTheLoadout(string gameId)
    {
        var page = WabbajackGalleryLeftMenuItem.PageFor(new IdentifiedStubbedGame(GameId.From(gameId), NexusModsGameId.From(1)), SomeLoadout);

        page.Should().NotBeNull();
        page!.FactoryId.Should().Be(WabbajackGalleryPageFactory.StaticId);
        page.Context.Should().Be(new WabbajackGalleryPageContext { LoadoutId = SomeLoadout });
    }

    [Fact]
    public void AGameTheGalleryKnowsNothingOf_GetsNoEntry()
    {
        WabbajackGalleryLeftMenuItem.PageFor(new NoNexusIdStubbedGame(provider), SomeLoadout).Should().BeNull();
        WabbajackGalleryLeftMenuItem.PageFor(new IdentifiedStubbedGame(GameId.From("Valve.Portal"), NexusModsGameId.From(1)), SomeLoadout).Should().BeNull();
    }
}
