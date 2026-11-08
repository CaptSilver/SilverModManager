using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.BuiltInEntities;

namespace SilverModManager.DataModel.Tests;

public class ProtectedLoadoutRevisionTests(ITestOutputHelper helper) : ANexusCollectionTest<ProtectedLoadoutRevisionTests>(helper)
{
    [Fact]
    public async Task AProtectedRevisionNamesItsLoadoutAndTransaction()
    {
        var loadout = await CreateLoadout();
        var restorePoint = await LoadoutSnapshot.WriteRestorePoint(Connection, loadout.LoadoutId);

        using var tx = Connection.BeginTransaction();
        var row = new ProtectedLoadoutRevision.New(tx)
        {
            LoadoutId = loadout.LoadoutId,
            RevisionId = TransactionId.From(EntityId.From(restorePoint.Value)),
            Reason = "Wabbajack update of Some list",
        };
        var result = await tx.Commit();

        var model = ProtectedLoadoutRevision.Load(Connection.Db, result.Remap(row).Id);
        model.LoadoutId.Should().Be(loadout.LoadoutId);
        model.Revision.Id.Value.Should().Be(restorePoint.Value);
        model.Reason.Should().Be("Wabbajack update of Some list");
        ProtectedLoadoutRevision.FindByLoadout(Connection.Db, loadout.LoadoutId).Select(r => r.Id)
            .Should().Equal(model.Id);
    }
}
