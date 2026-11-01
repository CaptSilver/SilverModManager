using System.Collections.Immutable;
using System.Collections.ObjectModel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.App.UI.LeftMenu.Loadout;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Models;
using SilverModManager.UI.Tests.Framework;

namespace SilverModManager.UI.Tests.LeftMenu;

/// <summary>
/// The Library badge counts what was acquired since the loadout's menu was built or its Library page was last open,
/// from the game's destination slice, never from the size of the library.
/// </summary>
[Collection(HeadlessUi.Collection)]
public class LibraryNewItemsTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);

    private static LoadoutId NewLoadout() => LoadoutId.From(EntityId.From(0x00AB_0000_0000UL + (ulong)Random.Shared.Next(1, int.MaxValue)));
    private static GameId NewGame(string prefix) => GameId.From(prefix + "-" + Guid.NewGuid().ToString("N"));

    /// <summary>Every count the badge saw, in order. Both threads touch it.</summary>
    private sealed class Counts : IDisposable
    {
        private readonly List<int> _seen = [];
        private readonly IDisposable _subscription;

        public Counts(IObservable<int> source) => _subscription = source.Subscribe(count => { lock (_seen) _seen.Add(count); });

        public int? Last { get { lock (_seen) return _seen.Count == 0 ? null : _seen[^1]; } }
        public int Highest { get { lock (_seen) return _seen.Count == 0 ? 0 : _seen.Max(); } }
        public void Dispose() => _subscription.Dispose();
    }

    private async Task<EntityId> AddLocalFile(string name, Destination destination, EntityId? fetchedForList = null)
    {
        using var tx = Connection.BeginTransaction();
        var bytes = System.Text.Encoding.UTF8.GetBytes(name + Guid.NewGuid());
        var file = new LibraryFile.New(tx, out var id)
        {
            FileName = name,
            Hash = bytes.xxHash3(),
            Size = Size.From((ulong)bytes.Length),
            LibraryItem = new LibraryItem.New(tx, id) { Name = name },
        };
        _ = new LocalFile.New(tx, id) { LibraryFile = file, OriginalPath = $"/x/{name}" };
        LibraryDestinations.Write(tx, id, destination);
        if (fetchedForList is { } list)
        {
            var row = new WabbajackModlistArchive.New(tx)
            {
                ModlistId = list,
                Identity = FileIdentity.XxHash64(Hash.From((ulong)Guid.NewGuid().GetHashCode())),
                Size = Size.Zero,
                Name = name,
            };
            tx.Add(row.Id, WabbajackModlistArchive.FetchedFiles, id);
        }
        var result = await tx.Commit();
        return result.Remap(file).Id;
    }

    private async Task<EntityId> AddModlist(string name, string gameType, Destination destination)
    {
        using var tx = Connection.BeginTransaction();
        var bytes = System.Text.Encoding.UTF8.GetBytes(name + Guid.NewGuid());
        var file = new LibraryFile.New(tx, out var id)
        {
            FileName = $"{name}.wabbajack",
            Hash = bytes.xxHash3(),
            Size = Size.From((ulong)bytes.Length),
            LibraryItem = new LibraryItem.New(tx, id) { Name = $"{name}.wabbajack" },
        };
        _ = new LocalFile.New(tx, id) { LibraryFile = file, OriginalPath = $"/x/{name}.wabbajack" };
        _ = new WabbajackModlistLibraryFile.New(tx, id)
        {
            LibraryFile = file, Name = name, Author = "Someone", Version = "1.0", GameType = gameType,
            ArchiveCount = 0, ArchiveSize = Size.Zero, InstallSize = Size.Zero, IsNsfw = false,
        };
        LibraryDestinations.Write(tx, id, destination);
        var result = await tx.Commit();
        return result.Remap(file).Id;
    }

    private LibraryNewItems NewItems() => new(Connection);

    [Fact]
    public async Task AtMenuCreation_CountIsZero_WhateverTheLibraryHolds()
    {
        var game = NewGame("game");
        for (var i = 0; i < 3; i++) await AddLocalFile($"held-{i}.7z", Destination.Of(game));
        await Task.Delay(Settle);

        using var counts = new Counts(NewItems().ObserveNewCount(NewLoadout(), game));

        await Task.Delay(300);
        counts.Last.Should().Be(0);
        counts.Highest.Should().Be(0, "a library that was there before the menu is not new, and never climbs on the way to its size");
    }

    [Fact]
    public async Task ADownloadAfterMenuCreation_Counts_BeforeAnyOpen()
    {
        var game = NewGame("game");
        var newItems = NewItems();
        using var counts = new Counts(newItems.ObserveNewCount(NewLoadout(), game));
        await Task.Delay(Settle);

        await AddLocalFile("later-1.7z", Destination.Of(game));
        await AddLocalFile("later-2.7z", Destination.Of(game));

        await Eventually(() => counts.Last.Should().Be(2), delay: Poll);
    }

    [Fact]
    public async Task AFetchedHiddenModlistArchive_DoesNotCount()
    {
        var game = NewGame("game");
        var list = await AddModlist("A list " + Guid.NewGuid().ToString("N"), "NoSuchGame", Destination.Of(game));
        var newItems = NewItems();
        using var counts = new Counts(newItems.ObserveNewCount(NewLoadout(), game));
        await Task.Delay(Settle);

        // The list's own download: the Mods tab keeps it out of the way, so the badge does too.
        await AddLocalFile("fetched.7z", Destination.Of(game), fetchedForList: list);
        await AddLocalFile("marker.7z", Destination.Of(game));

        await Eventually(() => counts.Last.Should().Be(1), delay: Poll);
        await Task.Delay(300);
        counts.Last.Should().Be(1);
    }

    [Fact]
    public async Task AModlistFile_DoesNotCount()
    {
        var game = NewGame("game");
        var newItems = NewItems();
        using var counts = new Counts(newItems.ObserveNewCount(NewLoadout(), game));
        await Task.Delay(Settle);

        await AddModlist("Another list " + Guid.NewGuid().ToString("N"), "NoSuchGame", Destination.Of(game));
        await AddLocalFile("marker.7z", Destination.Of(game));

        await Eventually(() => counts.Last.Should().Be(1), delay: Poll);
        await Task.Delay(300);
        counts.Last.Should().Be(1, "the Collections tab lists a modlist, not the Mods tab");
    }

    [Fact]
    public async Task AfterMarkSeen_OnlyLaterItemsCount()
    {
        var game = NewGame("game");
        var loadout = NewLoadout();
        var newItems = NewItems();
        using var counts = new Counts(newItems.ObserveNewCount(loadout, game));
        await Task.Delay(Settle);
        await AddLocalFile("before-1.7z", Destination.Of(game));
        await AddLocalFile("before-2.7z", Destination.Of(game));
        await Eventually(() => counts.Last.Should().Be(2), delay: Poll);
        await Task.Delay(Settle);

        newItems.MarkSeen(loadout);
        await Eventually(() => counts.Last.Should().Be(0), delay: Poll);
        await Task.Delay(Settle);
        await AddLocalFile("after.7z", Destination.Of(game));

        await Eventually(() => counts.Last.Should().Be(1), delay: Poll);
    }

    [Fact]
    public async Task MarkSeen_FromAnotherThread_WhileItemsArrive_EndsAtTheRightCount()
    {
        var game = NewGame("game");
        var loadout = NewLoadout();
        var newItems = NewItems();
        using var counts = new Counts(newItems.ObserveNewCount(loadout, game));
        await Task.Delay(Settle);

        var arriving = Task.Run(async () =>
        {
            for (var i = 0; i < 20; i++) await AddLocalFile($"arriving-{i}.7z", Destination.Of(game));
        });
        var seeing = Task.Run(async () =>
        {
            for (var i = 0; i < 20; i++)
            {
                newItems.MarkSeen(loadout);
                await Task.Delay(5);
            }
        });
        await Task.WhenAll(arriving, seeing);
        await Task.Delay(Settle);
        newItems.MarkSeen(loadout);
        await Eventually(() => counts.Last.Should().Be(0), delay: Poll);
        await Task.Delay(Settle);

        await AddLocalFile("last-1.7z", Destination.Of(game));
        await AddLocalFile("last-2.7z", Destination.Of(game));
        await AddLocalFile("last-3.7z", Destination.Of(game));

        await Eventually(() => counts.Last.Should().Be(3), delay: Poll);
    }

    [Fact]
    public async Task ItemsForTheSibling_NeverCount()
    {
        var game = NewGame("game");
        var sibling = NewGame("sibling");
        var newItems = NewItems();
        using var counts = new Counts(newItems.ObserveNewCount(NewLoadout(), game));
        await Task.Delay(Settle);

        for (var i = 0; i < 10; i++) await AddLocalFile($"theirs-{i}.7z", Destination.Of(sibling));
        await AddLocalFile("mine.7z", Destination.Of(game));

        await Eventually(() => counts.Last.Should().Be(1), delay: Poll);
        await Task.Delay(300);
        counts.Last.Should().Be(1);
        counts.Highest.Should().Be(1);
    }

    private static IWorkspaceController Controller(WorkspaceId workspaceId, ObservableCollection<IPanelViewModel> panels)
    {
        var workspace = Substitute.For<IWorkspaceViewModel>();
        workspace.Id.Returns(workspaceId);
        workspace.Panels.Returns(new ReadOnlyObservableCollection<IPanelViewModel>(panels));
        var controller = Substitute.For<IWorkspaceController>();
        controller.ActiveWorkspace.Returns(workspace);
        return controller;
    }

    private static IPanelViewModel PanelShowing(PageData page)
    {
        var tab = Substitute.For<IPanelTabViewModel>();
        tab.Contents.Returns(new Page { ViewModel = Substitute.For<IPageViewModelInterface>(), PageData = page });
        var panel = Substitute.For<IPanelViewModel>();
        panel.SelectedTab.Returns(tab);
        return panel;
    }

    [Fact]
    public async Task LoadoutLeftMenu_BindsTheBadgeToNewItems_AndMarksSeenWhenLibraryIsActive()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Badge loadout");
        var game = loadout.InstallationInstance.Game.GameId;
        await AddLocalFile("already-there.7z", Destination.Of(game));
        await Task.Delay(Settle);

        var workspaceId = WorkspaceId.From(Guid.NewGuid());
        var panels = new ObservableCollection<IPanelViewModel>();
        var controller = Controller(workspaceId, panels);
        var (menu, library) = await OnUi(() =>
        {
            var vm = new LoadoutLeftMenuViewModel(new LoadoutContext { LoadoutId = loadout.LoadoutId }, workspaceId, controller, Provider);
            return Task.FromResult((vm, (LeftMenuItemWithCountBadgeViewModel)vm.LeftMenuItemLibrary));
        });
        using var menuActive = await OnUi(() => Task.FromResult(menu.Activator.Activate()));
        using var libraryActive = await OnUi(() => Task.FromResult(library.Activator.Activate()));

        await Task.Delay(300);
        library.Count.Should().Be(0, "the library the menu found is not new");

        await AddLocalFile("downloaded.7z", Destination.Of(game));
        await EventuallyOnUi(() => library.Count.Should().Be(1), delay: Poll);

        // The Library page opens in the workspace: the badge clears, and stays clear for what was already seen.
        await OnUi(() =>
        {
            panels.Add(PanelShowing(new PageData
            {
                FactoryId = LibraryPageFactory.StaticId,
                Context = new LibraryPageContext { LoadoutId = loadout.LoadoutId },
            }));
            return Task.CompletedTask;
        });
        await EventuallyOnUi(() => library.IsActive.Should().BeTrue(), delay: Poll);
        await EventuallyOnUi(() => library.Count.Should().Be(0), delay: Poll);
        await Task.Delay(300);
        await OnUi(() => { library.Count.Should().Be(0); return Task.CompletedTask; });

        // Something that arrives while the page is open is seen as it arrives.
        await AddLocalFile("arrived-while-open.7z", Destination.Of(game));
        await Task.Delay(300);
        await EventuallyOnUi(() => library.Count.Should().Be(0), delay: Poll);
    }
}
