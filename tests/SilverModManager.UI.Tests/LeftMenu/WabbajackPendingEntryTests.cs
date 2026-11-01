using System.Collections.ObjectModel;
using DynamicData;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Collections;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.App.UI.LeftMenu.Loadout;
using SilverModManager.App.UI.Pages.Collections;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Tests.Pages;
using SilverModManager.UI.Tests.Pages.Collections;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.LeftMenu;

/// <summary>
/// A modlist in the library is offered in the menu of every loadout of its game until it is installed there, and then
/// becomes the toggle entry of that loadout only.
/// </summary>
public class WabbajackPendingEntryTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly IGameData SkyrimSe = new TestGame(GameId.From("CreationEngine.SkyrimSE"), SilverModManager.Sdk.NexusModsApi.NexusModsGameId.From(1704));
    private static readonly IGameData SkyrimVr = new TestGame(GameId.From("CreationEngine.SkyrimVR"), SilverModManager.Sdk.NexusModsApi.NexusModsGameId.From(1704));

    private static IWorkspaceController Controller()
    {
        var workspace = Substitute.For<IWorkspaceViewModel>();
        workspace.Id.Returns(WorkspaceId.From(Guid.NewGuid()));
        workspace.Panels.Returns(new ReadOnlyObservableCollection<IPanelViewModel>([]));
        var controller = Substitute.For<IWorkspaceController>();
        controller.ActiveWorkspace.Returns(workspace);
        return controller;
    }

    // Only the Wabbajack provider: the Nexus one reads the loadout's installed game, which a loadout row on a folder that does not exist has none of.
    private CollectionProviders Providers() => new([new WabbajackModlistProvider(Provider)], new LocalCollectionProvider(Provider));

    private async Task<Loadout.ReadOnly> LoadoutOf(IGameData game) => Loadout.Load(Connection.Db, (await TestGame.AddLoadout(Connection, game)).Value);

    private IDisposable PendingEntries(Loadout.ReadOnly loadout, out ReadOnlyObservableCollection<ILeftMenuItemViewModel> items) =>
        LoadoutLeftMenuViewModel.ObservePendingItems(Providers(), loadout, Controller(), WorkspaceId.From(Guid.NewGuid()), Provider)
            .Bind(out items)
            .Subscribe(_ => { });

    private IDisposable InstalledEntries(Loadout.ReadOnly loadout, out ReadOnlyObservableCollection<ILeftMenuItemViewModel> items) =>
        LoadoutLeftMenuViewModel.ObserveCollectionItems(Connection, Providers(), loadout, Controller(), WorkspaceId.From(Guid.NewGuid()), Provider)
            .Bind(out items)
            .Subscribe(_ => { });

    private static bool HasEntry(IEnumerable<ILeftMenuItemViewModel> items, string name) =>
        items.OfType<LeftMenuItemWithRightIconViewModel>().Any(item => item.Text.Value.Value == name);

    [Fact]
    public async Task AListOfTheLoadoutsGameWithNoInstallIsPendingInEveryLoadoutOfThatGame()
    {
        var first = await LoadoutOf(SkyrimSe);
        var second = await LoadoutOf(SkyrimSe);
        var file = await CollectionSeed.AddModlistFile(Connection, $"Pending list {Guid.NewGuid():N}", gameType: "SkyrimSpecialEdition");

        using var _ = PendingEntries(first, out var inFirst);
        using var __ = PendingEntries(second, out var inSecond);

        await Eventually(() =>
        {
            HasEntry(inFirst, file.Name).Should().BeTrue();
            HasEntry(inSecond, file.Name).Should().BeTrue();
        });
        inFirst.OfType<PendingCollectionLeftMenuItemViewModel>().Should().Contain(item => item.Text.Value.Value == file.Name);
    }

    [Fact]
    public async Task AListIsNotPendingInALoadoutOfAnotherGameNotEvenASibling()
    {
        var vr = await LoadoutOf(SkyrimVr);
        var file = await CollectionSeed.AddModlistFile(Connection, $"Special edition list {Guid.NewGuid():N}", gameType: "SkyrimSpecialEdition");
        var vrFile = await CollectionSeed.AddModlistFile(Connection, $"VR list {Guid.NewGuid():N}", gameType: "SkyrimVR");

        using var _ = PendingEntries(vr, out var items);

        await Eventually(() => HasEntry(items, vrFile.Name).Should().BeTrue());
        HasEntry(items, file.Name).Should().BeFalse("a Skyrim VR loadout is not offered a Skyrim SE list although Nexus gives them one domain");
    }

    [Fact]
    public async Task AListForAGameTheAppHasNoTitleForHasNoEntryAnywhere()
    {
        var loadout = await LoadoutOf(SkyrimSe);
        var file = await CollectionSeed.AddModlistFile(Connection, $"Unknown game list {Guid.NewGuid():N}", gameType: "NoSuchGame");
        var known = await CollectionSeed.AddModlistFile(Connection, $"Known game list {Guid.NewGuid():N}");

        using var _ = PendingEntries(loadout, out var items);

        await Eventually(() => HasEntry(items, known.Name).Should().BeTrue());
        HasEntry(items, file.Name).Should().BeFalse();
    }

    [Fact]
    public async Task AFinishedInstallFlipsTheEntryToTheToggleEntryInThatLoadoutOnly()
    {
        var installed = await LoadoutOf(SkyrimSe);
        var other = await LoadoutOf(SkyrimSe);
        var file = await CollectionSeed.AddModlistFile(Connection, $"Flip list {Guid.NewGuid():N}");
        var group = await WabbajackGroupSeed.AddGroup(Connection, file.Id, installed.LoadoutId, file.Name);

        using var _ = PendingEntries(installed, out var pendingHere);
        using var __ = InstalledEntries(installed, out var installedHere);
        using var ___ = PendingEntries(other, out var pendingElsewhere);

        await Eventually(() =>
        {
            HasEntry(pendingHere, file.Name).Should().BeTrue("an unfinished install is still offered");
            installedHere.OfType<CollectionLeftMenuItemViewModel>().Should().NotContain(item => item.CollectionGroupId.Value == group);
        });

        await WabbajackGroupSeed.Finish(Connection, group);

        await Eventually(() =>
        {
            HasEntry(pendingHere, file.Name).Should().BeFalse("a list is never listed twice");
            installedHere.OfType<CollectionLeftMenuItemViewModel>().Should().Contain(item => item.CollectionGroupId.Value == group);
            HasEntry(pendingElsewhere, file.Name).Should().BeTrue("the install belongs to one loadout");
        });
    }

    [Fact]
    public async Task RemovingTheGroupFlipsTheEntryBack()
    {
        var loadout = await LoadoutOf(SkyrimSe);
        var file = await CollectionSeed.AddModlistFile(Connection, $"Flip back list {Guid.NewGuid():N}");
        var group = await WabbajackGroupSeed.AddGroup(Connection, file.Id, loadout.LoadoutId, file.Name);
        await WabbajackGroupSeed.Finish(Connection, group);
        using var _ = PendingEntries(loadout, out var pending);
        using var __ = InstalledEntries(loadout, out var installed);
        await Eventually(() => installed.OfType<CollectionLeftMenuItemViewModel>().Should().Contain(item => item.CollectionGroupId.Value == group));
        HasEntry(pending, file.Name).Should().BeFalse();

        await WabbajackGroupSeed.Delete(Connection, group);

        await Eventually(() =>
        {
            HasEntry(pending, file.Name).Should().BeTrue();
            installed.OfType<CollectionLeftMenuItemViewModel>().Should().NotContain(item => item.CollectionGroupId.Value == group);
        });
    }

    [Fact]
    public async Task AFinishedInstallWithSkippedFilesStaysPending()
    {
        var loadout = await LoadoutOf(SkyrimSe);
        var file = await CollectionSeed.AddModlistFile(Connection, $"Skips list {Guid.NewGuid():N}");
        var group = await WabbajackGroupSeed.AddGroup(Connection, file.Id, loadout.LoadoutId, file.Name);
        await WabbajackGroupSeed.Finish(Connection, group);
        await WabbajackGroupSeed.AddSkip(Connection, group, loadout.LoadoutId);

        using var _ = PendingEntries(loadout, out var pending);
        using var __ = InstalledEntries(loadout, out var installed);

        await Eventually(() => HasEntry(pending, file.Name).Should().BeTrue());
        installed.OfType<CollectionLeftMenuItemViewModel>().Should().NotContain(item => item.CollectionGroupId.Value == group);
    }
}
