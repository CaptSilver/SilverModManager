using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.DataModel.Undo;
using SilverModManager.Games.RedEngine.Cyberpunk2077.Extensions;
using SilverModManager.Games.RedEngine.Cyberpunk2077.SortOrder;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests.Undo;

/// <summary>
/// Reverts a loadout that has REDmod load orders. The sort order manager is never started here: the
/// variety is driven directly, so only what the tests do to the load orders changes them.
/// </summary>
public class SortOrderRestorePointTests(ITestOutputHelper helper) : ANexusCollectionTest<SortOrderRestorePointTests>(helper)
{
    private UndoService UndoService => ServiceProvider.GetRequiredService<UndoService>();
    private RedModSortOrderVariety Variety => ServiceProvider.GetRequiredService<RedModSortOrderVariety>();

    [Fact]
    public async Task ReorderedItemsGoBackToTheirOldIndexes()
    {
        var loadout = await CreateLoadout();
        foreach (var name in new[] { "ModA", "ModB", "ModC" })
            await AddRedMod(loadout.LoadoutId, name);
        var sortOrder = await Variety.GetOrCreateSortOrderFor(loadout.LoadoutId, loadout.LoadoutId);
        await Variety.ReconcileSortOrder(sortOrder);
        await StampRevision(loadout.Id);
        var restorePoint = LastRevision(loadout.Id);
        var indexesBefore = SortIndexes(sortOrder);
        indexesBefore.Should().HaveCount(3);

        await Variety.MoveItems(sortOrder, [Key("ModA")], Key("ModC"), TargetRelativePosition.BeforeTarget);
        await StampRevision(loadout.Id);
        SortIndexes(sortOrder).Should().NotEqual(indexesBefore, "the move changed the order");

        var result = await UndoService.RevertTo(restorePoint);

        result.Residual.Should().BeEmpty();
        SortIndexes(sortOrder).Should().Equal(indexesBefore);
        foreach (var item in Connection.Db.RetrieveRedModSortableEntries(sortOrder))
            SortIndexDatoms(item.Id).Should().ContainSingle("the old index was replaced, not added to");
        await AssertReconcilingChangesNothing(loadout.LoadoutId);
    }

    [Fact]
    public async Task ACollectionSortOrderCreatedSinceIsRemovedWithItsGroup()
    {
        var loadout = await CreateLoadout();
        await StampRevision(loadout.Id);
        var restorePoint = LastRevision(loadout.Id);
        var ownedBefore = OwnedAt(restorePoint, loadout.Id);

        var collection = await AddNexusCollection(loadout.LoadoutId, "Collection", collectionId: 1, revisionId: 11, childCount: 1);
        await AddRedMod(loadout.LoadoutId, "ModInCollection", LoadoutItemGroupId.From(collection.Id.Value));
        var sortOrder = await Variety.GetOrCreateSortOrderFor(loadout.LoadoutId, collection.AsCollectionGroup().CollectionGroupId);
        await Variety.ReconcileSortOrder(sortOrder);
        await StampRevision(loadout.Id);
        var items = Connection.Db.RetrieveRedModSortableEntries(sortOrder).Select(item => item.Id).ToArray();
        items.Should().NotBeEmpty();
        OwnedIds(Connection.Db, loadout.Id).Should().Contain(items.Append(sortOrder.Value));

        var result = await UndoService.RevertTo(restorePoint);

        result.Residual.Should().BeEmpty();
        NexusCollectionLoadoutGroup.Load(Connection.Db, collection.Id).IsValid().Should().BeFalse();
        SortOrder.Load(Connection.Db, sortOrder).IsValid().Should().BeFalse("the sort order row is owned by the loadout");
        items.Should().OnlyContain(item => !SortOrderItem.Load(Connection.Db, item).IsValid());
        OwnedIds(Connection.Db, loadout.Id).Should().BeEquivalentTo(ownedBefore);
        await AssertReconcilingChangesNothing(loadout.LoadoutId);
    }

    [Fact]
    public async Task ACollectionSortOrderRemovedSinceComesBackWithItsItems()
    {
        var loadout = await CreateLoadout();
        var collection = await AddNexusCollection(loadout.LoadoutId, "Collection", collectionId: 1, revisionId: 11, childCount: 1);
        await AddRedMod(loadout.LoadoutId, "ModInCollection", LoadoutItemGroupId.From(collection.Id.Value));
        var sortOrder = await Variety.GetOrCreateSortOrderFor(loadout.LoadoutId, collection.AsCollectionGroup().CollectionGroupId);
        await Variety.ReconcileSortOrder(sortOrder);
        await StampRevision(loadout.Id);
        var restorePoint = LastRevision(loadout.Id);
        var items = Connection.Db.RetrieveRedModSortableEntries(sortOrder).Select(item => item.Id).ToArray();
        items.Should().NotBeEmpty();
        var before = DatomsOf(Connection.Db, items.Append(sortOrder.Value));

        // Stands in for the sort order manager, which deletes a collection's sort order when the collection goes.
        await LoadoutManager.RemoveCollection(collection.AsCollectionGroup().CollectionGroupId);
        await Variety.DeleteSortOrder(sortOrder);
        await StampRevision(loadout.Id);
        SortOrder.Load(Connection.Db, sortOrder).IsValid().Should().BeFalse();

        var result = await UndoService.RevertTo(restorePoint);

        result.Residual.Should().BeEmpty();
        SortOrder.Load(Connection.Db, sortOrder).IsValid().Should().BeTrue("the sort order comes back under its original id");
        DatomsOf(Connection.Db, items.Append(sortOrder.Value)).Should().BeEquivalentTo(before);
        await AssertReconcilingChangesNothing(loadout.LoadoutId);
    }

    /// <summary>
    /// The state a revert leaves is one the sort order logic agrees with: reconciling every load order
    /// of the loadout afterwards writes nothing new.
    /// </summary>
    private async Task AssertReconcilingChangesNothing(LoadoutId loadout)
    {
        var before = DatomsOf(Connection.Db, OwnedIds(Connection.Db, loadout.Value));
        foreach (var sortOrder in SortOrder.FindByLoadout(Connection.Db, loadout).ToArray())
            await Variety.ReconcileSortOrder(sortOrder.SortOrderId);

        DatomsOf(Connection.Db, OwnedIds(Connection.Db, loadout.Value)).Should().BeEquivalentTo(before);
    }

    private static SortItemKey<string> Key(string folder) => new(folder);

    private LoadoutRevision LastRevision(EntityId loadout) =>
        UndoService.RevisionsFor(loadout).OrderBy(r => r.Revision.TxEntity).Last().Revision;

    private HashSet<EntityId> OwnedAt(LoadoutRevision revision, EntityId loadout) =>
        OwnedIds(Connection.AsOf(TxId.From(revision.TxEntity.Value)), loadout);

    private List<(string Folder, int Index)> SortIndexes(SortOrderId sortOrder) =>
        Connection.Db.RetrieveRedModSortableEntries(sortOrder)
            .Select(item => (item.RedModFolderName.ToString(), item.AsSortOrderItem().SortIndex))
            .ToList();

    private IEnumerable<(EntityId E, AttributeId A, string V)> SortIndexDatoms(EntityId item)
    {
        var attribute = Connection.AttributeCache.GetAttributeId(SortOrderItem.SortIndex.Id);
        return DatomsOf(Connection.Db, [item]).Where(datom => datom.A == attribute);
    }
}
