using System.Reactive.Linq;
using FluentAssertions;
using NSubstitute;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Sdk.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Pages.Collections;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Icons;
using DynamicData.Kernel;

namespace SilverModManager.UI.Tests.Pages.Collections;

public class LocalCollectionSourceTests(IServiceProvider provider) : AUiTest(provider)
{
    private async Task<(Loadout.ReadOnly Loadout, CollectionGroup.ReadOnly Group, LocalCollectionSource Source)> Open(string name = "My list")
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Local source {Guid.NewGuid()}");
        var group = await CollectionSeed.AddLocalGroup(Connection, loadout.LoadoutId, name);
        return (loadout, group, new LocalCollectionSource(Provider, loadout.LoadoutId, group));
    }

    [Fact]
    public async Task ItsHeaderShowsNoSourceAndNoNumbers()
    {
        var (_, _, source) = await Open("Plain");

        source.InitialName.Should().Be("Plain");
        (await source.Name.FirstAsync()).Should().Be("Plain");
        source.AuthorName.Should().BeEmpty();
        source.VersionText.Should().BeEmpty();
        source.Stats.Should().Be(new CollectionStats(null, null, null, null, null), "a local collection has no ratings or sizes to show");
        source.Badge.Should().BeNull();
        source.Link.Should().BeNull();
        source.IsReadOnly.Should().BeFalse();
        (await source.StateLine.FirstAsync()).Should().Be(Language.CollectionLoadoutView_CollectionInstalled);
        (await source.Description.FirstAsync()).Should().BeEmpty();
        (await source.TileImage.FirstAsync()).Should().BeNull();
        (await source.Instructions.FirstAsync()).HasAny.Should().BeFalse();
        source.CreateDownloads().Should().BeNull();
    }

    [Fact]
    public async Task ItIsInstalledAndMayBeToggledAndHoldsItsGroup()
    {
        var (_, group, source) = await Open();

        (await source.Group.FirstAsync()).Should().Be(Optional<CollectionGroupId>.Create(group.CollectionGroupId));
        (await source.IsInstalled.FirstAsync()).Should().BeTrue();
        (await source.CanToggle.FirstAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task ItsInstalledPageIsTheLoadoutPageScopedToTheGroupAndItIsNeverPending()
    {
        var (loadout, group, source) = await Open();

        source.InstalledPage(group.CollectionGroupId).Should().Be(new PageData
        {
            FactoryId = LoadoutPageFactory.StaticId,
            Context = new LoadoutPageContext { LoadoutId = loadout.LoadoutId, GroupScope = group.CollectionGroupId },
        });
        source.PendingPage.Should().BeNull();
        source.PendingRemoval.Should().BeNull();
    }

    [Fact]
    public async Task ItsOnlyMoreMenuItemMakesAnEditableCopy()
    {
        var (_, _, source) = await Open();
        var host = Substitute.For<ICollectionPageHost>();

        var items = source.MenuItems(host, viewDownloads: null);

        items.Select(item => item.Header).Should().Equal(Language.CollectionLoadoutView_MakeEditableLocalCopy);
        items[0].Icon.Should().Be(IconValues.Copy);
    }

    [Fact]
    public async Task DeletingItIsCriticalAndFollowsWhetherAnotherEditableCollectionRemains()
    {
        var (loadout, group, source) = await Open();
        var removal = source.InstalledRemoval(group.CollectionGroupId);

        removal.Header.Should().Be(Language.Loadout_DeleteCollection_Menu_Text);
        removal.Icon.Should().Be(IconValues.DeleteOutline);
        removal.Styling.Should().Be(ContextMenuItemStyling.Critical);
        removal.ToolTip.Should().BeNull();

        var canDeleteTheOnlyOne = await removal.CanExecute.FirstAsync().Timeout(TimeSpan.FromSeconds(10));
        await CollectionSeed.AddLocalGroup(Connection, loadout.LoadoutId, "Another");
        var canDeleteWithTwo = await removal.CanExecute.Where(can => can).FirstAsync().Timeout(TimeSpan.FromSeconds(10));

        canDeleteTheOnlyOne.Should().BeFalse("a loadout keeps at least one editable collection");
        canDeleteWithTwo.Should().BeTrue();
    }
}
