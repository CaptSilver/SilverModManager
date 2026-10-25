using System.Collections.Immutable;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using DynamicData;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Helpers;
using SilverModManager.Collections;
using System.Reactive.Linq;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.App.UI.LeftMenu.Loadout;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Settings;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Settings;
using SilverModManager.UI.Sdk;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.LeftMenu;

/// <summary>
/// The order of a loadout's collections in the left menu: the oldest editable collection first, the rest in the
/// order the user left them in, collections nobody placed yet last, and entries that are not collections after those.
/// </summary>
public class LoadoutCollectionOrderTests(IServiceProvider provider) : AUiTest(provider)
{
    private static int _extraKeys;

    private ISettingsManager Settings => Provider.GetRequiredService<ISettingsManager>();

    private async Task<CollectionGroupId> AddCollection(Loadout.ReadOnly loadout, string name, bool readOnly = false)
    {
        using var tx = Connection.BeginTransaction();
        _ = new CollectionGroup.New(tx, out var id)
        {
            IsReadOnly = readOnly,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id) { Name = name, LoadoutId = loadout.LoadoutId },
            },
        };
        var result = await tx.Commit();
        return CollectionGroupId.From(result[id]);
    }

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

    /// <summary>A menu list over <paramref name="loadout"/>'s collections, with <paramref name="extra"/> entries mixed in.</summary>
    private sealed class OpenList(LoadoutCollectionList list, IDisposable binding, SourceCache<ILeftMenuItemViewModel, EntityId> extra) : IDisposable
    {
        public LoadoutCollectionList List => list;
        public SourceCache<ILeftMenuItemViewModel, EntityId> Extra => extra;
        public void Dispose() => Dispatcher.UIThread.Invoke(binding.Dispose);
    }

    private Task<OpenList> Open(Loadout.ReadOnly loadout) => OnUi(() =>
    {
        var list = new LoadoutCollectionList(Settings, loadout.LoadoutId);
        var extra = new SourceCache<ILeftMenuItemViewModel, EntityId>(_ => EntityId.From(0x00FF_0000_0000UL + (ulong)Interlocked.Increment(ref _extraKeys)));
        var source = LoadoutLeftMenuViewModel.ObserveCollectionItems(
                Connection,
                Provider.GetRequiredService<CollectionDownloader>(),
                loadout,
                Controller(),
                WorkspaceId.From(Guid.NewGuid()),
                Provider,
                list.MoveBy)
            .MergeChangeSets(extra.Connect());
        return Task.FromResult(new OpenList(list, list.Bind(source), extra));
    });

    private static string[] Names(OpenList open) => open.List.Items.Select(item => item.Text.Value.Value).ToArray();

    private Task ShowsEventually(OpenList open, params string[] names) =>
        EventuallyOnUi(() => Names(open).Should().Equal(names), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(50));

    private void SaveOrder(Loadout.ReadOnly loadout, params CollectionGroupId[] order) =>
        Settings.Update<LoadoutCollectionOrderSettings>(settings => settings with
        {
            Orders = settings.Orders.SetItem(LoadoutCollectionOrder.KeyOf(loadout.LoadoutId), order.Select(id => id.Value.Value).ToImmutableList()),
        });

    private ImmutableList<ulong> SavedOrder(Loadout.ReadOnly loadout) =>
        Settings.Get<LoadoutCollectionOrderSettings>().Orders.GetValueOrDefault(LoadoutCollectionOrder.KeyOf(loadout.LoadoutId)) ?? ImmutableList<ulong>.Empty;

    private static LeftMenuItemDesignViewModel NotACollection(string name) => new() { Text = new StringComponent(name) };

    [Fact]
    public async Task WithoutASavedOrder_TheOldestEditableCollectionComesFirstAndReadOnlyOnesLast()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "order default");
        await AddCollection(loadout, "My Mods");
        await AddCollection(loadout, "Nexus one", readOnly: true);
        await AddCollection(loadout, "Second");

        using var open = await Open(loadout);

        await ShowsEventually(open, "My Mods", "Second", "Nexus one");
    }

    [Fact]
    public async Task ASavedOrderDecidesPosition_NewCollectionsGoLast_AndTheOldestEditableOneStaysFirst()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "order saved");
        var mods = await AddCollection(loadout, "My Mods");
        var a = await AddCollection(loadout, "A");
        var b = await AddCollection(loadout, "B");
        await AddCollection(loadout, "C");
        // C is not listed; My Mods is listed last but is not movable.
        SaveOrder(loadout, b, a, mods);

        using var open = await Open(loadout);

        await ShowsEventually(open, "My Mods", "B", "A", "C");
    }

    [Fact]
    public async Task ACollectionAddedLater_JoinsTheEndOfTheSavedOrder()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "order added");
        await AddCollection(loadout, "My Mods");
        var a = await AddCollection(loadout, "A");
        var b = await AddCollection(loadout, "B");
        SaveOrder(loadout, b, a);
        using var open = await Open(loadout);
        await ShowsEventually(open, "My Mods", "B", "A");

        await AddCollection(loadout, "Late");

        await ShowsEventually(open, "My Mods", "B", "A", "Late");
    }

    [Fact]
    public async Task EntriesThatAreNotCollectionsStayAfterTheCollections()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "order extra");
        await AddCollection(loadout, "My Mods");
        var a = await AddCollection(loadout, "A");
        var b = await AddCollection(loadout, "B");
        SaveOrder(loadout, b, a);
        using var open = await Open(loadout);
        await OnUi(() => open.Extra.AddOrUpdate(NotACollection("Downloading one")));

        await ShowsEventually(open, "My Mods", "B", "A", "Downloading one");
    }

    [Fact]
    public async Task MovingACollection_SavesTheOrder_AndANewListRestoresIt()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "order move");
        await AddCollection(loadout, "My Mods");
        var a = await AddCollection(loadout, "A");
        var b = await AddCollection(loadout, "B");
        var c = await AddCollection(loadout, "C");
        SaveOrder(loadout, a, b, c);

        using (var open = await Open(loadout))
        {
            await ShowsEventually(open, "My Mods", "A", "B", "C");
            await OnUi(() => open.List.Move(1, 3));
            await ShowsEventually(open, "My Mods", "B", "C", "A");
        }

        SavedOrder(loadout).Should().Equal(b.Value.Value, c.Value.Value, a.Value.Value);
        using var reopened = await Open(loadout);
        await ShowsEventually(reopened, "My Mods", "B", "C", "A");
    }

    [Fact]
    public async Task MovingACollection_MovesItsEntryInTheBoundListInsteadOfRebuildingIt()
    {
        // A Reset makes the view throw every item away and recreate it, which shows as the menu blinking after a drop.
        var loadout = await SeedLoadoutRow(GameStore.Steam, "order no reset");
        await AddCollection(loadout, "My Mods");
        var a = await AddCollection(loadout, "A");
        var b = await AddCollection(loadout, "B");
        var c = await AddCollection(loadout, "C");
        SaveOrder(loadout, a, b, c);
        using var open = await Open(loadout);
        await ShowsEventually(open, "My Mods", "A", "B", "C");
        var actions = new List<NotifyCollectionChangedAction>();
        NotifyCollectionChangedEventHandler record = (_, e) => actions.Add(e.Action);
        await OnUi(() => ((INotifyCollectionChanged)open.List.Items).CollectionChanged += record);

        await OnUi(() => open.List.Move(1, 3));
        await ShowsEventually(open, "My Mods", "B", "C", "A");
        await OnUi(() => ((INotifyCollectionChanged)open.List.Items).CollectionChanged -= record);

        actions.Should().NotBeEmpty().And.NotContain(NotifyCollectionChangedAction.Reset);
    }

    [Fact]
    public async Task AnOrderSavedElsewhere_ReordersTheLiveList()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "order live");
        await AddCollection(loadout, "My Mods");
        var a = await AddCollection(loadout, "A");
        var b = await AddCollection(loadout, "B");
        using var open = await Open(loadout);
        await ShowsEventually(open, "My Mods", "A", "B");

        SaveOrder(loadout, b, a);

        await ShowsEventually(open, "My Mods", "B", "A");
    }

    [Fact]
    public async Task IdsOfDeletedCollectionsAreIgnored_AndPrunedOnTheNextSave()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "order pruned");
        await AddCollection(loadout, "My Mods");
        var a = await AddCollection(loadout, "A");
        var b = await AddCollection(loadout, "B");
        var gone = CollectionGroupId.From(EntityId.From(0xDEAD));
        SaveOrder(loadout, b, gone, a);
        using var open = await Open(loadout);
        await ShowsEventually(open, "My Mods", "B", "A");

        await OnUi(() => open.List.Move(1, 2));

        SavedOrder(loadout).Should().Equal(a.Value.Value, b.Value.Value);
    }

    [Fact]
    public async Task EachLoadoutKeepsItsOwnOrder()
    {
        var first = await SeedLoadoutRow(GameStore.Steam, "order own 1");
        await AddCollection(first, "My Mods");
        var a1 = await AddCollection(first, "A");
        var b1 = await AddCollection(first, "B");
        var second = await SeedLoadoutRow(GameStore.Steam, "order own 2");
        await AddCollection(second, "My Mods");
        await AddCollection(second, "A");
        await AddCollection(second, "B");
        SaveOrder(first, b1, a1);

        using var firstOpen = await Open(first);
        using var secondOpen = await Open(second);

        await ShowsEventually(firstOpen, "My Mods", "B", "A");
        await ShowsEventually(secondOpen, "My Mods", "A", "B");
    }

    [Fact]
    public async Task OnlyCollectionsBesidesTheFirstEditableOneCanMove()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "order movable");
        await AddCollection(loadout, "My Mods");
        await AddCollection(loadout, "A");
        await AddCollection(loadout, "B");
        using var open = await Open(loadout);
        await OnUi(() => open.Extra.AddOrUpdate(NotACollection("Downloading one")));
        await ShowsEventually(open, "My Mods", "A", "B", "Downloading one");

        await OnUi(() =>
        {
            open.List.IsMovable(0).Should().BeFalse("My Mods keeps its place");
            open.List.IsMovable(1).Should().BeTrue();
            open.List.IsMovable(2).Should().BeTrue();
            open.List.IsMovable(3).Should().BeFalse("it is not a collection");
            open.List.IsMovable(4).Should().BeFalse("there is nothing there");
            ((Action)(() => open.List.Move(0, 2))).Should().Throw<ArgumentException>();
            ((Action)(() => open.List.Move(1, 3))).Should().Throw<ArgumentException>();
        });
        await ShowsEventually(open, "My Mods", "A", "B", "Downloading one");
    }

    private static async Task Run(CollectionLeftMenuItemViewModel item, string header)
    {
        var entry = item.AdditionalContextMenuItems.Single(menuItem => menuItem.Header == header);
        await entry.Command.Execute().FirstOrDefaultAsync();
    }

    [Fact]
    public async Task MoveUpAndMoveDownInTheContextMenu_StepACollectionOneSlot()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "order menu");
        await AddCollection(loadout, "My Mods");
        await AddCollection(loadout, "A");
        await AddCollection(loadout, "B");
        await AddCollection(loadout, "C");
        using var open = await Open(loadout);
        await ShowsEventually(open, "My Mods", "A", "B", "C");
        CollectionLeftMenuItemViewModel Item(string name) => open.List.Items.OfType<CollectionLeftMenuItemViewModel>().Single(i => i.Text.Value.Value == name);

        await OnUi(() => Run(Item("C"), Language.SpineLoadoutMenu_MoveUp));
        await ShowsEventually(open, "My Mods", "A", "C", "B");

        await OnUi(() => Run(Item("A"), Language.SpineLoadoutMenu_MoveDown));
        await ShowsEventually(open, "My Mods", "C", "A", "B");
    }

    [Fact]
    public async Task MoveUpOnTheFirstMovableCollection_AndMoveDownOnTheLast_DoNothing()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "order menu ends");
        await AddCollection(loadout, "My Mods");
        await AddCollection(loadout, "A");
        await AddCollection(loadout, "B");
        using var open = await Open(loadout);
        await OnUi(() => open.Extra.AddOrUpdate(NotACollection("Downloading one")));
        await ShowsEventually(open, "My Mods", "A", "B", "Downloading one");
        CollectionLeftMenuItemViewModel Item(string name) => open.List.Items.OfType<CollectionLeftMenuItemViewModel>().Single(i => i.Text.Value.Value == name);

        await OnUi(() => Run(Item("A"), Language.SpineLoadoutMenu_MoveUp));
        await OnUi(() => Run(Item("B"), Language.SpineLoadoutMenu_MoveDown));

        await ShowsEventually(open, "My Mods", "A", "B", "Downloading one");
    }
}
