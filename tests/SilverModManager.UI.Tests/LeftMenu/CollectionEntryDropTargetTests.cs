using System.Collections.ObjectModel;
using Avalonia.Input;
using DynamicData;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls.DragAndDrop;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.App.UI.LeftMenu.Loadout;
using SilverModManager.App.UI.Pages.Collections;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.UI.Tests.Pages.Collections;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.LeftMenu;

/// <summary>Every installed collection's menu entry is a drop target for its own collection, read-only ones included.</summary>
public class CollectionEntryDropTargetTests(IServiceProvider provider) : AUiTest(provider)
{
    private async Task<CollectionLeftMenuItemViewModel> InstalledEntry(Loadout.ReadOnly loadout, EntityId group)
    {
        var workspace = Substitute.For<IWorkspaceViewModel>();
        workspace.Id.Returns(WorkspaceId.From(Guid.NewGuid()));
        workspace.Panels.Returns(new ReadOnlyObservableCollection<IPanelViewModel>([]));
        var controller = Substitute.For<IWorkspaceController>();
        controller.ActiveWorkspace.Returns(workspace);

        using var subscription = LoadoutLeftMenuViewModel.ObserveCollectionItems(
                Connection, Provider.GetRequiredService<CollectionProviders>(), loadout, controller, WorkspaceId.From(Guid.NewGuid()), Provider)
            .Bind(out var items)
            .Subscribe(_ => { });
        CollectionLeftMenuItemViewModel? found = null;
        await Eventually(() =>
        {
            found = items.OfType<CollectionLeftMenuItemViewModel>().FirstOrDefault(i => i.CollectionGroupId.Value == group);
            found.Should().NotBeNull();
        });
        return found!;
    }

    private static DragPayload.LibraryItems SomeLibraryItems(Loadout.ReadOnly loadout)
        => new(loadout.LoadoutId, [LibraryItemId.From(EntityId.From(1))]);

    [Fact]
    public async Task ALocalEntry_HasATargetAndAnErrorPresenter()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Local drop {Guid.NewGuid()}");
        var group = await CollectionSeed.AddLocalGroup(Connection, loadout.LoadoutId, "Local drop");

        var entry = await InstalledEntry(loadout, group.Id);

        entry.DropTarget.Should().BeOfType<CollectionDropTarget>();
        entry.ErrorPresenter.Should().NotBeNull("a failed drop has to be shown somewhere");
    }

    [Fact]
    public async Task ANexusEntry_RefusesADropWithItsReason()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Nexus drop {Guid.NewGuid()}");
        var revision = await CollectionSeed.AddRevision(Connection, loadout.InstallationInstance.Game.NexusModsGameId.Value, "Nexus drop");
        var group = await CollectionSeed.AddInstalledGroup(Connection, loadout.LoadoutId, "Nexus drop", revision);

        var entry = await InstalledEntry(loadout, group.Id);

        entry.DropTarget.Should().NotBeNull();
        entry.DropTarget!.Inspect(SomeLibraryItems(loadout))
            .Should().Be(new DropVerdict.Refuse("Nexus drop is a Nexus collection; it decides what it contains"));
    }
}
