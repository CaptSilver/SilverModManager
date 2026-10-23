using System.Reactive.Linq;
using FluentAssertions;
using NexusMods.App.UI.LeftMenu.Items;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
using NexusMods.App.UI.Helpers;
using NexusMods.App.UI.LeftMenu.Loadout;
using NexusMods.App.UI.Pages.LibraryPage;
using NexusMods.App.UI.Pages.LoadoutPage;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Wabbajack.Models;
using NSubstitute;
using Loadout = NexusMods.Sdk.Loadouts.Loadout;

namespace NexusMods.UI.Tests.Pages;

/// <summary>
/// A modlist's group is a collection with no Nexus metadata; the UI has to treat it like any other mutable collection,
/// except as a place to install the user's own mods. Its file is a library item that is never a mod.
/// </summary>
public class WabbajackLoadoutGroupUiTests(IServiceProvider provider) : AUiTest(provider)
{
    private async Task<WabbajackModlistLoadoutGroup.ReadOnly> AddModlistGroup(Loadout.ReadOnly loadout, string name)
    {
        using var tx = Connection.BeginTransaction();
        var bytes = System.Text.Encoding.UTF8.GetBytes(name);
        var file = new LibraryFile.New(tx, out var fileId)
        {
            FileName = $"{name}.wabbajack",
            Hash = bytes.xxHash3(),
            Size = Size.From((ulong)bytes.Length),
            LibraryItem = new LibraryItem.New(tx, fileId) { Name = $"{name}.wabbajack" },
        };
        var modlist = new WabbajackModlistLibraryFile.New(tx, fileId)
        {
            LibraryFile = file,
            Name = name,
            Author = "Someone",
            Version = "1.0",
            GameType = "SkyrimSpecialEdition",
            ArchiveCount = 0,
            ArchiveSize = Size.Zero,
            InstallSize = Size.Zero,
            IsNsfw = false,
        };
        var group = new WabbajackModlistLoadoutGroup.New(tx, out var id)
        {
            ModlistId = modlist,
            CollectionGroup = new CollectionGroup.New(tx, id)
            {
                IsReadOnly = false,
                LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
                {
                    IsGroup = true,
                    LoadoutItem = new LoadoutItem.New(tx, id) { Name = name, LoadoutId = loadout.LoadoutId, IsDisabled = true },
                },
            },
        };
        var result = await tx.Commit();
        return result.Remap(group);
    }

    [Fact]
    public async Task AModlistGroup_ShowsInTheLeftMenusCollectionListUnderTheListsName()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Left menu loadout");
        var group = await AddModlistGroup(loadout, "Left menu list");

        // The menu items watch the active workspace for open pages; a workspace with another id never matches.
        var workspace = Substitute.For<IWorkspaceViewModel>();
        workspace.Id.Returns(WorkspaceId.From(Guid.NewGuid()));
        workspace.Panels.Returns(new System.Collections.ObjectModel.ReadOnlyObservableCollection<IPanelViewModel>([]));
        var controller = Substitute.For<IWorkspaceController>();
        controller.ActiveWorkspace.Returns(workspace);

        var menu = new LoadoutLeftMenuViewModel(
            new LoadoutContext { LoadoutId = loadout.LoadoutId },
            WorkspaceId.From(Guid.NewGuid()),
            controller,
            Provider);
        using var activation = menu.Activator.Activate();

        CollectionLeftMenuItemViewModel? item = null;
        for (var attempt = 0; attempt < 200 && item is null; attempt++)
        {
            item = menu.LeftMenuCollectionItems.OfType<CollectionLeftMenuItemViewModel>().FirstOrDefault(i => i.CollectionGroupId.Value == group.Id);
            if (item is null) await Task.Delay(25);
        }

        item.Should().NotBeNull("the group is a mutable collection of the loadout");
        item!.Text.Value.Value.Should().Be("Left menu list");
        item.IsCollectionReadOnly.Should().BeFalse();
    }

    [Fact]
    public async Task AModlistGroup_CanAlwaysBeDeleted_EvenAsTheOnlyEditableCollection()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Delete loadout");
        var group = await AddModlistGroup(loadout, "Deletable list");

        var canDelete = await CollectionDeleteHelpers.ObserveCanDeleteCollection(CollectionGroupId.From(group.Id), Connection).FirstAsync();

        canDelete.Should().BeTrue();
    }

    [Fact]
    public async Task AModlistFile_IsNotInstalledAsAMod_WhileAnOrdinaryFileIs()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Install selected loadout");
        var group = await AddModlistGroup(loadout, "Selected list");

        using var tx = Connection.BeginTransaction();
        var bytes = System.Text.Encoding.UTF8.GetBytes("a mod");
        var mod = new LibraryFile.New(tx, out var modId)
        {
            FileName = "a mod.7z",
            Hash = bytes.xxHash3(),
            Size = Size.From((ulong)bytes.Length),
            LibraryItem = new LibraryItem.New(tx, modId) { Name = "a mod.7z" },
        };
        var result = await tx.Commit();
        var db = Connection.Db;

        LibraryViewModel.IsInstallableAsMod(LibraryItem.Load(db, group.ModlistId.Value)).Should().BeFalse();
        LibraryViewModel.IsInstallableAsMod(LibraryItem.Load(db, result.Remap(mod).Id)).Should().BeTrue();
    }

    [Fact]
    public async Task AModlistGroup_IsNotOfferedAsAnInstallTarget()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Install target loadout");
        var group = await AddModlistGroup(loadout, "Target list");

        using var tx = Connection.BeginTransaction();
        var mine = new CollectionGroup.New(tx, out var mineId)
        {
            IsReadOnly = false,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, mineId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, mineId) { Name = "My own", LoadoutId = loadout.LoadoutId },
            },
        };
        var result = await tx.Commit();
        var db = Connection.Db;

        LibraryViewModel.IsInstallTarget(CollectionGroup.Load(db, group.Id)).Should().BeFalse("the group stays disabled until the list is installed");
        LibraryViewModel.IsInstallTarget(CollectionGroup.Load(db, result.Remap(mine).Id)).Should().BeTrue();
    }
}
