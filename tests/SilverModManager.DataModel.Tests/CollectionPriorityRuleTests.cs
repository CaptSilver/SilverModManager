using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests;

/// <summary>
/// A rule can name a collection instead of a mod: it then holds for every mod the collection holds, and a priority
/// move is refused when it breaks such a rule.
/// </summary>
public class CollectionPriorityRuleTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<CollectionPriorityRuleTests>(helper)
{
    [Fact]
    public async Task AnAfterRuleOnACollectionPutsEveryMemberAboveTheMod()
    {
        var loadout = await CreateLoadout();
        var f = await Seed(loadout.LoadoutId);

        var result = await LoadoutManager.AddGroupRule(loadout.LoadoutId, f.Collection, f.Mod, GroupRuleKind.After);

        result.IsCycle.Should().BeFalse();
        var order = NamesByPriority(loadout.LoadoutId);
        order.IndexOf("Coll 1").Should().BeGreaterThan(order.IndexOf("Mod"));
        order.IndexOf("Coll 2").Should().BeGreaterThan(order.IndexOf("Mod"));
        order.IndexOf("Coll 2").Should().BeGreaterThan(order.IndexOf("Coll 1"), "the members keep their order inside the collection");
        LoadoutGroupRule.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().ContainSingle()
            .Which.SourceId.Should().Be(f.Collection);
    }

    [Fact]
    public async Task ABeforeRuleOnAModAgainstACollectionPutsItBelowEveryMember()
    {
        var loadout = await CreateLoadout();
        var f = await Seed(loadout.LoadoutId);

        // Mod sits above the collection's members and has to end up below all of them.
        await LoadoutManager.AddGroupRule(loadout.LoadoutId, f.Mod, f.Collection, GroupRuleKind.Before);

        var order = NamesByPriority(loadout.LoadoutId);
        order.IndexOf("Mod").Should().BeLessThan(order.IndexOf("Coll 1"));
        order.IndexOf("Mod").Should().BeLessThan(order.IndexOf("Coll 2"));
    }

    [Fact]
    public async Task ARuleOnACollectionThatContradictsAStoredOneNamesTheCollection()
    {
        var loadout = await CreateLoadout();
        var f = await Seed(loadout.LoadoutId);
        (await LoadoutManager.AddGroupRule(loadout.LoadoutId, f.Collection, f.Mod, GroupRuleKind.After)).IsCycle.Should().BeFalse();
        var before = NamesByPriority(loadout.LoadoutId);

        var result = await LoadoutManager.AddGroupRule(loadout.LoadoutId, f.Mod, f.Collection, GroupRuleKind.After);

        result.IsCycle.Should().BeTrue();
        new[] { result.Cycle.First.Value, result.Cycle.Second.Value }.Should().BeEquivalentTo([f.Collection.Value, f.Mod.Value]);
        LoadoutGroupRule.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().ContainSingle();
        NamesByPriority(loadout.LoadoutId).Should().Equal(before);
    }

    [Fact]
    public async Task ARuleOnACollectionWithNoModsInTheLoadoutIsRefused()
    {
        var loadout = await CreateLoadout();
        var f = await Seed(loadout.LoadoutId);
        var empty = await AddCollection(loadout.LoadoutId, "Empty");

        var act = async () => await LoadoutManager.AddGroupRule(loadout.LoadoutId, empty, f.Mod, GroupRuleKind.After);

        await act.Should().ThrowAsync<ArgumentException>();
        LoadoutGroupRule.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().BeEmpty();
    }

    [Fact]
    public async Task MovingAMemberAcrossTheModAThatACollectionRuleKeepsAboveIsRefused()
    {
        var loadout = await CreateLoadout();
        var f = await Seed(loadout.LoadoutId);
        await LoadoutManager.AddGroupRule(loadout.LoadoutId, f.Collection, f.Mod, GroupRuleKind.After);
        var before = PriorityRows(loadout.LoadoutId);

        // Sends one member of the collection to the bottom, under the mod.
        var member = LoadoutItemGroupPriority.FindByTarget(Connection.Db, f.Members[1]).Single();
        var result = await LoadoutManager.LoseAllFileConflicts([member.LoadoutItemGroupPriorityId]);

        result.IsRuleViolation.Should().BeTrue();
        result.Rule.SourceId.Should().Be(f.Collection, "the refusal names the rule as it was stored");
        PriorityRows(loadout.LoadoutId).Should().Equal(before);
    }

    [Fact]
    public async Task MovingTheWholeCollectionPastTheModKeepsItsRuleAndIsDone()
    {
        var loadout = await CreateLoadout();
        var f = await Seed(loadout.LoadoutId);
        await LoadoutManager.AddGroupRule(loadout.LoadoutId, f.Collection, f.Mod, GroupRuleKind.After);

        var rows = f.Members.Select(m => LoadoutItemGroupPriority.FindByTarget(Connection.Db, m).Single().LoadoutItemGroupPriorityId).ToArray();
        var result = await LoadoutManager.WinAllFileConflicts(rows);

        result.IsRuleViolation.Should().BeFalse();
        NamesByPriority(loadout.LoadoutId).Should().Equal("Mod", "Other", "Coll 1", "Coll 2");
    }

    [Fact]
    public async Task ACollectionRuleStillHoldsAfterAnotherModIsRemoved()
    {
        var loadout = await CreateLoadout();
        var f = await Seed(loadout.LoadoutId);
        await LoadoutManager.AddGroupRule(loadout.LoadoutId, f.Collection, f.Mod, GroupRuleKind.After);

        // A removal resolves the rules again; the members must still be above the mod afterwards.
        await LoadoutManager.RemoveItems([f.Other]);

        var order = NamesByPriority(loadout.LoadoutId);
        order.Should().NotContain("Other");
        order.IndexOf("Coll 1").Should().BeGreaterThan(order.IndexOf("Mod"));
        order.IndexOf("Coll 2").Should().BeGreaterThan(order.IndexOf("Mod"));
    }

    private sealed record Fixture(LoadoutItemGroupId Collection, LoadoutItemGroupId[] Members, LoadoutItemGroupId Mod, LoadoutItemGroupId Other);

    /// <summary>Priority order, lowest first: Coll 1, Mod, Coll 2, Other.</summary>
    private async Task<Fixture> Seed(LoadoutId loadoutId)
    {
        using var tx = Connection.BeginTransaction();
        _ = new CollectionGroup.New(tx, out var collectionId)
        {
            IsReadOnly = true,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, collectionId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, collectionId) { Name = "Collection", LoadoutId = loadoutId },
            },
        };
        var parent = LoadoutItemGroupId.From(collectionId.Value);
        var names = new (string Name, LoadoutItemGroupId? Parent)[] { ("Coll 1", parent), ("Mod", null), ("Coll 2", parent), ("Other", null) };
        var added = names.Select((n, i) =>
        {
            var group = AddEmptyGroup(tx, loadoutId, n.Name, n.Parent);
            _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadoutId, TargetId = group, Priority = ConflictPriority.From((ulong)(i + 1) * 10) };
            return group;
        }).ToArray();
        var result = await tx.Commit();

        LoadoutItemGroupId Map(LoadoutItemGroupId id) => LoadoutItemGroupId.From(result[id.Value].Value);
        return new Fixture(Map(parent), [Map(added[0]), Map(added[2])], Map(added[1]), Map(added[3]));
    }

    private async Task<LoadoutItemGroupId> AddCollection(LoadoutId loadoutId, string name)
    {
        using var tx = Connection.BeginTransaction();
        _ = new CollectionGroup.New(tx, out var id)
        {
            IsReadOnly = true,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id) { Name = name, LoadoutId = loadoutId },
            },
        };
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[id].Value);
    }

    private string[] NamesByPriority(LoadoutId loadoutId) => LoadoutItemGroupPriority
        .FindByLoadout(Connection.Db, loadoutId)
        .OrderBy(p => p.Priority)
        .Select(p => p.Target.AsLoadoutItem().Name)
        .ToArray();

    private (ulong Row, ulong Target, ulong Priority)[] PriorityRows(LoadoutId loadoutId) => LoadoutItemGroupPriority
        .FindByLoadout(Connection.Db, loadoutId)
        .OrderBy(p => p.Id)
        .Select(p => ((ulong)p.Id.Value, (ulong)p.TargetId.Value, (ulong)p.Priority.Value))
        .ToArray();
}
