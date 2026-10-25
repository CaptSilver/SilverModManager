using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DynamicData.Kernel;
using FluentAssertions;
using NSubstitute;
using System.Reactive.Linq;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.App.UI.LeftMenu.Loadout;
using SilverModManager.App.UI.Pages.NexusCatalog;
using SilverModManager.App.UI.Pages.Thunderstore;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.UI.Tests.LeftMenu;

public class StoreSectionTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly LoadoutId SomeLoadout = LoadoutId.From(EntityId.From(0xBEEF));

    /// <summary>Listed on Nexus Mods and a Thunderstore community at once.</summary>
    private sealed class NexusAndThunderstoreGame(IServiceProvider provider) : ThunderstoreStubbedGame(provider), IGameData
    {
        Optional<NexusModsGameId> IGameData.NexusModsGameId => SilverModManager.Sdk.NexusModsApi.NexusModsGameId.From(4242);
    }

    private static IWorkspaceController Controller(WorkspaceId id)
    {
        var workspace = Substitute.For<IWorkspaceViewModel>();
        workspace.Id.Returns(id);
        workspace.Panels.Returns(new ReadOnlyObservableCollection<IPanelViewModel>([]));
        var controller = Substitute.For<IWorkspaceController>();
        controller.ActiveWorkspace.Returns(workspace);
        return controller;
    }

    private IReadOnlyList<ILeftMenuItemViewModel> StoresOf(IGameData game, IWorkspaceController controller)
        => StoreLeftMenuItems.For(game, SomeLoadout, controller, WorkspaceId.From(Guid.NewGuid()));

    private static string[] Names(IEnumerable<ILeftMenuItemViewModel> items) => items.Select(item => item.Text.Value.Value).ToArray();

    [Fact]
    public async Task AGameOnBothStores_ListsNexusModsThenThunderstore()
    {
        var stores = await OnUi(() => Task.FromResult(StoresOf(new NexusAndThunderstoreGame(Provider), Controller(WorkspaceId.From(Guid.NewGuid())))));

        Names(stores).Should().Equal("Nexus Mods", "Thunderstore");
    }

    [Fact]
    public async Task AGameOnlyOnNexusMods_ListsOnlyNexusMods()
    {
        var game = new IdentifiedStubbedGame(GameId.From("OnNexus"), NexusModsGameId.From(4242));
        var stores = await OnUi(() => Task.FromResult(StoresOf(game, Controller(WorkspaceId.From(Guid.NewGuid())))));

        Names(stores).Should().Equal("Nexus Mods");
    }

    [Fact]
    public async Task AGameOnNoStore_ListsNone()
    {
        var stores = await OnUi(() => Task.FromResult(StoresOf(new NoNexusIdStubbedGame(Provider), Controller(WorkspaceId.From(Guid.NewGuid())))));

        stores.Should().BeEmpty();
    }

    [Fact]
    public async Task SelectingAStoreEntry_OpensItsPage()
    {
        var workspaceId = WorkspaceId.From(Guid.NewGuid());
        var controller = Controller(workspaceId);
        var stores = await OnUi(() => Task.FromResult(StoreLeftMenuItems.For(new NexusAndThunderstoreGame(Provider), SomeLoadout, controller, workspaceId)));

        await OnUi(async () =>
        {
            await stores[0].NavigateCommand.Execute(NavigationInformation.From(OpenPageBehaviorType.ReplaceTab)).FirstOrDefaultAsync();
            await stores[1].NavigateCommand.Execute(NavigationInformation.From(OpenPageBehaviorType.ReplaceTab)).FirstOrDefaultAsync();
        });

        controller.Received(1).OpenPage(workspaceId,
            Arg.Is<Optional<PageData>>(page => page.Value.FactoryId == NexusModsPageFactory.StaticId),
            Arg.Any<OpenPageBehavior>(), Arg.Any<bool>(), Arg.Any<bool>());
        controller.Received(1).OpenPage(workspaceId,
            Arg.Is<Optional<PageData>>(page => page.Value.FactoryId == ThunderstorePageFactory.StaticId),
            Arg.Any<OpenPageBehavior>(), Arg.Any<bool>(), Arg.Any<bool>());
    }

    private sealed class MenuStub(params ILeftMenuItemViewModel[] stores) : AViewModel<ILoadoutLeftMenuViewModel>, ILoadoutLeftMenuViewModel
    {
        private static ILeftMenuItemViewModel Item(string text) => new LeftMenuItemDesignViewModel { Text = new StringComponent(text), Icon = IconValues.Download };

        public ReadOnlyObservableCollection<ILeftMenuItemViewModel> LeftMenuCollectionItems { get; } = new([]);
        public bool HasSingleCollection => true;
        public WorkspaceId WorkspaceId { get; } = WorkspaceId.From(Guid.NewGuid());
        public IApplyControlViewModel ApplyControlViewModel { get; } = new ApplyControlDesignViewModel();
        public ILeftMenuItemViewModel LeftMenuItemLibrary { get; } = Item("Library item");
        public ILeftMenuItemViewModel LeftMenuItemLoadout { get; } = Item("My Mods item");
        public ReadOnlyObservableCollection<ILeftMenuItemViewModel> LeftMenuStoreItems { get; } = new(new ObservableCollection<ILeftMenuItemViewModel>(stores));
        public ILeftMenuItemViewModel LeftMenuItemNewCollection { get; } = Item("New collection item");
        public ILeftMenuItemViewModel LeftMenuItemHealthCheck { get; } = Item("Health item");
        public ILeftMenuItemViewModel LeftMenuItemTools { get; } = Item("Tools item");
        public ILeftMenuItemViewModel? LeftMenuItemExternalChanges => null;
    }

    private static ILeftMenuItemViewModel StoreItem(string text) => new LeftMenuItemDesignViewModel { Text = new StringComponent(text), Icon = IconValues.Download };

    private async Task<LoadoutLeftMenuView> Show(params ILeftMenuItemViewModel[] stores)
    {
        return await OnUi(async () =>
        {
            var view = new LoadoutLeftMenuView { ViewModel = new MenuStub(stores) };
            new Window { Width = 300, Height = 900, Content = view }.Show();
            Dispatcher.UIThread.RunJobs();
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
            return view;
        });
    }

    private static double Top(Visual of, Visual inView) => of.TranslatePoint(new Point(0, 0), inView)!.Value.Y;

    private static Control Named(LoadoutLeftMenuView view, string name) => view.GetVisualDescendants().OfType<Control>().First(c => c.Name == name);

    private static TextBlock Header(LoadoutLeftMenuView view, string text) => view.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == text);

    [Fact]
    public async Task TheStoresHeader_SitsBetweenLibraryAndInstalled_WithItsEntriesInOrder()
    {
        var view = await Show(StoreItem("Nexus Mods"), StoreItem("Thunderstore"));

        await OnUi(() =>
        {
            var stores = Header(view, "Stores");
            stores.IsEffectivelyVisible.Should().BeTrue();
            var library = Named(view, "LibraryItem");
            var installed = Header(view, SilverModManager.App.UI.Resources.Language.LeftMenu_Label_Installed_Mods);
            var nexus = Header(view, "Nexus Mods");
            var thunderstore = Header(view, "Thunderstore");

            Top(library, view).Should().BeLessThan(Top(stores, view));
            Top(stores, view).Should().BeLessThan(Top(nexus, view));
            Top(nexus, view).Should().BeLessThan(Top(thunderstore, view));
            Top(thunderstore, view).Should().BeLessThan(Top(installed, view));
        });
    }

    [Fact]
    public async Task AGameWithoutStores_ShowsNoStoresHeader()
    {
        var view = await Show();

        await OnUi(() =>
        {
            Named(view, "StoresSection").IsVisible.Should().BeFalse();
            Header(view, "Stores").IsEffectivelyVisible.Should().BeFalse();
        });
    }

    [Fact]
    public async Task AGameWithOneStore_ShowsTheHeaderOverThatEntryOnly()
    {
        var view = await Show(StoreItem("Nexus Mods"));

        await OnUi(() =>
        {
            Header(view, "Stores").IsEffectivelyVisible.Should().BeTrue();
            view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text == "Thunderstore").Should().BeEmpty();
        });
    }
}
