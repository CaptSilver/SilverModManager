using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.RedEngine.Cyberpunk2077.SortOrder;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests;

public class TrackedEntitiesTests(ITestOutputHelper helper) : ANexusCollectionTest<TrackedEntitiesTests>(helper)
{
    [Fact]
    public async Task ASortOrderWithoutItemsIsTracked()
    {
        var loadout = await CreateLoadout();
        var variety = ServiceProvider.GetRequiredService<RedModSortOrderVariety>();

        var sortOrderId = await variety.GetOrCreateSortOrderFor(loadout.LoadoutId, loadout.LoadoutId);

        OwnedIds(Connection.Db, loadout.Id).Should().Contain(sortOrderId.Value);
        NullRowCount(Connection.Db, loadout.Id).Should().Be(0, "a sort order without items must not produce a row with no id");
    }

    [Fact]
    public async Task ASortOrderAndItsItemsAreTracked()
    {
        var loadout = await CreateLoadout();
        var variety = ServiceProvider.GetRequiredService<RedModSortOrderVariety>();
        await AddRedMod(loadout.LoadoutId, "ModA");
        await AddRedMod(loadout.LoadoutId, "ModB");

        var sortOrderId = await variety.GetOrCreateSortOrderFor(loadout.LoadoutId, loadout.LoadoutId);
        await variety.ReconcileSortOrder(sortOrderId);

        var items = SortItemIds(Connection.Db, sortOrderId);
        items.Should().HaveCount(2);
        OwnedIds(Connection.Db, loadout.Id).Should().Contain(items.Append(sortOrderId.Value));
    }

    [Fact]
    public async Task AnOlderDbOnlyTracksWhatExistedThen()
    {
        var loadout = await CreateLoadout();
        var variety = ServiceProvider.GetRequiredService<RedModSortOrderVariety>();
        await AddRedMod(loadout.LoadoutId, "ModA");
        var sortOrderId = await variety.GetOrCreateSortOrderFor(loadout.LoadoutId, loadout.LoadoutId);
        await variety.ReconcileSortOrder(sortOrderId);
        var itemsThen = SortItemIds(Connection.Db, sortOrderId);
        itemsThen.Should().HaveCount(1);
        var then = Connection.Db.BasisTxId;

        await AddRedMod(loadout.LoadoutId, "ModB");
        await variety.ReconcileSortOrder(sortOrderId);
        var itemsNow = SortItemIds(Connection.Db, sortOrderId);
        itemsNow.Should().HaveCount(2);
        var itemAdded = itemsNow.Except(itemsThen).Single();

        var owned = OwnedIds(Connection.AsOf(then), loadout.Id);
        owned.Should().Contain(itemsThen.Append(sortOrderId.Value));
        owned.Should().NotContain(itemAdded);
        OwnedIds(Connection.Db, loadout.Id).Should().Contain(itemAdded);
    }

    private static EntityId[] SortItemIds(IDb db, SortOrderId sortOrderId) =>
        db.Datoms(SortOrderItem.ParentSortOrder, sortOrderId).Select(d => d.E).ToArray();

    private long NullRowCount(IDb db, EntityId loadout) =>
        Connection.Query<long>($"SELECT COUNT(*) FROM loadouts.TrackedEntitiesForLoadout({db}, {loadout}) WHERE Id IS NULL").First();
}
