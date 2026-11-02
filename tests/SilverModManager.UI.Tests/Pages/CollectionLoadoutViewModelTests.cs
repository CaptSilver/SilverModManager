using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Avalonia.Threading;
using DynamicData.Kernel;
using FluentAssertions;
using NSubstitute;
using ReactiveUI;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Games;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Pages.Collections;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.UI.Sdk.Icons;
using SilverModManager.UI.Tests.Pages.Collections;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// The collection page reads everything from a <see cref="CollectionSource"/>. The Nexus facts pin what the page did
/// before it was moved onto a source; the rest use a source the test drives.
/// </summary>
public class CollectionLoadoutViewModelTests(IServiceProvider provider) : AUiTest(provider)
{
    private IWindowManager _windowManager = Substitute.For<IWindowManager>();

    private async Task<(Loadout.ReadOnly Loadout, CollectionGroup.ReadOnly Group, TestCollectionSource Source)> TestSource()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Page source {Guid.NewGuid()}");
        var group = await CollectionSeed.AddLocalGroup(Connection, loadout.LoadoutId, "Page group");
        return (loadout, group, new TestCollectionSource(Provider, loadout.LoadoutId));
    }

    private async Task<CollectionLoadoutViewModel> Page(CollectionSource source) =>
        await Dispatcher.UIThread.InvokeAsync(() => new CollectionLoadoutViewModel(_windowManager, Provider, source));

    private async Task<(CollectionLoadoutViewModel Page, IDisposable Activation)> ActivePage(CollectionSource source)
    {
        var page = await Page(source);
        var activation = await Dispatcher.UIThread.InvokeAsync(() => page.Activator.Activate());
        return (page, activation);
    }

    private sealed record NexusFixture(Loadout.ReadOnly Loadout, NexusCollectionSource Source);

    private async Task<NexusFixture> NexusSource(string name = "Quality of Life")
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Nexus page {Guid.NewGuid()}");
        var revision = await CollectionSeed.AddRevision(Connection, loadout.InstallationInstance.Game.NexusModsGameId.Value, name, 7);
        var group = await CollectionSeed.AddInstalledGroup(Connection, loadout.LoadoutId, $"{name} (group)", revision);
        return new NexusFixture(loadout, NexusCollectionSource.Installed(Provider, loadout.LoadoutId, group));
    }

    [Fact]
    public async Task ANexusPageShowsTheSourcesHeaderAndOpensOnInstalled()
    {
        var f = await NexusSource("Header check");
        var (page, activation) = await ActivePage(f.Source);
        using var _ = activation;

        page.TabTitle.Should().Be("Header check (group)");
        page.Name.Should().Be("Header check (group)");
        page.VersionText.Should().Be("Revision 7");
        page.AuthorName.Should().Be("An author");
        page.Badge.Should().Be(f.Source.Badge);
        page.Link.Should().BeSameAs(f.Source.Link);
        page.Stats.Should().Be(f.Source.Stats);
        page.IsReadOnly.Should().BeTrue();
        page.TabIcon.Should().Be(IconValues.CollectionsOutline);
        page.SelectedTab.Should().Be(CollectionPageTab.Installed);
        await EventuallyOnUi(() =>
        {
            page.StateLine.Should().Be(Language.CollectionLoadoutView_CollectionInstalled);
            page.Adapter.Should().NotBeNull("a Nexus group is constant, so the tree exists from the start");
            page.HasInstalledTab.Should().BeTrue();
            page.IsToggleVisible.Should().BeTrue();
        });
    }

    [Fact]
    public async Task ANexusPagesMoreMenuKeepsTodaysOrderAndEndsWithUninstall()
    {
        var f = await NexusSource();
        var (page, activation) = await ActivePage(f.Source);
        using var _ = activation;

        await EventuallyOnUi(() => page.MenuItems.Select(item => item.Header).Should().Equal(
            Language.CollectionLoadoutView_MakeEditableLocalCopy,
            Language.CollectionLoadoutView_ViewCollectionDownloadMenuItem,
            Language.CollectionLoadoutView_ReinstallCollection,
            Language.CollectionLoadoutView_UninstallCollection));
        var uninstall = page.MenuItems[^1];
        uninstall.Icon.Should().Be(IconValues.PlaylistRemove);
        uninstall.ToolTip.Should().Be(Language.CollectionLoadoutView_UninstallCollection_ToolTip);
        page.MenuItems[1].Icon.Should().Be(IconValues.Downloading);
    }

    [Fact]
    public async Task TheEmbeddedDownloadsPageReceivesThePagesIds()
    {
        var f = await NexusSource();
        var page = await Page(f.Source);
        var windowId = WindowId.From(Guid.NewGuid());
        var workspaceId = WorkspaceId.NewId();
        var panelId = PanelId.NewId();
        var tabId = PanelTabId.From(Guid.NewGuid());
        page.WindowId = windowId;
        page.WorkspaceId = workspaceId;
        page.PanelId = panelId;
        page.TabId = tabId;

        using var activation = await Dispatcher.UIThread.InvokeAsync(() => page.Activator.Activate());

        page.Downloads.Should().NotBeNull();
        page.Downloads!.WindowId.Should().Be(windowId);
        page.Downloads.WorkspaceId.Should().Be(workspaceId);
        page.Downloads.PanelId.Should().Be(panelId);
        page.Downloads.TabId.Should().Be(tabId);
    }

    [Fact]
    public async Task TheHeaderShowsTheSourcesInstallProgressOnEveryTabAndClearsItWhenTheInstallEnds()
    {
        var (_, group, source) = await TestSource();
        source.GroupSubject.OnNext(Optional<CollectionGroupId>.Create(group.CollectionGroupId));
        var (page, activation) = await ActivePage(source);
        using var _ = activation;
        await EventuallyOnUi(() => page.InstallProgress.Should().BeNull("no install is running"));

        source.InstallProgressSubject.OnNext(new CollectionInstallStatus("Writing files: 4 of 8", 0.5));

        foreach (var tab in new[] { CollectionPageTab.Installed, CollectionPageTab.Instructions, CollectionPageTab.Downloads })
        {
            await Dispatcher.UIThread.InvokeAsync(() => page.SelectedTab = tab);
            await EventuallyOnUi(() => page.InstallProgress.Should().Be(new CollectionInstallStatus("Writing files: 4 of 8", 0.5), $"the {tab} tab shows it too"));
        }

        source.InstallProgressSubject.OnNext(null);
        await EventuallyOnUi(() => page.InstallProgress.Should().BeNull());
    }

    [Fact]
    public async Task APageWhoseSourceHasNoDownloadsHasNoViewDownloadsItem()
    {
        var (_, group, source) = await TestSource();
        source.GroupSubject.OnNext(Optional<CollectionGroupId>.Create(group.CollectionGroupId));
        var (page, activation) = await ActivePage(source);
        using var _ = activation;

        page.Downloads.Should().BeNull();
        source.ViewDownloadsSeen.Should().BeNull();
        await EventuallyOnUi(() => page.MenuItems.Select(item => item.Header).Should().Equal("Source item", "Remove test collection"));
    }

    [Fact]
    public async Task APageWithDownloadsPassesItsViewDownloadsItemToTheSourceAndShowsDownloadsCommandSwitchesTab()
    {
        var (_, group, source) = await TestSource();
        source.GroupSubject.OnNext(Optional<CollectionGroupId>.Create(group.CollectionGroupId));
        source.DownloadsToReturn = Substitute.For<IPageViewModelInterface>();
        var (page, activation) = await ActivePage(source);
        using var _ = activation;

        source.ViewDownloadsSeen.Should().NotBeNull();
        source.ViewDownloadsSeen!.Header.Should().Be(Language.CollectionLoadoutView_ViewCollectionDownloadMenuItem);
        source.ViewDownloadsSeen.Icon.Should().Be(IconValues.Downloading);
        page.Downloads.Should().BeSameAs(source.DownloadsToReturn);

        page.SelectedTab = CollectionPageTab.Installed;
        await Dispatcher.UIThread.InvokeAsync(() => source.ViewDownloadsSeen.Command.Execute().Subscribe());
        await EventuallyOnUi(() => page.SelectedTab.Should().Be(CollectionPageTab.Downloads));
    }

    [Fact]
    public async Task TheRemovalItemRemovesTheCurrentGroupWithoutAskingFirst()
    {
        var (_, group, source) = await TestSource();
        source.GroupSubject.OnNext(Optional<CollectionGroupId>.Create(group.CollectionGroupId));
        var (page, activation) = await ActivePage(source);
        using var _ = activation;
        await EventuallyOnUi(() => page.MenuItems.Should().HaveCount(2));
        var removal = page.MenuItems[^1];

        removal.Header.Should().Be("Remove test collection");
        removal.Styling.Should().Be(ContextMenuItemStyling.Default);
        removal.ToolTip.Should().Be("Removes the test collection");
        var controller = Substitute.For<IWorkspaceController>();
        _windowManager.TryGetWindow(Arg.Any<WindowId>(), out Arg.Any<IWorkspaceWindow?>()).Returns(call =>
        {
            var window = Substitute.For<IWorkspaceWindow>();
            window.WorkspaceController.Returns(controller);
            call[1] = window;
            return true;
        });
        page.WindowId = WindowId.From(Guid.NewGuid());
        await Dispatcher.UIThread.InvokeAsync(() => removal.Command.Execute().Subscribe());

        await EventuallyOnUi(() => source.RemoveCalls.Should().Be(1));
    }

    [Fact]
    public async Task APageWithNoGroupHasNoInstalledTabAndOpensOnDownloads()
    {
        var (_, _, source) = await TestSource();
        source.DownloadsToReturn = Substitute.For<IPageViewModelInterface>();
        var (page, activation) = await ActivePage(source);
        using var _ = activation;

        await EventuallyOnUi(() =>
        {
            page.HasInstalledTab.Should().BeFalse();
            page.Adapter.Should().BeNull();
        });
        page.SelectedTab.Should().Be(CollectionPageTab.Downloads);
    }

    [Fact]
    public async Task AGroupThatIsNotInstalledOffersInstalledButOpensOnDownloads()
    {
        var (_, group, source) = await TestSource();
        source.GroupSubject.OnNext(Optional<CollectionGroupId>.Create(group.CollectionGroupId));
        source.InstalledSubject.OnNext(false);
        source.DownloadsToReturn = Substitute.For<IPageViewModelInterface>();
        var (page, activation) = await ActivePage(source);
        using var _ = activation;

        await EventuallyOnUi(() => page.HasInstalledTab.Should().BeTrue("the half-written group is shown"));
        page.SelectedTab.Should().Be(CollectionPageTab.Downloads);
    }

    [Fact]
    public async Task TheTreeAppearsWhenTheGroupDoes()
    {
        var (_, group, source) = await TestSource();
        var (page, activation) = await ActivePage(source);
        using var _ = activation;
        await EventuallyOnUi(() => page.Adapter.Should().BeNull());

        source.GroupSubject.OnNext(Optional<CollectionGroupId>.Create(group.CollectionGroupId));

        await EventuallyOnUi(() =>
        {
            page.Adapter.Should().NotBeNull();
            page.HasInstalledTab.Should().BeTrue();
        });
    }

    private async Task SeedModIn(Loadout.ReadOnly loadout, CollectionGroup.ReadOnly group, string name)
    {
        using var tx = Connection.BeginTransaction();
        _ = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id) { Name = name, LoadoutId = loadout.LoadoutId },
        };
        tx.Add(id, LoadoutItem.Parent, group.Id);
        await tx.Commit();
    }

    [Fact]
    public async Task TheTreeFollowsTheGroupOfTheModsItHolds()
    {
        var (loadout, group, source) = await TestSource();
        await SeedModIn(loadout, group, "Tracked mod");
        source.GroupSubject.OnNext(Optional<CollectionGroupId>.Create(group.CollectionGroupId));
        var (page, activation) = await ActivePage(source);
        using var _ = activation;

        await EventuallyOnUi(() => page.InstalledModsCount.Should().Be(1));
    }

    [Fact]
    public async Task WhenTheGroupGoesAwayWhileInstalledIsShownThePageMovesToDownloads()
    {
        var (_, group, source) = await TestSource();
        source.GroupSubject.OnNext(Optional<CollectionGroupId>.Create(group.CollectionGroupId));
        source.DownloadsToReturn = Substitute.For<IPageViewModelInterface>();
        var (page, activation) = await ActivePage(source);
        using var _ = activation;
        await EventuallyOnUi(() => page.SelectedTab.Should().Be(CollectionPageTab.Installed));

        source.GroupSubject.OnNext(Optional<CollectionGroupId>.None);

        await EventuallyOnUi(() =>
        {
            page.HasInstalledTab.Should().BeFalse();
            page.Adapter.Should().BeNull();
            page.SelectedTab.Should().Be(CollectionPageTab.Downloads, "a hidden tab must not stay selected");
        });
    }

    [Fact]
    public async Task ASourceThatEmitsOnAPoolThreadStillUpdatesThePageOnTheUiThread()
    {
        var (_, group, source) = await TestSource();
        var (page, activation) = await ActivePage(source);
        using var _ = activation;
        var threads = new System.Collections.Concurrent.ConcurrentQueue<(string Property, bool OnUi)>();
        using var subscriptions = new System.Reactive.Disposables.CompositeDisposable(
            page.WhenAnyValue(x => x.Name).Skip(1).Subscribe(_ => threads.Enqueue(("Name", Dispatcher.UIThread.CheckAccess()))),
            page.WhenAnyValue(x => x.StateLine).Skip(1).Subscribe(_ => threads.Enqueue(("StateLine", Dispatcher.UIThread.CheckAccess()))),
            page.WhenAnyValue(x => x.Adapter).Skip(1).Subscribe(_ => threads.Enqueue(("Adapter", Dispatcher.UIThread.CheckAccess()))));

        await Task.Run(() =>
        {
            source.NameSubject.OnNext("From a pool thread");
            source.StateLineSubject.OnNext("Pool state");
            source.GroupSubject.OnNext(Optional<CollectionGroupId>.Create(group.CollectionGroupId));
        });

        await EventuallyOnUi(() =>
        {
            page.Name.Should().Be("From a pool thread");
            page.StateLine.Should().Be("Pool state");
            page.Adapter.Should().NotBeNull();
            // The change notifications are part of what is waited for: the values can be readable before the last one is raised.
            threads.Select(t => t.Property).Should().Contain(["Name", "StateLine", "Adapter"]);
        });
        threads.Should().OnlyContain(t => t.OnUi, "bound state is only touched on the UI thread");
    }

    [Fact]
    public async Task TheTogglePanelFollowsTheSourcesCanToggle()
    {
        var (_, _, source) = await TestSource();
        var (page, activation) = await ActivePage(source);
        using var _ = activation;
        await EventuallyOnUi(() => page.IsToggleVisible.Should().BeFalse());

        source.CanToggleSubject.OnNext(true);

        await EventuallyOnUi(() => page.IsToggleVisible.Should().BeTrue());
    }

    [Fact]
    public async Task TheTogglePanelShowsTheDescriptionTheSourceGives()
    {
        var (_, _, source) = await TestSource();
        var (page, activation) = await ActivePage(source);
        using var _ = activation;

        source.DescriptionSubject.OnNext("A long story.");

        await EventuallyOnUi(() => page.Description.Should().Be("A long story."));
    }

    [Fact]
    public async Task CreateDownloadsAndActivateRunOnceEachAndTheActivationEndsWithThePage()
    {
        var (_, group, source) = await TestSource();
        source.GroupSubject.OnNext(Optional<CollectionGroupId>.Create(group.CollectionGroupId));
        source.DownloadsToReturn = Substitute.For<IPageViewModelInterface>();
        var page = await Page(source);

        source.CreateDownloadsCalls.Should().Be(1, "the page builds its Downloads tab in its constructor");
        source.ActivateCalls.Should().Be(0);

        var activation = await Dispatcher.UIThread.InvokeAsync(() => page.Activator.Activate());
        await EventuallyOnUi(() => source.ActivateCalls.Should().Be(1));
        source.CreateDownloadsCalls.Should().Be(1);

        activation.Dispose();
        source.DisposedActivations.Should().Be(1);
    }

    [Fact]
    public async Task TheFactoryBuildsALocalCollectionsPageFromItsProvidersSource()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Factory local {Guid.NewGuid()}");
        var group = await CollectionSeed.AddLocalGroup(Connection, loadout.LoadoutId, "Factory list");
        var factory = new CollectionLoadoutPageFactory(Provider);

        var page = await Dispatcher.UIThread.InvokeAsync(() =>
            factory.CreateViewModel(new CollectionLoadoutPageContext { LoadoutId = loadout.LoadoutId, GroupId = group.CollectionGroupId }));

        page.Name.Should().Be("Factory list");
        page.TabTitle.Should().Be("Factory list");
    }

    private async Task<(Loadout.ReadOnly Loadout, WabbajackModlistSource Source, EntityId File)> ModlistSource(string name)
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Modlist page {Guid.NewGuid()}");
        var file = await CollectionSeed.AddModlistFile(Connection, $"{name} {Guid.NewGuid():N}", version: "2.0", archiveCount: 10);
        return (loadout, new WabbajackModlistSource(Provider, loadout.LoadoutId, file), file.Id);
    }

    [Fact]
    public async Task AModlistThatIsNotInstalledShowsOnlyDownloadsAndSaysSo()
    {
        var (_, source, _) = await ModlistSource("Not installed");
        var (page, activation) = await ActivePage(source);
        using var _ = activation;

        page.HasInstalledTab.Should().BeFalse();
        page.SelectedTab.Should().Be(CollectionPageTab.Downloads);
        page.Downloads.Should().NotBeNull();
        page.VersionText.Should().Be("Version 2.0");
        await EventuallyOnUi(() =>
        {
            page.StateLine.Should().Be("Not installed yet");
            page.Adapter.Should().BeNull();
            page.IsToggleVisible.Should().BeFalse();
        });
    }

    [Fact]
    public async Task AModlistWhoseInstallDidNotFinishOffersInstalledButOpensOnDownloads()
    {
        var (loadout, source, file) = await ModlistSource("Half installed");
        await WabbajackGroupSeed.AddGroup(Connection, file, loadout.LoadoutId, "Half installed");
        var (page, activation) = await ActivePage(source);
        using var _ = activation;

        page.SelectedTab.Should().Be(CollectionPageTab.Downloads, "Resume is on the Downloads tab");
        await EventuallyOnUi(() =>
        {
            page.HasInstalledTab.Should().BeTrue();
            page.StateLine.Should().Be("Install not finished");
            page.Adapter.Should().NotBeNull();
            page.IsToggleVisible.Should().BeFalse();
        });
    }

    [Fact]
    public async Task AnInstalledModlistOpensOnInstalledWithItsToggle()
    {
        var (loadout, source, file) = await ModlistSource("Installed list");
        var group = await WabbajackGroupSeed.AddGroup(Connection, file, loadout.LoadoutId, "Installed list");
        await WabbajackGroupSeed.Finish(Connection, group);
        var (page, activation) = await ActivePage(source);
        using var _ = activation;

        page.SelectedTab.Should().Be(CollectionPageTab.Installed);
        await EventuallyOnUi(() =>
        {
            page.HasInstalledTab.Should().BeTrue();
            page.StateLine.Should().Be(Language.CollectionLoadoutView_CollectionInstalled);
            page.IsToggleVisible.Should().BeTrue();
            page.Adapter.Should().NotBeNull();
        });
    }

    [Fact]
    public async Task AModlistWhoseInstallIsRemovedFromItsPageFallsBackToDownloads()
    {
        var (loadout, source, file) = await ModlistSource("Removed list");
        var group = await WabbajackGroupSeed.AddGroup(Connection, file, loadout.LoadoutId, "Removed list");
        await WabbajackGroupSeed.Finish(Connection, group);
        var (page, activation) = await ActivePage(source);
        using var _ = activation;
        await EventuallyOnUi(() => page.Adapter.Should().NotBeNull());

        await WabbajackGroupSeed.Delete(Connection, group);

        await EventuallyOnUi(() =>
        {
            page.HasInstalledTab.Should().BeFalse();
            page.SelectedTab.Should().Be(CollectionPageTab.Downloads);
            page.Adapter.Should().BeNull();
            page.StateLine.Should().Be("Not installed yet");
        });
    }

    [Fact]
    public async Task AModlistPagesMoreMenuHasTheDownloadsItemsAndEndsWithReinstallThenRemoveWhileInstalled()
    {
        var (loadout, source, file) = await ModlistSource("Menu list");
        var group = await WabbajackGroupSeed.AddGroup(Connection, file, loadout.LoadoutId, "Menu list");
        await WabbajackGroupSeed.Finish(Connection, group);
        var (page, activation) = await ActivePage(source);
        using var _ = activation;

        await EventuallyOnUi(() => page.MenuItems.Select(item => item.Header).Should().Equal(
            Language.CollectionLoadoutView_ViewCollectionDownloadMenuItem,
            "Check inbox",
            "Open inbox folder",
            "Choose download folder...",
            "Use the default download folder",
            "Reinstall modlist",
            "Remove modlist"));
    }
}
