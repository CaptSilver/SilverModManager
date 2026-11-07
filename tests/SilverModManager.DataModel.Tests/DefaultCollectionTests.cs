using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests;

public class DefaultCollectionTests(ITestOutputHelper helper) : ANexusCollectionTest<DefaultCollectionTests>(helper)
{
    [Fact]
    public async Task OfOrCreate_ReturnsTheLoadoutsExistingDefaultCollectionAndAddsNothing()
    {
        var loadout = await CreateLoadout();
        var existing = DefaultCollection.Of(Connection.Db, loadout)!.Value;
        var collectionsBefore = CollectionGroup.All(Connection.Db).Count();

        using var tx = Connection.BeginTransaction();
        var found = DefaultCollection.OfOrCreate(tx, Connection.Db, loadout);
        await tx.Commit();

        found.Should().Be(existing.Value);
        CollectionGroup.All(Connection.Db).Count().Should().Be(collectionsBefore);
    }

    [Fact]
    public async Task OfOrCreate_AddsADefaultCollectionWhenTheLoadoutHasNone()
    {
        var loadout = await CreateLoadout();
        using (var remove = Connection.BeginTransaction())
        {
            remove.Delete(DefaultCollection.Of(Connection.Db, loadout)!.Value.Value, recursive: false);
            await remove.Commit();
        }
        DefaultCollection.Of(Connection.Db, loadout).Should().BeNull("precondition: the default collection is gone");

        using var tx = Connection.BeginTransaction();
        var created = DefaultCollection.OfOrCreate(tx, Connection.Db, loadout);
        var result = await tx.Commit();

        DefaultCollection.Of(Connection.Db, loadout)!.Value.Value.Should().Be(result[created]);
    }
}
