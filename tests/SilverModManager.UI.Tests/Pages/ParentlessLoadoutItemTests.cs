using DynamicData;
using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// LoadoutItem.Parent is optional: an item that sits directly in the loadout has none, and reading it throws.
/// The row builders run inside database observers, where a throw also costs every observer queued behind it.
/// </summary>
public class ParentlessLoadoutItemTests(IServiceProvider provider) : AUiTest(provider)
{
    private async Task<LoadoutItem.ReadOnly> SeedParentlessItem()
    {
        var loadout = await SeedLoadoutRow(GameStore.Unknown, "parentless-" + Guid.NewGuid().ToString("N"));
        using var tx = Connection.BeginTransaction();
        _ = new LoadoutItemGroup.New(tx, out var groupId)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, groupId) { Name = "no parent", LoadoutId = loadout.Id },
        };
        var result = await tx.Commit();
        var item = LoadoutItem.Load(result.Db, result[groupId]);
        item.HasParent().Should().BeFalse("the item is seeded without a parent");
        return item;
    }

    [Fact]
    public async Task TheParentDisabledColumnTakesAnItemWithoutAParent()
    {
        var item = await SeedParentlessItem();
        var source = new SourceCache<LoadoutItem.ReadOnly, EntityId>(static i => i.Id);
        var model = new CompositeItemModel<EntityId>(item.Id);
        LoadoutDataProviderHelper.AddParentCollectionsDisabled(Connection, model, source.Connect());
        model.Activate();

        var add = () => source.AddOrUpdate(item);

        add.Should().NotThrow();
    }

    [Fact]
    public async Task TheMixedLockedColumnTakesAnItemWithoutAParent()
    {
        var item = await SeedParentlessItem();
        var source = new SourceCache<LoadoutItem.ReadOnly, EntityId>(static i => i.Id);
        var model = new CompositeItemModel<EntityId>(item.Id);
        LoadoutDataProviderHelper.AddMixLockedAndParentDisabled(Connection, model, source.Connect());
        model.Activate();

        var add = () => source.AddOrUpdate(item);

        add.Should().NotThrow();
    }

    [Fact]
    public async Task TheChildRowTakesAnItemWithoutAParent()
    {
        var item = await SeedParentlessItem();

        var build = () => LoadoutDataProviderHelper.ToChildItemModel(Connection, item);

        build.Should().NotThrow();
    }
}
