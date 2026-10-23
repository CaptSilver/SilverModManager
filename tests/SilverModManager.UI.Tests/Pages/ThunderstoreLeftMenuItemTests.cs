using FluentAssertions;
using NexusMods.App.UI.LeftMenu.Loadout;
using NexusMods.App.UI.Pages.Thunderstore;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers.StubbedGames;

namespace NexusMods.UI.Tests.Pages;

public class ThunderstoreLeftMenuItemTests(IServiceProvider provider)
{
    private static readonly LoadoutId SomeLoadout = LoadoutId.From(EntityId.From(0xBEEF));

    [Fact]
    public void AThunderstoreGame_GetsAnEntryThatOpensThePageOfTheLoadout()
    {
        var page = ThunderstoreLeftMenuItem.PageFor(new ThunderstoreStubbedGame(provider), SomeLoadout);

        page.Should().NotBeNull();
        page!.FactoryId.Should().Be(ThunderstorePageFactory.StaticId);
        page.Context.Should().Be(new ThunderstorePageContext { LoadoutId = SomeLoadout });
    }

    [Fact]
    public void AGameWhoseModsDoNotComeFromThunderstore_GetsNoEntry()
    {
        ThunderstoreLeftMenuItem.PageFor(new NoNexusIdStubbedGame(provider), SomeLoadout).Should().BeNull();
    }
}
