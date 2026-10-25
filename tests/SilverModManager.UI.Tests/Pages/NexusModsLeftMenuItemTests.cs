using FluentAssertions;
using SilverModManager.App.UI.LeftMenu.Loadout;
using SilverModManager.App.UI.Pages.NexusCatalog;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.UI.Tests.Pages;

public class NexusModsLeftMenuItemTests(IServiceProvider provider)
{
    private static readonly LoadoutId SomeLoadout = LoadoutId.From(EntityId.From(0xBEEF));

    [Fact]
    public void AGameOnNexusMods_GetsAnEntryThatOpensThePageOfTheLoadout()
    {
        var page = NexusModsLeftMenuItem.PageFor(new IdentifiedStubbedGame(GameId.From("OnNexus"), NexusModsGameId.From(4242)), SomeLoadout);

        page.Should().NotBeNull();
        page!.FactoryId.Should().Be(NexusModsPageFactory.StaticId);
        page.Context.Should().Be(new NexusModsPageContext { LoadoutId = SomeLoadout });
    }

    [Fact]
    public void AGameWithoutANexusId_GetsNoEntry()
    {
        NexusModsLeftMenuItem.PageFor(new NoNexusIdStubbedGame(provider), SomeLoadout).Should().BeNull();
    }
}
