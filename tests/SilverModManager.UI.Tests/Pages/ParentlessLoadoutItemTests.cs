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

    private async Task<(LoadoutItem.ReadOnly Parentless, LoadoutItem.ReadOnly Inside)> SeedParentlessAndCollectionItem(string collectionName)
    {
        var loadout = await SeedLoadoutRow(GameStore.Unknown, "mixed-" + Guid.NewGuid().ToString("N"));
        using var tx = Connection.BeginTransaction();
        _ = new LoadoutItemGroup.New(tx, out var bareId)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, bareId) { Name = "bare", LoadoutId = loadout.Id },
        };
        _ = new CollectionGroup.New(tx, out var collectionId)
        {
            IsReadOnly = false,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, collectionId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, collectionId) { Name = collectionName, LoadoutId = loadout.Id },
            },
        };
        _ = new LoadoutItemGroup.New(tx, out var insideId)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, insideId) { Name = "inside", LoadoutId = loadout.Id, ParentId = collectionId },
        };
        var result = await tx.Commit();
        return (LoadoutItem.Load(result.Db, result[bareId]), LoadoutItem.Load(result.Db, result[insideId]));
    }

    [Fact]
    public async Task TheCollectionsColumnNamesTheCollectionOfAMixedSelection()
    {
        var (bare, inside) = await SeedParentlessAndCollectionItem("Mixed Collection");
        var source = new SourceCache<LoadoutItem.ReadOnly, EntityId>(static i => i.Id);
        var values = new List<string>();
        Exception? error = null;
        using var subscription = LoadoutDataProviderHelper.ObserveCollectionNames(source.Connect()).Subscribe(values.Add, e => error = e);

        source.AddOrUpdate(bare);
        source.AddOrUpdate(inside);

        error.Should().BeNull("a parentless item is in no collection, it is not an error");
        values.Should().NotBeEmpty().And.EndWith("Mixed Collection");
    }

    [Fact]
    public async Task TheCollectionsColumnIsEmptyForOnlyParentlessItems()
    {
        var item = await SeedParentlessItem();
        var source = new SourceCache<LoadoutItem.ReadOnly, EntityId>(static i => i.Id);
        var values = new List<string>();
        Exception? error = null;
        using var subscription = LoadoutDataProviderHelper.ObserveCollectionNames(source.Connect()).Subscribe(values.Add, e => error = e);

        source.AddOrUpdate(item);

        error.Should().BeNull();
        values.Should().NotBeEmpty().And.OnlyContain(static v => v == string.Empty);
    }

    [Fact]
    public async Task TheLibraryRelatedCollectionsNameTheCollectionOfAMixedSelection()
    {
        var (bare, inside) = await SeedParentlessAndCollectionItem("Mixed Collection");
        var source = new SourceCache<LoadoutItem.ReadOnly, EntityId>(static i => i.Id);
        var latest = new List<string>();
        Exception? error = null;
        using var subscription = LibraryDataProviderHelper.ObserveInstalledCollectionNames(source.Connect())
            .QueryWhenChanged(static items => items.ToArray())
            .Subscribe(items => { latest.Clear(); latest.AddRange(items); }, e => error = e);

        source.AddOrUpdate(bare);
        source.AddOrUpdate(inside);

        error.Should().BeNull("a parentless item is in no collection, it is not an error");
        latest.Should().Equal("Mixed Collection");
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
