using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.RedEngine.Cyberpunk2077.SortOrder;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
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

    private Task<IReadOnlyList<ModOrderDrop>> Apply(Loadout.ReadOnly loadout, LoadoutItemGroupId container, params (LoadoutItemGroupId Group, bool? Enabled)[] winnerToLoser) =>
        ApplyRun(loadout, container, newGroups: null, winnerToLoser);

    /// <summary>A run that added only <paramref name="newGroups"/> to the container; null is a run where every group is new.</summary>
    private async Task<IReadOnlyList<ModOrderDrop>> ApplyRun(Loadout.ReadOnly loadout, LoadoutItemGroupId container, IReadOnlySet<LoadoutItemGroupId>? newGroups, params (LoadoutItemGroupId Group, bool? Enabled)[] winnerToLoser)
    {
        using var tx = Connection.BeginTransaction();
        var drops = Sink.Apply(Refresh(loadout), ModOrder.Create(winnerToLoser.Select(e => new ModOrderEntry(e.Group, e.Enabled)), []),
            new ModOrderSource("collection Test", container, null, NewGroups: newGroups), tx);
        await tx.Commit();
        return drops;
    }

    /// <summary>The user drags <paramref name="winner"/> above <paramref name="loser"/> on File Conflicts.</summary>
    private async Task UserMovesAbove(LoadoutId loadoutId, LoadoutItemGroupId winner, LoadoutItemGroupId loser)
    {
        var rows = LoadoutItemGroupPriority.FindByLoadout(Connection.Db, loadoutId).ToDictionary(r => r.TargetId, r => r.LoadoutItemGroupPriorityId);
        (await LoadoutManager.ResolveFileConflicts([rows[winner]], rows[loser])).IsRuleViolation.Should().BeFalse();
    }

    private string[] ChildrenWinnerFirst(LoadoutItemGroupId container) =>
        ContainerOrder.ChildrenWinnerFirst(Connection.Db, container).Select(child => child.AsLoadoutItem().Name).ToArray();

    /// <summary>A mod installed after the order was applied: its row goes above every other.</summary>
    private async Task<LoadoutItemGroupId> InstallOnTop(LoadoutId loadoutId, string name, LoadoutItemGroupId? parent = null)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadoutId, name, parent);
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
    public async Task AnOrderWhoseGroupsAreAllNewPutsTheBlockInTheListedOrder()
    {
        var (loadout, container, g) = await Seed("a", "b");
        await Apply(loadout, container, (g["a"], null), (g["b"], null));
        await UserMovesAbove(loadout.LoadoutId, g["b"], g["a"]);
        PriorityOf(g["b"]).Should().BeGreaterThan(PriorityOf(g["a"]));

        // A first install, a reinstall into a new group and a Wabbajack list bring every group new: the listed order is the starting order.
        await Apply(loadout, container, (g["a"], null), (g["b"], null));

        PriorityOf(g["a"]).Should().BeGreaterThan(PriorityOf(g["b"]));
    }

    [Fact]
    public async Task ARunThatAddsNoGroupKeepsAReorderTheUserMadeInsideTheBlock()
    {
        var (loadout, container, g) = await Seed("a", "b");
        await Apply(loadout, container, (g["a"], null), (g["b"], null));
        await UserMovesAbove(loadout.LoadoutId, g["b"], g["a"]);

        // "Install optional mods" runs the collection's install again over what is there; the user's order stays.
        await ApplyRun(loadout, container, new HashSet<LoadoutItemGroupId>(), (g["a"], false), (g["b"], null));

        PriorityOf(g["b"]).Should().BeGreaterThan(PriorityOf(g["a"]));
        IsDisabled(g["a"]).Should().BeFalse("the key's switch is only applied to groups the run added");
    }

    [Fact]
    public async Task ARunThatAddsAGroupLeavesTheUsersOwnModsAboveTheBlock()
    {
        var (loadout, container, g) = await Seed("a", "b");
        await Apply(loadout, container, (g["a"], null), (g["b"], null));
        g["x"] = await InstallOnTop(loadout.LoadoutId, "x");
        g["n"] = await InstallOnTop(loadout.LoadoutId, "n", container);

        await ApplyRun(loadout, container, new HashSet<LoadoutItemGroupId> { g["n"] }, (g["a"], null), (g["n"], null), (g["b"], null));

        PriorityOf(g["x"]).Should().BeGreaterThan(new[] { "a", "n", "b" }.Max(name => PriorityOf(g[name])), "the new group's top slot is not the block's to hand out");
        PriorityOf(g["a"]).Should().BeGreaterThan(PriorityOf(g["n"]));
        PriorityOf(g["n"]).Should().BeGreaterThan(PriorityOf(g["b"]));
    }

    [Fact]
    public async Task ARunThatAddsAGroupKeepsTheUsersOrderAndSwitchesAndSlotsTheNewGroupWhereItIsListed()
    {
        var (loadout, container, g) = await Seed("a", "b", "c", "n");
        await Apply(loadout, container, (g["a"], null), (g["b"], null), (g["c"], null));
        await UserMovesAbove(loadout.LoadoutId, g["c"], g["a"]);
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(g["b"], LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }
        ChildrenWinnerFirst(container).Take(3).Should().Equal("c", "a", "b");

        // The collection lists n between a and b, switched off, and b switched on.
        await ApplyRun(loadout, container, new HashSet<LoadoutItemGroupId> { g["n"] }, (g["a"], null), (g["n"], false), (g["b"], true), (g["c"], null));

        ChildrenWinnerFirst(container).Should().Equal("c", "a", "n", "b");
        IsDisabled(g["b"]).Should().BeTrue("the user's switch on an existing group stays");
        IsDisabled(g["n"]).Should().BeTrue("the new group takes the key's switch");
    }

    [Fact]
    public async Task ARuleOverrideComesBackAsADrop()
    {
        var (loadout, container, g) = await Seed("a", "b");
        await Apply(loadout, container, (g["a"], null), (g["b"], null));
        await LoadoutManager.AddGroupRule(loadout.LoadoutId, g["a"], g["b"], GroupRuleKind.After);

        var drops = await Apply(loadout, container, (g["b"], null), (g["a"], null));

        // A stored rule is edited on File Conflicts on every game, so the drop says so whatever the sink's own page is.
        drops.Should().ContainSingle().Which.Should().Be(new ModOrderDrop("the listed order of b and a", "the stored rule 'a wins over b' puts them the other way round", "File Conflicts"));
        PriorityOf(g["a"]).Should().BeGreaterThan(PriorityOf(g["b"]));
    }

    [Fact]
    public async Task ADisplacedPairIsPartOfTheDropOfTheRuleThatMovedIt()
    {
        var (loadout, container, g) = await Seed("w", "y", "u", "z");
        await Apply(loadout, container, (g["z"], null), (g["u"], null), (g["y"], null), (g["w"], null));
        await LoadoutManager.AddGroupRule(loadout.LoadoutId, g["z"], g["u"], GroupRuleKind.After);

        var drops = await Apply(loadout, container, (g["w"], null), (g["y"], null), (g["z"], null));

        drops.Should().Equal(new ModOrderDrop("the listed order of y and z", "they were moved to make room for the stored rule 'z wins over u'", "File Conflicts"));
    }

    /// <summary>A collection container with the named mods in it; a rule that names the collection names each of them.</summary>
    private async Task<(Loadout.ReadOnly Loadout, LoadoutItemGroupId Container, Dictionary<string, LoadoutItemGroupId> Groups)> SeedCollection(params string[] names)
    {
        var loadout = await CreateLoadout();
        using var tx = Connection.BeginTransaction();
        var container = new CollectionGroup.New(tx, out var id)
        {
            IsReadOnly = false,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id) { Name = "Pack", LoadoutId = loadout.LoadoutId },
            },
        };
        var parent = LoadoutItemGroupId.From(container.Id.Value);
        var added = names.ToDictionary(name => name, name => AddEmptyGroup(tx, loadout.LoadoutId, name, parent));
        var result = await tx.Commit();
        return (loadout, LoadoutItemGroupId.From(result[container.Id].Value), added.ToDictionary(p => p.Key, p => LoadoutItemGroupId.From(result[p.Value].Value)));
    }

    [Fact]
    public async Task ARuleThatTurnsRoundSeveralPairsIsOneDropNamingWhatItMoved()
    {
        var (loadout, container, g) = await SeedCollection("x", "a", "b", "c");
        await Apply(loadout, container, (g["a"], null), (g["b"], null), (g["c"], null), (g["x"], null));
        // The collection wins over x: every other mod of it must sit above x.
        await LoadoutManager.AddGroupRule(loadout.LoadoutId, container, g["x"], GroupRuleKind.After);

        var drops = await Apply(loadout, container, (g["x"], null), (g["a"], null), (g["b"], null), (g["c"], null));

        drops.Should().Equal(new ModOrderDrop("the listed order of x, a, b and c", "the stored rule 'Pack wins over x' puts them the other way round", "File Conflicts"));
    }

    [Fact]
    public async Task ALongListOfMovedModsIsCut()
    {
        string[] mods = ["a", "b", "c", "d", "e", "f", "g"];
        var (loadout, container, g) = await SeedCollection(["x", ..mods]);
        await Apply(loadout, container, [..mods.Select(name => (g[name], (bool?)null)), (g["x"], null)]);
        await LoadoutManager.AddGroupRule(loadout.LoadoutId, container, g["x"], GroupRuleKind.After);

        var drops = await Apply(loadout, container, [(g["x"], null), ..mods.Select(name => (g[name], (bool?)null))]);

        drops.Should().ContainSingle().Which.Item.Should().Be("the listed order of x, a, b, c, d and 3 more");
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
