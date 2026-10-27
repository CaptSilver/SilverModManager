using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using Xunit;

namespace SilverModManager.Collections.Tests.LoadOrder;

public class ModOrderImporterTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<ModOrderImporterTests>(helper)
{
    private ModOrderImporter Importer => new(ServiceProvider, Connection, NullLogger<ModOrderImporter>.Instance);

    private sealed class RecordingSink(string fixPage, params ModOrderDrop[] drops) : IModOrderSink
    {
        public string FixPage(IGame game) => fixPage;
        public List<ModOrder> Orders { get; } = [];

        public IReadOnlyList<ModOrderDrop> Apply(Loadout.ReadOnly loadout, ModOrder order, ModOrderSource source, ITransaction tx)
        {
            Orders.Add(order);
            return drops;
        }
    }

    private sealed class SinkGame(IServiceProvider provider, IModOrderSink sink) : StubbedGame(provider), IModOrderGame
    {
        public IModOrderSink? OrderSink => sink;
        public IVortexOrderFormat? VortexOrderFormat => null;
    }

    private async Task<(Loadout.ReadOnly Loadout, LoadoutItemGroupId ContainerA, LoadoutItemGroupId ContainerB, Dictionary<string, LoadoutItemGroupId> Groups)> Seed(params string[] names)
    {
        var loadout = await CreateLoadout();
        using var tx = Connection.BeginTransaction();
        var a = AddEmptyGroup(tx, loadout.LoadoutId, "Collection A");
        var b = AddEmptyGroup(tx, loadout.LoadoutId, "Collection B");
        var added = names.ToDictionary(name => name, name => AddEmptyGroup(tx, loadout.LoadoutId, name, a));
        var result = await tx.Commit();
        return (loadout, LoadoutItemGroupId.From(result[a].Value), LoadoutItemGroupId.From(result[b].Value),
            added.ToDictionary(p => p.Key, p => LoadoutItemGroupId.From(result[p.Value].Value)));
    }

    private async Task Import(Loadout.ReadOnly loadout, ModOrder order, ModOrderSource source)
    {
        using var tx = Connection.BeginTransaction();
        Importer.Import(Refresh(loadout), order, source, tx);
        await tx.Commit();
    }

    private (string Item, string Source, string Reason, string Page)[] Rows(LoadoutId loadout, LoadoutItemGroupId container) =>
        ModOrderDroppedEntry.FindByLoadout(Connection.Db, loadout)
            .Where(row => row.ContainerId == container)
            .Select(row => (row.Item, row.Source, row.Reason, row.Page))
            .Order()
            .ToArray();

    [Fact]
    public Task DefaultSinkIsUsedForAGameWithoutOne()
    {
        ModOrderImporter.SinkFor(GameInstallation.GetGame(), ServiceProvider).Should().BeOfType<DefaultPriorityOrderSink>();
        return Task.CompletedTask;
    }

    [Fact]
    public Task TheGamesOwnSinkWinsWhenItHasOne()
    {
        var sink = new RecordingSink("Somewhere");
        ModOrderImporter.SinkFor(new SinkGame(ServiceProvider, sink), ServiceProvider).Should().BeSameAs(sink);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task DroppedRowsAreReplacedPerContainerNotAppended()
    {
        var (loadout, a, b, g) = await Seed("x");
        var sourceA = new ModOrderSource("collection Same", a, null);
        var sourceB = new ModOrderSource("collection Same", b, null);

        await Import(loadout, ModOrder.Create([], [new ModOrderDrop("first", "r1"), new ModOrderDrop("second", "r2")]), sourceB);
        await Import(loadout, ModOrder.Create([], [new ModOrderDrop("old", "r3")]), sourceA);
        await Import(loadout, ModOrder.Create([new ModOrderEntry(g["x"], null)], [new ModOrderDrop("new", "r4")]), sourceA);

        Rows(loadout.LoadoutId, a).Should().Equal(("new", "collection Same", "r4", "File Conflicts"));
        Rows(loadout.LoadoutId, b).Should().Equal(("first", "collection Same", "r1", "File Conflicts"), ("second", "collection Same", "r2", "File Conflicts"));
    }

    [Fact]
    public async Task AnImportWithNothingDroppedClearsTheContainersRows()
    {
        var (loadout, a, _, _) = await Seed();
        var source = new ModOrderSource("collection A", a, null);

        await Import(loadout, ModOrder.Create([], [new ModOrderDrop("old", "r")]), source);
        await Import(loadout, ModOrder.Empty, source);

        Rows(loadout.LoadoutId, a).Should().BeEmpty();
    }

    [Fact]
    public async Task SinkDropsAreRecordedWithTheSinksPage()
    {
        var (loadout, a, _, g) = await Seed("a", "b");
        var source = new ModOrderSource("collection A", a, null);
        await Import(loadout, ModOrder.Create([new ModOrderEntry(g["a"], null), new ModOrderEntry(g["b"], null)], []), source);
        await LoadoutManager.AddGroupRule(loadout.LoadoutId, g["a"], g["b"], GroupRuleKind.After);

        await Import(loadout, ModOrder.Create([new ModOrderEntry(g["b"], null), new ModOrderEntry(g["a"], null)], [new ModOrderDrop("reader item", "reader reason")]), source);

        Rows(loadout.LoadoutId, a).Should().BeEquivalentTo(
        [
            ("the listed order of b and a", "collection A", "the stored rule 'a wins over b' puts them the other way round", "File Conflicts"),
            ("reader item", "collection A", "reader reason", "File Conflicts"),
        ]);
    }

    [Fact]
    public async Task ADropThatNamesItsOwnPageKeepsItAndTheRestGetTheSinksPage()
    {
        var (loadout, a, _, _) = await Seed();

        await Import(loadout, ModOrder.Create([], [new ModOrderDrop("own", "r1", "Pack"), new ModOrderDrop("plain", "r2")]), new ModOrderSource("collection A", a, null));

        // Cyberpunk has REDmod's sort order, so the priority rows are edited on File Conflicts, not a Load Order tab.
        Rows(loadout.LoadoutId, a).Should().BeEquivalentTo(
        [
            ("own", "collection A", "r1", "Pack"),
            ("plain", "collection A", "r2", "File Conflicts"),
        ]);
    }
}
