using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// The Update column and the Update All badge are built from <see cref="ModUpdate"/>s whichever source made them.
/// </summary>
public class LibraryUpdateColumnTests(IServiceProvider provider) : AUiTest(provider)
{
    private static LibraryComponents.UpdateAction ActionFor(ModUpdate update)
        => new(update, R3.Observable.Return(update));

    [Fact]
    public void TheUpdateButton_CountsTheUpdatesOfItsUpdate()
    {
        var action = ActionFor(new FakeModUpdate(Items: 2));

        action.UpdateButtonText.Value.Should().Be(string.Format(Language.Library_Update, 2));
        action.Update.Value.Count.Should().Be(2);
    }

    [Fact]
    public void TheUpdateButton_FollowsALaterUpdate()
    {
        var subject = new R3.Subject<ModUpdate>();
        var action = new LibraryComponents.UpdateAction(new FakeModUpdate(Items: 1), subject);
        using var _ = action.Activate();

        subject.OnNext(new FakeModUpdate(Items: 3));

        action.UpdateButtonText.Value.Should().Be(string.Format(Language.Library_Update, 3));
    }

    [Fact]
    public void Updates_AreOrderedByWhenTheyWerePublished()
    {
        var earlier = ActionFor(new FakeModUpdate(1, NewestVersion: "9.0", PublishedAt: DateTimeOffset.UnixEpoch));
        var later = ActionFor(new FakeModUpdate(1, NewestVersion: "1.0", PublishedAt: DateTimeOffset.UnixEpoch.AddDays(1)));

        earlier.CompareTo(later).Should().BeNegative();
        later.CompareTo(earlier).Should().BePositive();
    }

    [Fact]
    public void UpdatesPublishedTogether_AreOrderedByNewestVersion()
    {
        var published = DateTimeOffset.UnixEpoch;
        var lower = ActionFor(new FakeModUpdate(1, NewestVersion: "1.0", PublishedAt: published));
        var higher = ActionFor(new FakeModUpdate(1, NewestVersion: "2.0", PublishedAt: published));

        lower.CompareTo(higher).Should().BeNegative();
    }

    [Fact]
    public void AnUpdateWithNoPublishDate_SortsBeforeOneWithADate()
    {
        var unknown = ActionFor(new FakeModUpdate(1, PublishedAt: null));
        var known = ActionFor(new FakeModUpdate(1, PublishedAt: DateTimeOffset.UnixEpoch));

        unknown.CompareTo(known).Should().BeNegative();
    }

    // Selecting a row reads its item ids, so every row carries them.
    private static CompositeItemModel<EntityId> RowWithIds(ulong id)
    {
        var row = new CompositeItemModel<EntityId>(EntityId.From(id));
        row.Add(LibraryColumns.Actions.LibraryItemIdsComponentKey, new LibraryComponents.LibraryItemIds(LibraryItemId.From(EntityId.From(id))));
        return row;
    }

    private async Task<LibraryViewModel> CreateViewModel(params IModSource[] sources)
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Update column");
        return await OnUi(() => Task.FromResult(new LibraryViewModel(
            Substitute.For<IWindowManager>(),
            new ServicesWithSources(Provider, sources),
            Provider.GetRequiredService<IGameDomainToGameIdMappingCache>(),
            loadout.LoadoutId)));
    }

    [Fact]
    public async Task UpdateAllIsAvailable_WhenAnySourceHasUpdates()
    {
        var quiet = new FakeModSource("quiet");
        var busy = new FakeModSource("busy");
        var vm = await CreateViewModel(quiet, busy);
        using var _ = await OnUi(() => Task.FromResult(vm.Activator.Activate()));
        await Eventually(() => vm.HasAnyUpdatesAvailable.Should().BeFalse());

        busy.HasAnyUpdates.OnNext(true);

        await Eventually(() => vm.HasAnyUpdatesAvailable.Should().BeTrue());

        busy.HasAnyUpdates.OnNext(false);

        await Eventually(() => vm.HasAnyUpdatesAvailable.Should().BeFalse());
    }

    [Fact]
    public async Task TheSelectionCountOfUpdatableRows_CountsTheRowsCarryingAnUpdateAction()
    {
        var vm = await CreateViewModel(new FakeModSource());
        using var _ = await OnUi(() => Task.FromResult(vm.Activator.Activate()));
        var updatable = RowWithIds(0xA00001);
        updatable.Add(LibraryColumns.Actions.UpdateComponentKey, ActionFor(new FakeModUpdate(1)));
        var plain = RowWithIds(0xA00002);

        await OnUi(() =>
        {
            vm.Adapter.SelectedModels.Add(updatable);
            vm.Adapter.SelectedModels.Add(plain);
            return Task.CompletedTask;
        });

        await Eventually(() => vm.UpdatableSelectionCount.Should().Be(1));
    }
}
