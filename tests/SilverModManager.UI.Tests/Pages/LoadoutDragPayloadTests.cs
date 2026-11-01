using DynamicData;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Controls.DragAndDrop;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.App.UI.Windows;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>What dragging rows of a loadout page carries: the loadout items the rows stand for, as the rows hold them.</summary>
public class LoadoutDragPayloadTests(IServiceProvider provider) : AUiTest(provider)
{
    private async Task<(LoadoutViewModel Vm, Loadout.ReadOnly Loadout)> CreateViewModel()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Loadout drag " + Guid.NewGuid());
        var vm = await OnUi(() => Task.FromResult(new LoadoutViewModel(
            Substitute.For<IWindowManager>(),
            new ServicesWithSources(Provider).With(Substitute.For<IWindowNotificationService>()),
            loadout.LoadoutId)));
        return (vm, loadout);
    }

    private static CompositeItemModel<EntityId> Row(EntityId key, params LoadoutItemId[] items)
    {
        var row = new CompositeItemModel<EntityId>(key);
        row.Add(LoadoutColumns.EnabledState.LoadoutItemIdsComponentKey, new LoadoutComponents.LoadoutItemIds(items[0]));
        return row;
    }

    /// <summary>A parent row: it holds the ids of its children, which the observable fills in.</summary>
    private static CompositeItemModel<EntityId> ParentRow(EntityId key, params LoadoutItemId[] children)
    {
        var row = new CompositeItemModel<EntityId>(key);
        var changes = new ChangeSet<LoadoutItemId, EntityId>(children.Select(child => new Change<LoadoutItemId, EntityId>(ChangeReason.Add, child.Value, child)));
        row.Add(LoadoutColumns.EnabledState.LoadoutItemIdsComponentKey, new LoadoutComponents.LoadoutItemIds(System.Reactive.Linq.Observable.Return<IChangeSet<LoadoutItemId, EntityId>>(changes)));
        return row;
    }

    [Fact]
    public async Task Rows_CarryTheirDistinctItemsForThisLoadout()
    {
        var (vm, loadout) = await CreateViewModel();
        var one = LoadoutItemId.From(EntityId.From(0x0100_0000_0000_0001));
        var two = LoadoutItemId.From(EntityId.From(0x0100_0000_0000_0002));

        var payload = vm.DragPayloadFor([Row(one.Value, one), Row(two.Value, two), Row(one.Value, one)]);

        payload.Should().BeOfType<DragPayload.LoadoutMods>().Which.Loadout.Should().Be(loadout.LoadoutId);
        ((DragPayload.LoadoutMods)payload!).Items.Should().Equal(one, two);
    }

    [Fact]
    public async Task AParentRow_CarriesItsChildren()
    {
        var (vm, _) = await CreateViewModel();
        var child = LoadoutItemId.From(EntityId.From(0x0100_0000_0000_0003));
        var other = LoadoutItemId.From(EntityId.From(0x0100_0000_0000_0004));

        var payload = vm.DragPayloadFor([ParentRow(EntityId.From(0x0100_0000_0000_0009), child, other)]);

        ((DragPayload.LoadoutMods)payload!).Items.Should().BeEquivalentTo([child, other]);
    }

    [Fact]
    public async Task NoRows_CarryNothing()
    {
        var (vm, _) = await CreateViewModel();

        vm.DragPayloadFor([]).Should().BeNull();
    }
}
