using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.CreationEngine.SortOrder;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.CreationEngine.Tests.SortOrder;

/// <summary>
/// Seeding the plugin order from a group order must leave the stored row of a plugin whose mod left (a hidden
/// row) in its slot, since that row comes back at its old index when the mod does.
/// </summary>
public class PluginSeedFromGroupOrderTests(ITestOutputHelper helper)
    : AIsolatedGameTest<PluginSeedFromGroupOrderTests, CreationEngine.SkyrimSE.SkyrimSE>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddCreationEngine()
        .AddUniversalGameLocator<CreationEngine.SkyrimSE.SkyrimSE>(new Version("1.6.1170"), locationsFileSystem: IsolatedKnownFolders.Create);

    private record Mod(LoadoutItemGroupId Group, LoadoutFileId File);

    private async Task<Mod> AddMod(Loadout.ReadOnly loadout, string plugin)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, plugin);
        var file = AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, $"Data/{plugin}"));
        var result = await tx.Commit();
        return new Mod(LoadoutItemGroupId.From(result[group.Value]), LoadoutFileId.From(result[file.Value]));
    }

    private List<(string PluginName, int SortIndex)> StoredRows(SortOrderId id) =>
        Connection.Query<(string PluginName, int SortIndex)>($"SELECT PluginName, SortIndex FROM plugins.SortOrderItems({Connection}, {id.Value}) ORDER BY SortIndex").ToList();

    [Fact]
    public async Task SeedingFromGroupOrderKeepsAHiddenRowsSlot()
    {
        var loadout = await CreateLoadout();
        var variety = new PluginSortOrderVariety(ServiceProvider, Game);
        var id = await variety.GetOrCreateSortOrderFor(loadout.LoadoutId, loadout.LoadoutId, TestContext.Current.CancellationToken);

        // One reconcile per mod, so the stored order follows the install order: A, X, B.
        var a = await AddMod(loadout, "A.esp");
        await variety.ReconcileSortOrder(id, null, TestContext.Current.CancellationToken);
        var x = await AddMod(loadout, "X.esp");
        await variety.ReconcileSortOrder(id, null, TestContext.Current.CancellationToken);
        var b = await AddMod(loadout, "B.esp");
        await variety.ReconcileSortOrder(id, null, TestContext.Current.CancellationToken);

        using (var remove = Connection.BeginTransaction())
        {
            remove.Delete(x.File, recursive: false);
            remove.Delete(x.Group, recursive: false);
            await remove.Commit();
        }
        // No reconcile here: the visible rows are compacted to A=0, B=1 while the stored ones stay A=0, X=1, B=2.

        using var tx = Connection.BeginTransaction();
        ((IGroupOrderSeedable)variety).SeedFromGroupOrder(id, [a.Group, b.Group], tx, Connection.Db);
        await tx.Commit();

        // A wins and the greater index wins, so A takes the later of the two slots the visible plugins held.
        StoredRows(id).Should().Equal([("B.esp", 0), ("X.esp", 1), ("A.esp", 2)]);
    }
}
