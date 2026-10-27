using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using NSubstitute;
using DynamicData.Kernel;
using SilverModManager.Abstractions.Games;
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

    private async Task<LoadoutItemGroupId> AddMod(Loadout.ReadOnly loadout, string plugin, LoadoutItemGroupId? container = null)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, plugin, container);
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

    private bool[] StoredSwitches(Loadout.ReadOnly loadout) =>
        Game.PluginVariety().StoredOrder(Refresh(loadout)).OrderBy(row => row.SortIndex).Select(row => row.IsEnabled).ToArray();

    private async Task<IReadOnlyList<ModOrderDrop>> ImportRaw(Loadout.ReadOnly loadout, LoadoutItemGroupId container, string pluginsJson, params LoadoutItemGroupId[] winnerToLoser)
    {
        var manifest = new CollectionRoot
        {
            Info = new CollectionInfo { Name = "Test", DomainName = GameDomain.From("skyrimspecialedition") },
            Plugins = Json(pluginsJson),
        };
        return await Apply(loadout, container, manifest, winnerToLoser);
    }

    private async Task<IReadOnlyList<ModOrderDrop>> Apply(Loadout.ReadOnly loadout, LoadoutItemGroupId container, CollectionRoot manifest, params LoadoutItemGroupId[] winnerToLoser)
    {
        var sink = (IModOrderSink)new BethesdaModOrderSink(new DefaultPriorityOrderSink(LoadoutManager, NullLogger<DefaultPriorityOrderSink>.Instance), Game.PluginVariety(), Game);
        using var tx = Connection.BeginTransaction();
        var drops = sink.Apply(Refresh(loadout), ModOrder.Create(winnerToLoser.Select(group => new ModOrderEntry(group, null)), []),
            new ModOrderSource("collection Test", container, manifest), tx);
        await tx.Commit();
        return drops;
    }

    private async Task ReconcilePlugins(Loadout.ReadOnly loadout)
    {
        var variety = Game.PluginVariety();
        await variety.ReconcileSortOrder(variety.SortOrderOf(loadout, Connection), null, TestContext.Current.CancellationToken);
    }

    private string[] StoredNames(Loadout.ReadOnly loadout) =>
        Game.PluginVariety().StoredOrder(Refresh(loadout)).OrderBy(row => row.SortIndex).Select(row => row.Name).ToArray();

    [Fact]
    public void ReadsNameAndEnabledInArrayOrder()
    {
        var (plugins, unmapped) = GamebryoPluginsKey.Read(Json("""[{"name":"B.esp","enabled":true},{"name":"A.esp","enabled":false},{"name":"C.esp"}]"""));

        // A listed plugin without a switch is on: Vortex lists only the enabled ones.
        plugins.Should().Equal([("B.esp", true), ("A.esp", false), ("C.esp", true)]);
        unmapped.Should().BeEmpty();
    }

    [Fact]
    public void AnEntryWithoutANameIsADropNotAThrow()
    {
        var (plugins, unmapped) = GamebryoPluginsKey.Read(Json("""[{"enabled":true},"B.esp",{"name":"A.esp","enabled":true}]"""));

        plugins.Should().Equal([("A.esp", true)]);
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

    [Fact]
    public async Task APluginTheCollectionShipsButDoesNotListIsSwitchedOff()
    {
        var loadout = await CreateLoadout();
        var container = await AddContainer(loadout);
        var a = await AddMod(loadout, "A.esp", container);
        var b = await AddMod(loadout, "B.esp", container);
        var outside = await AddMod(loadout, "C.esp");
        await ReconcilePlugins(loadout);

        await ImportRaw(loadout, container, """[{"name":"B.esp"}]""", a, b);

        // B.esp is listed (so on, though the entry has no switch), A.esp is shipped but unlisted, C.esp is not the collection's.
        StoredNames(loadout).Should().Equal("B.esp", "A.esp", "C.esp");
        StoredSwitches(loadout).Should().Equal(true, false, true);
    }

    [Fact]
    public async Task AnExplicitEnabledFalseReachesTheRowSwitch()
    {
        var loadout = await CreateLoadout();
        var container = await AddContainer(loadout);
        var a = await AddMod(loadout, "A.esp", container);
        var b = await AddMod(loadout, "B.esp", container);
        await ReconcilePlugins(loadout);

        await ImportRaw(loadout, container, """[{"name":"A.esp","enabled":false},{"name":"B.esp","enabled":true}]""", a, b);

        StoredNames(loadout).Should().Equal("A.esp", "B.esp");
        StoredSwitches(loadout).Should().Equal(false, true);
    }

    [Fact]
    public async Task AValueThatIsNotAnArrayIsADropAndTheModOrderStillSeedsThePluginOrder()
    {
        var loadout = await CreateLoadout();
        var container = await AddContainer(loadout);
        var a = await AddMod(loadout, "A.esp", container);
        var b = await AddMod(loadout, "B.esp", container);
        await ReconcilePlugins(loadout);

        var drops = await ImportRaw(loadout, container, """{"name":"B.esp"}""", a, b);

        drops.Should().ContainSingle(drop => drop.Item == "plugins");
        // Seeding stayed on: the winner-first mod order puts A.esp last.
        StoredNames(loadout).Should().Equal("B.esp", "A.esp");
    }

    [Fact]
    public async Task TheGameHandsOutOneBethesdaSinkAndNoVortexOrderFormat()
    {
        var game = (IModOrderGame)Game;

        game.OrderSink.Should().BeOfType<BethesdaModOrderSink>().Which.FixPage.Should().Be("Load Order");
        game.OrderSink.Should().BeSameAs(game.OrderSink);
        game.VortexOrderFormat.Should().BeNull();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ListedPluginsComeAfterTheEarlyLoadersAndBeforeTheUnlistedRows()
    {
        var loadout = await CreateLoadout();
        var container = await AddContainer(loadout);
        var a = await AddMod(loadout, "A.esp", container);
        var b = await AddMod(loadout, "B.esp", container);
        var c = await AddMod(loadout, "C.esp");
        var early = await AddMod(loadout, "Skyrim.esm");
        await ReconcilePlugins(loadout);
        StoredNames(loadout).Should().Equal("Skyrim.esm", "A.esp", "B.esp", "C.esp");

        // Skyrim.esm is listed last, but it is an early loader and stays locked in front; C.esp is not listed and follows.
        await ImportRaw(loadout, container, """[{"name":"B.esp"},{"name":"A.esp"},{"name":"Skyrim.esm"}]""", a, b);

        StoredNames(loadout).Should().Equal("Skyrim.esm", "B.esp", "A.esp", "C.esp");
        StoredSwitches(loadout).Skip(1).Should().Equal(true, true, true);
    }

    [Fact]
    public async Task APluginsKeyDecidesThePluginOrderEvenWithASilverLoadOrder()
    {
        var loadout = await CreateLoadout();
        var container = await AddContainer(loadout);
        var a = await AddMod(loadout, "A.esp", container);
        var b = await AddMod(loadout, "B.esp", container);
        await ReconcilePlugins(loadout);

        var manifest = new CollectionRoot
        {
            Info = new CollectionInfo { Name = "Test", DomainName = GameDomain.From("skyrimspecialedition") },
            Plugins = Json("""[{"name":"A.esp"},{"name":"B.esp"}]"""),
            SilverLoadOrder = [],
        };
        await Apply(loadout, container, manifest, b, a);

        StoredNames(loadout).Should().Equal("A.esp", "B.esp");
    }

    [Fact]
    public async Task TheSinkWritesTheModOrderToThePriorityRowsToo()
    {
        var loadout = await CreateLoadout();
        var container = await AddContainer(loadout);
        var a = await AddMod(loadout, "A.esp", container);
        var b = await AddMod(loadout, "B.esp", container);
        await ReconcilePlugins(loadout);

        await ImportRaw(loadout, container, """[{"name":"B.esp"},{"name":"A.esp"}]""", a, b);

        var lowestFirst = LoadoutItemGroupPriority.FindByLoadout(Connection.Db, loadout.LoadoutId)
            .OrderBy(row => row.Priority).Select(row => row.TargetId).ToArray();
        lowestFirst.Should().ContainInOrder(b, a); // A was listed first, so it wins
    }

    [Fact]
    public async Task WithoutAPluginOrderYetThePluginsAreADropNotAThrow()
    {
        var loadout = await CreateLoadout();
        var container = await AddContainer(loadout);
        var a = await AddMod(loadout, "A.esp", container);
        var variety = Substitute.For<IPluginSortOrderVariety>();
        variety.GetSortOrderIdFor(default, default).ReturnsForAnyArgs(Optional<SortOrderId>.None);
        var sink = new BethesdaModOrderSink(new DefaultPriorityOrderSink(LoadoutManager, NullLogger<DefaultPriorityOrderSink>.Instance), variety, Game);
        var manifest = new CollectionRoot
        {
            Info = new CollectionInfo { Name = "Test", DomainName = GameDomain.From("skyrimspecialedition") },
            Plugins = Json("""[{"name":"A.esp"}]"""),
        };

        using var tx = Connection.BeginTransaction();
        var drops = sink.Apply(Refresh(loadout), ModOrder.Create([new ModOrderEntry(a, null)], []), new ModOrderSource("collection Test", container, manifest), tx);

        drops.Should().Contain(drop => drop.Reason == "the plugin order was not created yet");
    }

    [Theory]
    [InlineData("""{"plugins":[{"name":"A.esp"}],"groups":[]}""", true)]
    [InlineData("""{"plugins":[],"groups":[{"name":"g","after":[]}]}""", true)]
    [InlineData("""{"plugins":[],"groups":[]}""", false)]
    public async Task LootRulesAreReportedAsNotApplied(string rules, bool dropped)
    {
        var loadout = await CreateLoadout();
        var container = await AddContainer(loadout);
        var a = await AddMod(loadout, "A.esp", container);
        var manifest = new CollectionRoot
        {
            Info = new CollectionInfo { Name = "Test", DomainName = GameDomain.From("skyrimspecialedition") },
            PluginRules = Json(rules),
        };

        var drops = await Apply(loadout, container, manifest, a);

        drops.Any(drop => drop.Item == "pluginRules").Should().Be(dropped);
    }
}
