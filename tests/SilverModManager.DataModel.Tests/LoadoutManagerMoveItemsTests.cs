using FluentAssertions;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;

namespace SilverModManager.DataModel.Tests;

public class LoadoutManagerMoveItemsTests(ITestOutputHelper helper) : ANexusCollectionTest<LoadoutManagerMoveItemsTests>(helper)
{
    private LoadoutId _loadout;
    private CollectionGroupId _myMods;
    private CollectionGroupId _tweaks;
    private ulong _nextPriority;

    private async Task Seed()
    {
        _loadout = await CreateLoadout();
        _myMods = DefaultCollection.Of(Connection.Db, _loadout)!.Value;
        _tweaks = (await CreateCollection(_loadout, "Tweaks")).CollectionGroupId;
    }

    [Fact]
    public async Task ModsMoveKeepingTheirEnabledStateAndPlaceInOneTransaction()
    {
        await Seed();
        var enabled = await AddMod("Enabled mod", _myMods);
        var disabled = await AddMod("Disabled mod", _myMods);
        await Disable(disabled);
        var prioritiesBefore = PrioritiesByGroup();
        prioritiesBefore.Should().HaveCount(2);
        var before = Connection.Db.BasisTxId;

        var result = await LoadoutManager.MoveItemsToCollection([Item(enabled), Item(disabled)], _tweaks);

        result.Moved.Should().Equal(enabled, disabled);
        result.Skipped.Should().BeEmpty();
        ParentOf(Connection.Db, enabled).Should().Be(_tweaks.Value);
        ParentOf(Connection.Db, disabled).Should().Be(_tweaks.Value);
        LoadoutItem.Load(Connection.Db, Item(disabled)).IsDisabled.Should().BeTrue();
        LoadoutItem.Load(Connection.Db, Item(enabled)).IsDisabled.Should().BeFalse();
        PrioritiesByGroup().Should().BeEquivalentTo(prioritiesBefore);
        Connection.Db.BasisTxId.Value.Should().Be(before.Value + 1, "the whole move is one transaction");

        var asBefore = Connection.AsOf(before);
        ParentOf(asBefore, enabled).Should().Be(_myMods.Value, "nothing was half applied");
        ParentOf(asBefore, disabled).Should().Be(_myMods.Value);
    }

    [Fact]
    public async Task NothingMovableCommitsNothingAndReturnsTheSkips()
    {
        await Seed();
        var already = await AddMod("Here already", _tweaks);
        var before = Connection.Db.BasisTxId;

        var result = await LoadoutManager.MoveItemsToCollection([Item(already)], _tweaks);

        result.Moved.Should().BeEmpty();
        result.Skipped.Should().ContainSingle().Which.Should().Be(new CollectionMoveSkip(Item(already), "Here already", "Here already is already in Tweaks"));
        Connection.Db.BasisTxId.Should().Be(before, "nothing was written");
    }

    [Fact]
    public async Task AMixedSelectionMovesWhatItCanAndReportsTheRest()
    {
        await Seed();
        var movable = await AddMod("Movable", _myMods);
        var already = await AddMod("Here already", _tweaks);

        var result = await LoadoutManager.MoveItemsToCollection([Item(already), Item(movable)], _tweaks);

        result.Moved.Should().Equal(movable);
        result.Skipped.Select(static skip => skip.Name).Should().Equal("Here already");
        ParentOf(Connection.Db, movable).Should().Be(_tweaks.Value);
    }

    [Fact]
    public async Task AMoveThatMakesTheStoredRulesCycleThrowsAndMovesNothing()
    {
        await Seed();
        var mod = await AddMod("Mod", _myMods);
        var other = await AddMod("Other", _myMods);
        // Once the mod is in Tweaks it must follow Other, and Other already has to follow it.
        await AddRule(_tweaks.Value, other.Value, GroupRuleKind.After);
        await AddRule(other.Value, mod.Value, GroupRuleKind.After);
        var before = Connection.Db.BasisTxId;

        var move = async () => await LoadoutManager.MoveItemsToCollection([Item(mod)], _tweaks);

        await move.Should().ThrowAsync<InvalidOperationException>();
        Connection.Db.BasisTxId.Should().Be(before, "the failed transaction wrote nothing");
        ParentOf(Connection.Db, mod).Should().Be(_myMods.Value);
    }

    private static LoadoutItemId Item(LoadoutItemGroupId group) => LoadoutItemId.From(group.Value);

    private static EntityId ParentOf(IDb db, LoadoutItemGroupId group) => LoadoutItem.Load(db, Item(group)).ParentId.Value;

    private Dictionary<LoadoutItemGroupId, ConflictPriority> PrioritiesByGroup() => LoadoutItemGroupPriority
        .FindByLoadout(Connection.Db, _loadout)
        .ToDictionary(static row => row.TargetId, static row => row.Priority);

    private async Task<LoadoutItemGroupId> AddMod(string name, CollectionGroupId parent)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, _loadout, name, LoadoutItemGroupId.From(parent.Value));
        // A group inside a collection has no row of its own from AddEmptyGroup; an installed mod does. Dense, as the resolver writes them.
        _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = _loadout, TargetId = group, Priority = ConflictPriority.From(++_nextPriority) };
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[group].Value);
    }

    private async Task Disable(LoadoutItemGroupId group)
    {
        using var tx = Connection.BeginTransaction();
        tx.Add(group.Value, LoadoutItem.Disabled, Null.Instance);
        await tx.Commit();
    }

    private async Task AddRule(EntityId source, EntityId other, GroupRuleKind kind)
    {
        using var tx = Connection.BeginTransaction();
        _ = new LoadoutGroupRule.New(tx) { LoadoutId = _loadout, SourceId = LoadoutItemGroupId.From(source.Value), OtherId = LoadoutItemGroupId.From(other.Value), Kind = kind };
        await tx.Commit();
    }
}
