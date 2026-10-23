using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.DataModel.Undo;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests.Undo;

public class RestorePointTests(ITestOutputHelper helper) : ANexusCollectionTest<RestorePointTests>(helper)
{
    [Fact]
    public async Task CanGetRestorePoints()
    {
        var undoService = ServiceProvider.GetRequiredService<UndoService>();
        var loadout = await CreateLoadout();
        var loadoutId = loadout.Id;

        // Creating a loadout already records its first revision.
        undoService.RevisionsFor(loadoutId).Select(r => r.ModCount).Should().Equal([0]);

        // Build on it: collection A (3 mods), collection B (2 mods), then A removed.
        var collectionA = await AddNexusCollection(loadout, "Collection A", collectionId: 1, revisionId: 11, childCount: 3);
        await StampRevision(loadoutId);
        await AddNexusCollection(loadout, "Collection B", collectionId: 2, revisionId: 22, childCount: 2);
        await StampRevision(loadoutId);
        await LoadoutManager.RemoveCollection(collectionA.AsCollectionGroup());
        await StampRevision(loadoutId);

        var before = undoService.RevisionsFor(loadoutId).OrderBy(r => r.Revision.TxEntity).ToList();
        before.Select(r => r.ModCount).Should().Equal([0, 3, 5, 2]);

        // Revert to the point where both collections were installed.
        var result = await undoService.RevertTo(before[2].Revision);

        result.Residual.Should().BeEmpty();

        LinkedItemCount(loadoutId).Should().Be(5, "both collections are back");
        var restoredA = NexusCollectionLoadoutGroup.Load(Connection.Db, collectionA.Id);
        restoredA.IsValid().Should().BeTrue();
        NexusCollectionLoadoutGroup.Collection.Contains(restoredA).Should().BeTrue();
        restoredA.AsCollectionGroup().AsLoadoutItemGroup().Children.Should().HaveCount(3);

        var after = undoService.RevisionsFor(loadoutId).OrderBy(r => r.Revision.TxEntity).ToList();
        after.Should().HaveCount(before.Count + 1);
        after.Take(before.Count).Select(r => (r.Revision.TxEntity, r.ModCount))
            .Should().Equal(before.Select(r => (r.Revision.TxEntity, r.ModCount)));
        after[^1].ModCount.Should().Be(5);
        IsSnapshotOf(after[^1].Revision.TxEntity, loadoutId).Should().BeTrue("a revert is recorded as a snapshot of the loadout");
    }

    private int LinkedItemCount(EntityId loadout)
    {
        var db = Connection.Db;
        return (int)Connection.Query<long>($"SELECT COUNT(*) FROM mdb_LibraryLinkedLoadoutItem(Db=>{db}) WHERE Loadout = {loadout}").First();
    }

    private bool IsSnapshotOf(EntityId tx, EntityId loadout)
    {
        var db = Connection.Db;
        return Connection.Query<long>($"SELECT COUNT(*) FROM mdb_Datoms(Db=>{db}, A=>'LoadoutSnapshot/Snapshot') WHERE E = {tx} AND V = {loadout}").First() == 1;
    }
}
