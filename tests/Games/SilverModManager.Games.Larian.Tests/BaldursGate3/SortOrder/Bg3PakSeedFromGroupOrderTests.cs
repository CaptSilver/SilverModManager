using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Larian.BaldursGate3;
using SilverModManager.Games.Larian.BaldursGate3.SortOrder;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Larian.Tests.BaldursGate3.SortOrder;

/// <summary>
/// An imported mod order reaches the pak order through the default sink, which seeds the variety from the groups.
/// </summary>
public class Bg3PakSeedFromGroupOrderTests(ITestOutputHelper helper)
    : AIsolatedGameTest<Bg3PakSeedFromGroupOrderTests, Larian.BaldursGate3.BaldursGate3>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddBaldursGate3()
        .AddUniversalGameLocator<Larian.BaldursGate3.BaldursGate3>(new Version("1.61"), locationsFileSystem: IsolatedKnownFolders.Create);

    private async Task<LoadoutItemGroupId> AddMod(Loadout.ReadOnly loadout, string name, string pak)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, name);
        AddFile(tx, loadout.LoadoutId, group, new GamePath(Bg3Constants.ModsLocationId, pak), pak);
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[group.Value]);
    }

    private async Task<LoadoutItemGroupId> AddContainer(Loadout.ReadOnly loadout)
    {
        using var tx = Connection.BeginTransaction();
        var container = AddEmptyGroup(tx, loadout.LoadoutId, "Container");
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[container.Value]);
    }

    private async Task Apply(Loadout.ReadOnly loadout, LoadoutItemGroupId container, params LoadoutItemGroupId[] winnerToLoser)
    {
        var sink = new DefaultPriorityOrderSink(LoadoutManager, NullLogger<DefaultPriorityOrderSink>.Instance);
        using var tx = Connection.BeginTransaction();
        sink.Apply(Refresh(loadout), ModOrder.Create(winnerToLoser.Select(group => new ModOrderEntry(group, null)), []),
            new ModOrderSource("collection Test", container, null), tx);
        await tx.Commit();
    }

    [Fact]
    public async Task TheBg3PakOrderFollowsTheImportedModOrder()
    {
        var loadout = await CreateLoadout();
        var container = await AddContainer(loadout);
        var a = await AddMod(loadout, "a", "a.pak");
        var b = await AddMod(loadout, "b", "b.pak");
        await Game.SortOrderManager.UpdateLoadOrders(loadout.LoadoutId, token: TestContext.Current.CancellationToken);

        var variety = Game.SortOrderManager.GetSortOrderVarieties().OfType<Bg3PakSortOrderVariety>().Single();
        var sortOrder = variety.GetSortOrderIdFor(loadout.LoadoutId, Connection.Db).Value;
        int IndexOf(string pak) => variety.GetSortOrderItems(sortOrder, Connection.Db).Single(item => item.Key.Key == pak).SortIndex;

        // The greater index wins for paks, so the winner of the import takes the later index.
        // Both directions, so the result cannot be the order the variety started in.
        await Apply(loadout, container, a, b);
        (IndexOf("a.pak"), IndexOf("b.pak")).Should().Be((1, 0));

        await Apply(loadout, container, b, a);
        (IndexOf("b.pak"), IndexOf("a.pak")).Should().Be((1, 0));
    }
}
