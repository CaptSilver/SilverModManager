using System.Reactive.Linq;
using FluentAssertions;
using SilverModManager.App.UI.LeftMenu.Items;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Helpers;
using SilverModManager.Collections;
using DynamicData;
using SilverModManager.App.UI.Pages;
using SilverModManager.App.UI.LeftMenu.Loadout;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.App.UI.WorkspaceSystem;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Models;
using NSubstitute;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages;

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

        // Only the menu's collection list. The whole menu, activated in a test, leaves work running that crashes the
        // test host once the shared database closes after the last test.
        // The menu items watch the active workspace for open pages; a workspace with another id never matches.
        var workspace = Substitute.For<IWorkspaceViewModel>();
        workspace.Id.Returns(WorkspaceId.From(Guid.NewGuid()));
        workspace.Panels.Returns(new System.Collections.ObjectModel.ReadOnlyObservableCollection<IPanelViewModel>([]));
        var controller = Substitute.For<IWorkspaceController>();
        controller.ActiveWorkspace.Returns(workspace);

        Exception? failure = null;
        using var subscription = LoadoutLeftMenuViewModel.ObserveCollectionItems(
                Connection,
                Provider.GetRequiredService<CollectionDownloader>(),
                loadout,
                controller,
                WorkspaceId.From(Guid.NewGuid()),
                Provider)
            .Bind(out var items)
            .Subscribe(_ => { }, e => failure = e);

        CollectionLeftMenuItemViewModel? item = null;
        for (var attempt = 0; attempt < 200 && item is null && failure is null; attempt++)
        {
            item = items.OfType<CollectionLeftMenuItemViewModel>().FirstOrDefault(i => i.CollectionGroupId.Value == group.Id);
            if (item is null) await Task.Delay(25);
        }

        failure.Should().BeNull();
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

    [Fact]
    public async Task RemovingAGame_DoesNotTakeAListForAGameTheAppHasNoTitleFor()
    {
        using var tx = Connection.BeginTransaction();
        var bytes = System.Text.Encoding.UTF8.GetBytes("unknown game list");
        var file = new LibraryFile.New(tx, out var fileId)
        {
            FileName = "unknown.wabbajack",
            Hash = bytes.xxHash3(),
            Size = Size.From((ulong)bytes.Length),
            LibraryItem = new LibraryItem.New(tx, fileId) { Name = "unknown.wabbajack" },
        };
        _ = new WabbajackModlistLibraryFile.New(tx, fileId)
        {
            LibraryFile = file,
            Name = "Unknown game list",
            Author = "Someone",
            Version = "1.0",
            GameType = "NoSuchGame",
            ArchiveCount = 0,
            ArchiveSize = Size.Zero,
            InstallSize = Size.Zero,
            IsNsfw = false,
        };
        var result = await tx.Commit();
        var listId = result.Remap(file).Id;

        // Removing a game deletes what GetAllFiles returns for it.
        var provider = new WabbajackModlistDataProvider(Provider);
        var game = Provider.GetRequiredService<IGameRegistry>().LocateGameInstallations().First().Game.GameId;

        provider.GetAllFiles(game).Select(f => f.Id).Should().NotContain(listId);
    }
}
