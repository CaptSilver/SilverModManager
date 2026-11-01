using System.Collections.ObjectModel;
using DynamicData;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.App.UI.LeftMenu.Loadout;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.App.UI.Pages.Collections;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.UI.Sdk.Icons;
using SilverModManager.UI.Tests.Pages.Collections;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.LeftMenu;

/// <summary>
/// The delete item of a collection's left-menu entry comes from the collection's source, whichever kind it is: what
/// it says, which icon and styling it has, and where the entry leads.
/// </summary>
public class CollectionEntryRemovalTests(IServiceProvider provider) : AUiTest(provider)
{
    private static IWorkspaceController Controller()
    {
        // The menu items watch the active workspace for open pages; a workspace with another id never matches.
        var workspace = Substitute.For<IWorkspaceViewModel>();
        workspace.Id.Returns(WorkspaceId.From(Guid.NewGuid()));
        workspace.Panels.Returns(new ReadOnlyObservableCollection<IPanelViewModel>([]));
        var controller = Substitute.For<IWorkspaceController>();
        controller.ActiveWorkspace.Returns(workspace);
        return controller;
    }

    private async Task<TItem> InstalledEntry<TItem>(Loadout.ReadOnly loadout, IWorkspaceController controller, EntityId group) where TItem : class
    {
        using var subscription = LoadoutLeftMenuViewModel.ObserveCollectionItems(
                Connection, Provider.GetRequiredService<CollectionProviders>(), loadout, controller, WorkspaceId.From(Guid.NewGuid()), Provider)
            .Bind(out var items)
            .Subscribe(_ => { });
        TItem? found = null;
        await Eventually(() =>
        {
            found = items.OfType<CollectionLeftMenuItemViewModel>().FirstOrDefault(i => i.CollectionGroupId.Value == group) as TItem;
            found.Should().NotBeNull();
        });
        return found!;
    }

    private static IContextMenuItem DeleteItem(ILeftMenuItemViewModel entry) => entry.AdditionalContextMenuItems.Last();

    [Fact]
    public async Task ANexusEntryUninstallsWithTheDefaultStyling()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Nexus entry {Guid.NewGuid()}");
        var revision = await CollectionSeed.AddRevision(Connection, loadout.InstallationInstance.Game.NexusModsGameId.Value, "Nexus entry");
        var group = await CollectionSeed.AddInstalledGroup(Connection, loadout.LoadoutId, "Nexus entry", revision);

        var entry = await InstalledEntry<CollectionLeftMenuItemViewModel>(loadout, Controller(), group.Id);

        var delete = DeleteItem(entry);
        delete.Header.Should().Be(Language.CollectionLoadoutView_UninstallCollection);
        delete.Icon.Should().Be(IconValues.PlaylistRemove);
        delete.Styling.Should().Be(ContextMenuItemStyling.Default);
        delete.ToolTip.Should().BeNull("only the page's item has a tooltip");
        entry.IsCollectionReadOnly.Should().BeTrue();
    }

    [Fact]
    public async Task ALocalEntryDeletesInRed()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Local entry {Guid.NewGuid()}");
        var group = await CollectionSeed.AddLocalGroup(Connection, loadout.LoadoutId, "Local entry");

        var entry = await InstalledEntry<CollectionLeftMenuItemViewModel>(loadout, Controller(), group.Id);

        var delete = DeleteItem(entry);
        delete.Header.Should().Be(Language.Loadout_DeleteCollection_Menu_Text);
        delete.Icon.Should().Be(IconValues.DeleteOutline);
        delete.Styling.Should().Be(ContextMenuItemStyling.Critical);
        entry.IsCollectionReadOnly.Should().BeFalse();
    }

    [Fact]
    public async Task AModlistEntryRemovesTheListFromTheLoadoutWithTheDefaultStyling()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Modlist entry {Guid.NewGuid()}");
        var group = await CollectionSeed.AddModlistGroup(Connection, loadout.LoadoutId, "Modlist entry", finished: true);

        var entry = await InstalledEntry<CollectionLeftMenuItemViewModel>(loadout, Controller(), group.Id);

        var delete = DeleteItem(entry);
        delete.Header.Should().Be("Remove modlist");
        delete.Icon.Should().Be(IconValues.PlaylistRemove);
        delete.Styling.Should().Be(ContextMenuItemStyling.Default, "the list stays in the library and the install can be redone");
        entry.IsCollectionReadOnly.Should().BeFalse();
    }

    [Fact]
    public async Task AnInstalledEntryOpensItsSourcesInstalledPage()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Entry page {Guid.NewGuid()}");
        var revision = await CollectionSeed.AddRevision(Connection, loadout.InstallationInstance.Game.NexusModsGameId.Value, "Entry page");
        var group = await CollectionSeed.AddInstalledGroup(Connection, loadout.LoadoutId, "Entry page", revision);
        var controller = Controller();
        var entry = await InstalledEntry<CollectionLeftMenuItemViewModel>(loadout, controller, group.Id);
        var expected = Provider.GetRequiredService<CollectionProviders>().For(group.AsCollectionGroup(), loadout.LoadoutId)
            .InstalledPage(CollectionGroupId.From(group.Id));

        await OnUi(() => entry.NavigateCommand.Execute(NavigationInformation.From(OpenPageBehaviorType.ReplaceTab)).Subscribe());

        await Eventually(() => controller.Received().OpenPage(Arg.Any<WorkspaceId>(), expected, Arg.Any<OpenPageBehavior>(), Arg.Any<bool>(), Arg.Any<bool>()));
    }

    [Fact]
    public async Task APendingNexusEntryDeletesTheRevisionInRedAndOpensTheDownloadPage()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Pending entry {Guid.NewGuid()}");
        var revision = await CollectionSeed.AddRevision(Connection, loadout.InstallationInstance.Game.NexusModsGameId.Value, "Pending entry");
        var controller = Controller();
        var workspaceId = WorkspaceId.From(Guid.NewGuid());

        using var subscription = LoadoutLeftMenuViewModel.ObservePendingItems(
                Provider.GetRequiredService<CollectionProviders>(), loadout, controller, workspaceId, Provider)
            .Bind(out var items)
            .Subscribe(_ => { });
        PendingCollectionLeftMenuItemViewModel? entry = null;
        await Eventually(() =>
        {
            entry = items.OfType<PendingCollectionLeftMenuItemViewModel>().FirstOrDefault(i => i.Text.Value.Value == "Pending entry");
            entry.Should().NotBeNull();
        });

        var delete = DeleteItem(entry!);
        delete.Header.Should().Be(Language.CollectionDownloadView_Menu_DeleteCollectionRevision);
        delete.Icon.Should().Be(IconValues.DeleteOutline);
        delete.Styling.Should().Be(ContextMenuItemStyling.Critical);
        entry!.RightIcon.Should().Be(IconValues.Downloading);
        await OnUi(() => entry.NavigateCommand.Execute(NavigationInformation.From(OpenPageBehaviorType.ReplaceTab)).Subscribe());
        await Eventually(() => controller.Received().OpenPage(workspaceId, Arg.Is<Optional<PageData>>(page => page.HasValue && page.Value.FactoryId == CollectionDownloadPageFactory.StaticId), Arg.Any<OpenPageBehavior>(), Arg.Any<bool>(), Arg.Any<bool>()));
    }
}
