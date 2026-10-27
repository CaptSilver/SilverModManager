using FluentAssertions;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests.LoadOrder;

/// <summary>The dropped order rows go with their loadout: copied with it and removed with it, like its priority rows and rules.</summary>
public class ModOrderDroppedEntryLoadoutTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<ModOrderDroppedEntryLoadoutTests>(helper)
{
    private async Task<(LoadoutItemGroupId Container, ModOrderDroppedEntryId Row)> AddDroppedRow(LoadoutId loadoutId)
    {
        using var tx = Connection.BeginTransaction();
        var container = AddEmptyGroup(tx, loadoutId, "Pack");
        var row = new ModOrderDroppedEntry.New(tx)
        {
            LoadoutId = loadoutId,
            ContainerId = container,
            Source = "collection Pack",
            Item = "Ghost",
            Reason = "no installed mod matches it",
            Page = "Load Order",
        };
        var result = await tx.Commit();
        return (LoadoutItemGroupId.From(result[container].Value), ModOrderDroppedEntryId.From(result[row.Id].Value));
    }

    [Fact]
    public async Task ACopiedLoadoutGetsItsOwnDroppedRowsPointingAtTheCopiedContainer()
    {
        var loadout = await CreateLoadout();
        var (container, _) = await AddDroppedRow(loadout.LoadoutId);

        var copy = await LoadoutManager.CopyLoadout(loadout.LoadoutId);

        var db = Connection.Db;
        var row = ModOrderDroppedEntry.FindByLoadout(db, copy.LoadoutId).Should().ContainSingle().Subject;
        row.ContainerId.Should().NotBe(container);
        LoadoutItem.Load(db, row.ContainerId).LoadoutId.Should().Be(copy.LoadoutId, "the row's container is the copy's own group");
        (row.Source, row.Item, row.Reason, row.Page).Should().Be(("collection Pack", "Ghost", "no installed mod matches it", "Load Order"));

        ModOrderDroppedEntry.FindByLoadout(db, loadout.LoadoutId).Should().ContainSingle()
            .Which.ContainerId.Should().Be(container, "the original keeps its own row");
    }

    [Fact]
    public async Task DeletingALoadoutRemovesItsDroppedRows()
    {
        var loadout = await CreateLoadout();
        var (_, row) = await AddDroppedRow(loadout.LoadoutId);

        await LoadoutManager.DeleteLoadout(loadout.LoadoutId, deactivateIfActive: false);

        ModOrderDroppedEntry.Load(Connection.Db, row).IsValid().Should().BeFalse();
    }
}
