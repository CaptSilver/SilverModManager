using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.CreationEngine.SortOrder;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.CreationEngine.Tests.SortOrder;

/// <summary>
/// A Vortex collection's <c>plugins</c> key orders plugins, not mods, so the Bethesda sink stages it on the plugin
/// order after the priority rows are written.
/// </summary>
public class GamebryoPluginsKeyTests(ITestOutputHelper helper)
    : AIsolatedGameTest<GamebryoPluginsKeyTests, CreationEngine.SkyrimSE.SkyrimSE>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddCreationEngine()
        .AddUniversalGameLocator<CreationEngine.SkyrimSE.SkyrimSE>(new Version("1.6.1170"), locationsFileSystem: IsolatedKnownFolders.Create);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private async Task<LoadoutItemGroupId> AddMod(Loadout.ReadOnly loadout, string plugin)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, plugin);
        AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, $"Data/{plugin}"));
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

    private async Task Import(Loadout.ReadOnly loadout, LoadoutItemGroupId container, string? pluginsJson, params LoadoutItemGroupId[] winnerToLoser)
    {
        var manifest = new CollectionRoot
        {
            Info = new CollectionInfo { Name = "Test", DomainName = GameDomain.From("skyrimspecialedition") },
            Plugins = pluginsJson is null ? null : Json(pluginsJson),
        };
        var inner = new DefaultPriorityOrderSink(LoadoutManager, NullLogger<DefaultPriorityOrderSink>.Instance);
        var sink = new BethesdaModOrderSink(inner, Game.PluginVariety(), Game);

        using var tx = Connection.BeginTransaction();
        sink.Apply(Refresh(loadout), ModOrder.Create(winnerToLoser.Select(group => new ModOrderEntry(group, null)), []),
            new ModOrderSource("collection Test", container, manifest), tx);
        await tx.Commit();
    }

    private string[] StoredNames(Loadout.ReadOnly loadout) =>
        Game.PluginVariety().StoredOrder(Refresh(loadout)).OrderBy(row => row.SortIndex).Select(row => row.Name).ToArray();

    [Fact]
    public void ReadsNameAndEnabledInArrayOrder()
    {
        var (plugins, unmapped) = GamebryoPluginsKey.Read(Json("""[{"name":"B.esp","enabled":true},{"name":"A.esp","enabled":false},{"name":"C.esp"}]"""));

        plugins.Should().Equal([("B.esp", (bool?)true), ("A.esp", false), ("C.esp", null)]);
        unmapped.Should().BeEmpty();
    }

    [Fact]
    public void AnEntryWithoutANameIsADropNotAThrow()
    {
        var (plugins, unmapped) = GamebryoPluginsKey.Read(Json("""[{"enabled":true},"B.esp",{"name":"A.esp","enabled":true}]"""));

        plugins.Should().Equal([("A.esp", (bool?)true)]);
        unmapped.Should().HaveCount(2);
        unmapped.Should().OnlyContain(drop => drop.Reason.Length > 0);
    }

    [Fact]
    public async Task TheSinkStagesPluginsAfterThePriorityRows()
    {
        var loadout = await CreateLoadout();
        var container = await AddContainer(loadout);
        var a = await AddMod(loadout, "A.esp");
        var b = await AddMod(loadout, "B.esp");
        var variety = Game.PluginVariety();
        await variety.ReconcileSortOrder(variety.SortOrderOf(loadout, Connection), null, TestContext.Current.CancellationToken);
        StoredNames(loadout).Should().Equal("A.esp", "B.esp");

        // The mod order alone would leave A.esp first (the winner is last); the plugins key says otherwise.
        await Import(loadout, container, """[{"name":"B.esp","enabled":true},{"name":"A.esp","enabled":true}]""", b, a);

        StoredNames(loadout).Should().Equal("B.esp", "A.esp");
    }

    [Fact]
    public async Task WithoutAPluginsKeyThePluginOrderFollowsTheModOrder()
    {
        var loadout = await CreateLoadout();
        var container = await AddContainer(loadout);
        var a = await AddMod(loadout, "A.esp");
        var b = await AddMod(loadout, "B.esp");
        var variety = Game.PluginVariety();
        await variety.ReconcileSortOrder(variety.SortOrderOf(loadout, Connection), null, TestContext.Current.CancellationToken);
        StoredNames(loadout).Should().Equal("A.esp", "B.esp");

        // The later plugin wins, so the winner-first mod order puts its plugin last. Both directions, so neither result is the starting order.
        await Import(loadout, container, null, a, b);
        StoredNames(loadout).Should().Equal("B.esp", "A.esp");

        await Import(loadout, container, null, b, a);
        StoredNames(loadout).Should().Equal("A.esp", "B.esp");
    }
}
