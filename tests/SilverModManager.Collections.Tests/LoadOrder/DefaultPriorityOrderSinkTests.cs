using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.RedEngine.Cyberpunk2077.SortOrder;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.Collections.Tests.LoadOrder;

public class DefaultPriorityOrderSinkTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<DefaultPriorityOrderSinkTests>(helper)
{
    private DefaultPriorityOrderSink Sink => new(LoadoutManager, NullLogger<DefaultPriorityOrderSink>.Instance);

    private async Task<(Loadout.ReadOnly Loadout, LoadoutItemGroupId Container, Dictionary<string, LoadoutItemGroupId> Groups)> Seed(params string[] names)
    {
        var loadout = await CreateLoadout();
        using var tx = Connection.BeginTransaction();
        var container = AddEmptyGroup(tx, loadout.LoadoutId, "Container");
        var added = names.ToDictionary(name => name, name => AddEmptyGroup(tx, loadout.LoadoutId, name, container));
        var result = await tx.Commit();
        return (loadout, LoadoutItemGroupId.From(result[container].Value), added.ToDictionary(p => p.Key, p => LoadoutItemGroupId.From(result[p.Value].Value)));
    }

    private async Task<IReadOnlyList<ModOrderDrop>> Apply(Loadout.ReadOnly loadout, LoadoutItemGroupId container, params (LoadoutItemGroupId Group, bool? Enabled)[] winnerToLoser)
    {
        using var tx = Connection.BeginTransaction();
        var drops = Sink.Apply(Refresh(loadout), ModOrder.Create(winnerToLoser.Select(e => new ModOrderEntry(e.Group, e.Enabled)), []),
            new ModOrderSource("collection Test", container, null), tx);
        await tx.Commit();
        return drops;
    }

    /// <summary>A mod installed after the order was applied: its row goes above every other.</summary>
    private async Task<LoadoutItemGroupId> InstallOnTop(LoadoutId loadoutId, string name)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadoutId, name);
        var top = LoadoutItemGroupPriority.FindByLoadout(Connection.Db, loadoutId).Max(r => r.Priority.Value);
        _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadoutId, TargetId = group, Priority = ConflictPriority.From(top + 1) };
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[group].Value);
    }

    private ulong PriorityOf(LoadoutItemGroupId group) => LoadoutItemGroupPriority.FindByTarget(Connection.Db, group).Single().Priority.Value;

    private bool IsDisabled(LoadoutItemGroupId group) => LoadoutItem.Load(Connection.Db, group).IsDisabled;

    [Fact]
    public async Task AppliesTheOrderThroughThePriorityRows()
    {
        var (loadout, container, g) = await Seed("a", "b", "c");

        await Apply(loadout, container, (g["c"], null), (g["a"], null), (g["b"], null));

        PriorityOf(g["c"]).Should().BeGreaterThan(PriorityOf(g["a"]));
        PriorityOf(g["a"]).Should().BeGreaterThan(PriorityOf(g["b"]));
    }

    [Fact]
    public async Task EnabledFalseSwitchesOffAndTrueSwitchesBackOn()
    {
        var (loadout, container, g) = await Seed("a", "b");

        await Apply(loadout, container, (g["a"], null), (g["b"], false));
        IsDisabled(g["b"]).Should().BeTrue();
        IsDisabled(g["a"]).Should().BeFalse();

        // null leaves the switch where it is.
        await Apply(loadout, container, (g["a"], null), (g["b"], null));
        IsDisabled(g["b"]).Should().BeTrue();

        await Apply(loadout, container, (g["a"], null), (g["b"], true));
        IsDisabled(g["b"]).Should().BeFalse();
    }

    [Fact]
    public async Task ARepeatApplyKeepsTheBlockBelowModsInstalledSince()
    {
        var (loadout, container, g) = await Seed("a", "b");

        await Apply(loadout, container, (g["a"], null), (g["b"], null));
        g["x"] = await InstallOnTop(loadout.LoadoutId, "x");
        PriorityOf(g["x"]).Should().BeGreaterThan(PriorityOf(g["a"]));

        await Apply(loadout, container, (g["b"], null), (g["a"], null));

        PriorityOf(g["x"]).Should().BeGreaterThan(PriorityOf(g["a"]));
        PriorityOf(g["x"]).Should().BeGreaterThan(PriorityOf(g["b"]));
    }

    [Fact]
    public async Task ARepeatApplyOverwritesAReorderTheUserMadeInsideTheBlock()
    {
        var (loadout, container, g) = await Seed("a", "b");
        await Apply(loadout, container, (g["a"], null), (g["b"], null));

        // The user puts b above a.
        var rows = LoadoutItemGroupPriority.FindByLoadout(Connection.Db, loadout.LoadoutId).ToDictionary(r => r.TargetId, r => r.LoadoutItemGroupPriorityId);
        (await LoadoutManager.ResolveFileConflicts([rows[g["b"]]], rows[g["a"]])).IsRuleViolation.Should().BeFalse();
        PriorityOf(g["b"]).Should().BeGreaterThan(PriorityOf(g["a"]));

        // The collection's order is the designed winner of a re-install: the user's reorder inside the block does not survive it.
        await Apply(loadout, container, (g["a"], null), (g["b"], null));

        PriorityOf(g["a"]).Should().BeGreaterThan(PriorityOf(g["b"]));
    }

    [Fact]
    public async Task ARuleOverrideComesBackAsADrop()
    {
        var (loadout, container, g) = await Seed("a", "b");
        await Apply(loadout, container, (g["a"], null), (g["b"], null));
        await LoadoutManager.AddGroupRule(loadout.LoadoutId, g["a"], g["b"], GroupRuleKind.After);

        var drops = await Apply(loadout, container, (g["b"], null), (g["a"], null));

        drops.Should().ContainSingle().Which.Should().Be(new ModOrderDrop("b above a", "the stored rule 'a wins over b' puts them the other way round"));
        PriorityOf(g["a"]).Should().BeGreaterThan(PriorityOf(g["b"]));
    }

    [Fact]
    public async Task ADisplacedPairComesBackWithTheRoomMakingWording()
    {
        var (loadout, container, g) = await Seed("w", "y", "u", "z");
        await Apply(loadout, container, (g["z"], null), (g["u"], null), (g["y"], null), (g["w"], null));
        await LoadoutManager.AddGroupRule(loadout.LoadoutId, g["z"], g["u"], GroupRuleKind.After);

        var drops = await Apply(loadout, container, (g["w"], null), (g["y"], null), (g["z"], null));

        drops.Should().Contain(new ModOrderDrop("y above z", "the order moved to make room for the stored rule 'z wins over u'"));
    }

    private async Task<(Loadout.ReadOnly Loadout, LoadoutItemGroupId Container, Dictionary<string, LoadoutItemGroupId> Groups, Func<string, int> IndexOf)> SeedRedMods()
    {
        var (loadout, container, g) = await Seed("a", "b");
        using (var tx = Connection.BeginTransaction())
        {
            foreach (var name in new[] { "a", "b" })
                AddFile(tx, loadout.LoadoutId, g[name], new GamePath(LocationId.Game, $"mods/{name}_redmod/info.json"));
            await tx.Commit();
        }
        await GameInstallation.GetGame().SortOrderManager.UpdateLoadOrders(loadout.LoadoutId, token: TestContext.Current.CancellationToken);

        var variety = GameInstallation.GetGame().SortOrderManager.GetSortOrderVarieties().OfType<RedModSortOrderVariety>().Single();
        var sortOrder = variety.GetSortOrderIdFor(loadout.LoadoutId, Connection.Db).Value;
        return (loadout, container, g, name => variety.GetSortOrderItems(sortOrder, Connection.Db).Single(item => item.Key.Key == $"{name}_redmod").SortIndex);
    }

    [Fact]
    public async Task TheRedModOrderFollowsTheImportedModOrder()
    {
        var (loadout, container, g, indexOf) = await SeedRedMods();

        // REDmod loads first-listed first and the first one wins, so the winner takes the smaller index.
        // Both directions, so the result cannot be the order the variety started in.
        await Apply(loadout, container, (g["a"], null), (g["b"], null));
        (indexOf("a"), indexOf("b")).Should().Be((0, 1));

        await Apply(loadout, container, (g["b"], null), (g["a"], null));
        (indexOf("b"), indexOf("a")).Should().Be((0, 1));
    }

    [Fact]
    public async Task TheRedModOrderFollowsTheResolvedOrderWhereAStoredRuleOverrodeTheImport()
    {
        var (loadout, container, g, indexOf) = await SeedRedMods();
        await Apply(loadout, container, (g["a"], null), (g["b"], null));
        await LoadoutManager.AddGroupRule(loadout.LoadoutId, g["a"], g["b"], GroupRuleKind.After);

        await Apply(loadout, container, (g["b"], null), (g["a"], null));

        PriorityOf(g["a"]).Should().BeGreaterThan(PriorityOf(g["b"]));
        (indexOf("a"), indexOf("b")).Should().Be((0, 1), "the rule puts a over b, and the variety agrees with the priority rows");
    }

    [Fact]
    public async Task ASourceThatCarriesItsOwnVarietyOrdersIsNotSeeded()
    {
        var (loadout, container, g, indexOf) = await SeedRedMods();
        await Apply(loadout, container, (g["a"], null), (g["b"], null));
        var before = (indexOf("a"), indexOf("b"));

        using var tx = Connection.BeginTransaction();
        Sink.Apply(Refresh(loadout), ModOrder.Create([new ModOrderEntry(g["b"], null), new ModOrderEntry(g["a"], null)], []),
            new ModOrderSource("collection Test", container, null, SeedVarieties: false), tx);
        await tx.Commit();

        PriorityOf(g["b"]).Should().BeGreaterThan(PriorityOf(g["a"]), "the priority rows still follow the order");
        (indexOf("a"), indexOf("b")).Should().Be(before);
    }
}
