using FluentAssertions;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests;

public class CollectionMovePlanTests(ITestOutputHelper helper) : ANexusCollectionTest<CollectionMovePlanTests>(helper)
{
    private const string ModlistName = "Modlist";

    private LoadoutId _loadout;
    private CollectionGroupId _myMods;
    private CollectionGroupId _tweaks;
    private CollectionGroupId _modlist;
    private NexusCollectionLoadoutGroup.ReadOnly _nexus;

    private ICollectionOwnership Ownership { get; } = new CollectionOwnership([new NexusCollectionLock(), new NamedLock(ModlistName)]);

    private async Task Seed()
    {
        _loadout = await CreateLoadout();
        _myMods = DefaultCollection.Of(Connection.Db, _loadout)!.Value;
        _tweaks = (await CreateCollection(_loadout, "Tweaks")).CollectionGroupId;
        _modlist = (await CreateCollection(_loadout, ModlistName)).CollectionGroupId;
        _nexus = await AddNexusCollection(_loadout, "Witchcore", collectionId: 1, revisionId: 11, childCount: 2);
    }

    [Fact]
    public async Task AModMovesIntoAnotherUserCollection()
    {
        await Seed();
        var mod = await AddMod(_loadout, "Tall grass", _myMods);

        var plan = Plan([Item(mod)], _tweaks);

        plan.Movable.Should().Equal(mod);
        plan.Skipped.Should().BeEmpty();
        plan.TargetName.Should().Be("Tweaks");
        plan.RefusalText.Should().BeEmpty();
    }

    [Fact]
    public async Task ANexusCollectionAsTargetSkipsEverythingWithItsReason()
    {
        await Seed();
        var a = await AddMod(_loadout, "A", _myMods);
        var b = await AddMod(_loadout, "B", _tweaks);

        var plan = Plan([Item(a), Item(b)], _nexus.AsCollectionGroup().CollectionGroupId);

        const string reason = "Witchcore is a Nexus collection; it decides what it contains";
        plan.Movable.Should().BeEmpty();
        plan.Skipped.Select(s => s.Reason).Should().Equal(reason, reason);
        plan.RefusalText.Should().Be(reason);
    }

    [Fact]
    public async Task AWabbajackLockedCollectionAsTargetSkipsEverythingWithItsReason()
    {
        await Seed();
        var a = await AddMod(_loadout, "A", _myMods);

        var plan = Plan([Item(a)], _modlist);

        plan.Movable.Should().BeEmpty();
        plan.Skipped.Should().ContainSingle().Which.Reason.Should().Be("Modlist is a Wabbajack modlist; it decides what it contains");
        plan.RefusalText.Should().Be("Modlist is a Wabbajack modlist; it decides what it contains");
    }

    [Fact]
    public async Task AModOfAnotherLoadoutBelongsToThatLoadout()
    {
        await Seed();
        var other = await CreateLoadout();
        var foreign = await AddMod(other, "Foreign", DefaultCollection.Of(Connection.Db, other)!.Value);

        var plan = Plan([Item(foreign)], _tweaks);

        plan.Movable.Should().BeEmpty();
        plan.Skipped.Should().ContainSingle().Which.Reason.Should().Be("Foreign belongs to another loadout");
    }

    [Fact]
    public async Task AModAlreadyInTheTargetIsListedAsSkipped()
    {
        await Seed();
        var mod = await AddMod(_loadout, "Here already", _tweaks);

        var plan = Plan([Item(mod)], _tweaks);

        plan.Movable.Should().BeEmpty();
        plan.Skipped.Should().ContainSingle().Which.Should().Be(new CollectionMoveSkip(Item(mod), "Here already", "Here already is already in Tweaks"));
    }

    [Fact]
    public async Task AModCannotLeaveAWabbajackLockedCollection()
    {
        await Seed();
        var mod = await AddMod(_loadout, "From the list", _modlist);

        var plan = Plan([Item(mod)], _tweaks);

        plan.Movable.Should().BeEmpty();
        plan.Skipped.Should().ContainSingle().Which.Reason.Should().Be("From the list belongs to Modlist, which decides what it contains");
    }

    [Fact]
    public async Task ARequiredAndAnOptionalItemCannotLeaveANexusCollection()
    {
        await Seed();
        var children = Connection.Query<EntityId>($"SELECT Id FROM mdb_LoadoutItemGroup(Db => {Connection.Db}) WHERE Parent = {_nexus.Id}").ToList();
        children.Should().HaveCount(2);
        await MakeOptional(children[1]);

        var plan = Plan(children.Select(id => LoadoutItemId.From(id)).ToList(), _tweaks);

        plan.Movable.Should().BeEmpty();
        plan.Skipped.Should().HaveCount(2);
        plan.Skipped.Select(s => s.Reason).Should().OnlyContain(r =>
            r.EndsWith("belongs to Witchcore, which decides what it contains; make an editable copy of it first"));
    }

    [Fact]
    public async Task TheGamesOwnContentStays()
    {
        await Seed();
        var installed = await AddGameInstalledGroup(_loadout);

        var plan = Plan([Item(installed)], _tweaks);

        plan.Movable.Should().BeEmpty();
        plan.Skipped.Should().ContainSingle().Which.Reason.Should().Be("Installed by the game is part of the game's own content");
    }

    [Fact]
    public async Task AnOverridesGroupIsNotAMod()
    {
        await Seed();
        var overrides = await AddOverridesGroup(_loadout);

        var plan = Plan([Item(overrides)], _tweaks);

        plan.Movable.Should().BeEmpty();
        plan.Skipped.Should().ContainSingle().Which.Reason.Should().Be("Overrides is not a mod");
    }

    [Fact]
    public async Task AGroupThatIsNotInACollectionIsNotAMod()
    {
        await Seed();
        var loose = await AddMod(_loadout, "Loose", parent: null);

        var plan = Plan([Item(loose)], _tweaks);

        plan.Movable.Should().BeEmpty();
        plan.Skipped.Should().ContainSingle().Which.Reason.Should().Be("Loose is not a mod");
    }

    [Fact]
    public async Task AFileIsNotAMod()
    {
        await Seed();
        var mod = await AddMod(_loadout, "Has a file", _myMods);
        using var tx = Connection.BeginTransaction();
        var file = AddFile(tx, _loadout, mod, new GamePath(LocationId.Game, "a.txt"));
        var result = await tx.Commit();

        var plan = Plan([LoadoutItemId.From(result[file.Value].Value)], _tweaks);

        plan.Movable.Should().BeEmpty();
        plan.Skipped.Should().ContainSingle().Which.Reason.Should().EndWith("is not a mod");
    }

    [Fact]
    public async Task AMissingItemIsSkippedNotThrownOn()
    {
        await Seed();
        var gone = LoadoutItemId.From(EntityId.From(0x00FF_FFFF_FFFF_0001UL));

        var plan = Plan([gone], _tweaks);

        plan.Movable.Should().BeEmpty();
        plan.Skipped.Should().ContainSingle().Which.Reason.Should().Be("This item no longer exists");
    }

    [Fact]
    public async Task AMixedSelectionKeepsInputOrderInBothLists()
    {
        await Seed();
        var one = await AddMod(_loadout, "One", _myMods);
        var already = await AddMod(_loadout, "Already", _tweaks);
        var two = await AddMod(_loadout, "Two", _myMods);
        var loose = await AddMod(_loadout, "Loose", parent: null);
        var three = await AddMod(_loadout, "Three", _myMods);

        var plan = Plan([Item(three), Item(loose), Item(one), Item(already), Item(two)], _tweaks);

        plan.Movable.Should().Equal(three, one, two);
        plan.Skipped.Select(s => s.Name).Should().Equal("Loose", "Already");
        plan.RefusalText.Should().BeEmpty();
    }

    [Fact]
    public async Task RefusalTextIsTheSharedReasonOrASummary()
    {
        await Seed();
        var a = await AddMod(_loadout, "A", _myMods);
        var b = await AddMod(_loadout, "B", _myMods);

        Plan([Item(a), Item(b)], _modlist).RefusalText.Should().Be("Modlist is a Wabbajack modlist; it decides what it contains");

        var inTweaks = await AddMod(_loadout, "C", _tweaks);
        var loose = await AddMod(_loadout, "D", parent: null);
        Plan([Item(inTweaks), Item(loose)], _tweaks).RefusalText.Should().Be("None of these mods can move to Tweaks");
    }

    [Fact]
    public async Task AnEmptySelectionHasNothingToMove()
    {
        await Seed();

        var plan = Plan([], _tweaks);

        plan.Movable.Should().BeEmpty();
        plan.Skipped.Should().BeEmpty();
        plan.RefusalText.Should().Be("None of these mods can move to Tweaks");
    }

    private CollectionMovePlan Plan(IReadOnlyList<LoadoutItemId> items, CollectionGroupId target)
        => CollectionMovePlan.Plan(Connection.Db, items, target, Ownership);

    private static LoadoutItemId Item(LoadoutItemGroupId group) => LoadoutItemId.From(group.Value);

    private async Task<LoadoutItemGroupId> AddMod(LoadoutId loadout, string name, CollectionGroupId? parent)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout, name, parent is { } p ? LoadoutItemGroupId.From(p.Value) : null);
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[group].Value);
    }

    private async Task MakeOptional(EntityId child)
    {
        using var tx = Connection.BeginTransaction();
        tx.Add(child, NexusCollectionItemLoadoutGroup.IsRequired, false);
        await tx.Commit();
    }

    private async Task<LoadoutItemGroupId> AddGameInstalledGroup(LoadoutId loadout)
    {
        using var tx = Connection.BeginTransaction();
        var group = ALoadoutSynchronizer.CreateGameInstalledGroup(tx, Loadout.Load(Connection.Db, loadout));
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[group].Value);
    }

    private async Task<LoadoutItemGroupId> AddOverridesGroup(LoadoutId loadout)
    {
        using var tx = Connection.BeginTransaction();
        _ = new LoadoutOverridesGroup.New(tx, out var id)
        {
            OverridesForId = loadout,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id) { Name = "Overrides", LoadoutId = loadout },
            },
        };
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[id].Value);
    }

    /// <summary>Stands in for the Wabbajack lock, which lives in a project the plan's tests don't reference.</summary>
    private sealed class NamedLock(string name) : ICollectionLock
    {
        public string? WhyLocked(CollectionGroup.ReadOnly collection)
        {
            var collectionName = collection.AsLoadoutItemGroup().AsLoadoutItem().Name;
            return collectionName == name ? $"{name} is a Wabbajack modlist; it decides what it contains" : null;
        }
    }
}
