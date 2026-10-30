using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Ids;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// The inputs the Apply button's status compares: the newest transaction and the number of datoms among everything
/// an apply reads from the loadout.
/// </summary>
public class SyncInputRevisionTests(ITestOutputHelper helper) : AStubbedGameTest<SyncInputRevisionTests>(helper)
{
    private static readonly RelativePath[] ModFiles = ["mods/alpha/a.txt", "mods/alpha/b.txt"];

    private SyncInputs Inputs(LoadoutId loadout) => SyncInputRevision.Read(Connection.Db, loadout);

    private LoadoutWithTxId LastApplied(Loadout.ReadOnly loadout)
    {
        var metadata = GameInstallMetadata.Load(Connection.Db, Loadout.Load(Connection.Db, loadout.LoadoutId).InstallationId);
        return new LoadoutWithTxId(
            GameInstallMetadata.LastSyncedLoadout.Get(metadata),
            TxId.From(GameInstallMetadata.LastSyncedLoadoutTransaction.Get(metadata).Value));
    }

    private async Task<(Loadout.ReadOnly Loadout, LoadoutItemGroupId Mod)> AppliedWithOneMod()
    {
        var loadout = await CreateLoadout();
        LoadoutItemGroupId mod;
        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, ModFiles, loadout.LoadoutId, "Alpha");
            await tx.Commit();
        }
        mod = LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemGroup().Single(g => g.AsLoadoutItem().Name == "Alpha").LoadoutItemGroupId;
        await SynchronizerService.Synchronize(loadout.LoadoutId);
        return (Refresh(loadout), mod);
    }

    [Fact]
    public async Task TheNewestInputIsNoLaterThanTheApplyThatJustRan()
    {
        var (loadout, _) = await AppliedWithOneMod();

        var last = LastApplied(loadout);
        var inputs = Inputs(loadout.LoadoutId);

        last.Id.Should().Be(loadout.LoadoutId);
        inputs.MaxTx.Value.Should().BeGreaterThan(0UL, "the loadout has datoms");
        inputs.MaxTx.Value.Should().BeLessThanOrEqualTo(last.Tx.Value, "the apply's record is the newest thing the loadout says");
        SyncInputRevision.Read(Connection.AsOf(last.Tx), loadout.LoadoutId).Should().Be(inputs);
    }

    [Fact]
    public async Task ACommitOutsideTheLoadoutLeavesTheInputsAlone()
    {
        var (loadout, _) = await AppliedWithOneMod();
        var before = Inputs(loadout.LoadoutId);

        var other = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(other.Id, Loadout.Name, "unrelated");
            await tx.Commit();
        }

        Inputs(loadout.LoadoutId).Should().Be(before);
    }

    [Fact]
    public async Task AddingAModRaisesTheNewestInput()
    {
        var (loadout, _) = await AppliedWithOneMod();
        var before = Inputs(loadout.LoadoutId);

        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, ["mods/beta/c.txt"], loadout.LoadoutId, "Beta");
            await tx.Commit();
        }

        var after = Inputs(loadout.LoadoutId);
        after.MaxTx.Value.Should().BeGreaterThan(before.MaxTx.Value);
        after.Count.Should().BeGreaterThan(before.Count);
    }

    [Fact]
    public async Task RemovingTheOnlyModLowersTheCountWhileTheNewestInputStaysPut()
    {
        var (loadout, mod) = await AppliedWithOneMod();
        var before = Inputs(loadout.LoadoutId);

        using (var tx = Connection.BeginTransaction())
        {
            tx.Delete(mod, recursive: true);
            await tx.Commit();
        }

        var after = Inputs(loadout.LoadoutId);
        after.Count.Should().BeLessThan(before.Count);
        after.MaxTx.Value.Should().BeLessThanOrEqualTo(before.MaxTx.Value, "a pure retraction asserts nothing, so only the count can see it");
    }

    [Fact]
    public async Task AGameInstalledClaimIsAnInput()
    {
        var (loadout, _) = await AppliedWithOneMod();
        var before = Inputs(loadout.LoadoutId);

        using (var tx = Connection.BeginTransaction())
        {
            _ = new GameInstalledClaim.New(tx)
            {
                LoadoutId = loadout.LoadoutId,
                Path = new GamePath(LocationId.Game, "Data/alpha.esm").ToGamePathParentTuple(loadout.Id),
                Source = "Source",
                SourceId = "alpha",
                ClaimedAt = DateTimeOffset.UtcNow,
            };
            await tx.Commit();
        }

        Inputs(loadout.LoadoutId).MaxTx.Value.Should().BeGreaterThan(before.MaxTx.Value);
    }

    [Fact]
    public async Task AnEntityReferencingASortOrderOfTheLoadoutIsAnInput()
    {
        var (loadout, _) = await AppliedWithOneMod();
        SortOrderId sortOrder;
        using (var tx = Connection.BeginTransaction())
        {
            var order = new SortOrder.New(tx)
            {
                LoadoutId = loadout.LoadoutId,
                ParentEntity = loadout.LoadoutId,
                SortOrderTypeId = Guid.NewGuid(),
            };
            sortOrder = SortOrderId.From((await tx.Commit())[order.Id]);
        }
        var before = Inputs(loadout.LoadoutId);

        // Not a SortOrderItem and not named by the loadout queries: whatever a game hangs off its sort order.
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(EntityId.From(tx.TempId().Value), SortOrderItem.ParentSortOrder, sortOrder);
            await tx.Commit();
        }

        Inputs(loadout.LoadoutId).Count.Should().BeGreaterThan(before.Count);
    }
}
