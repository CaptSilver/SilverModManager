using FluentAssertions;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests;

/// <summary>
/// The ordering rules a collection ships with end up as editable group rules on the loadout.
/// </summary>
public class CollectionRuleTests(ITestOutputHelper helper) : ANexusCollectionTest<CollectionRuleTests>(helper)
{
    [Fact]
    public async Task ACollectionRuleBecomesOneGroupRuleAndTheMatchingOrder()
    {
        var loadout = await CreateLoadout();
        var collection = await AddNexusCollection(loadout, "Coll", collectionId: 1, revisionId: 11, childCount: 2);
        var (first, second) = await PrioritiseChildren(loadout, collection);
        // "mod 0 after mod 1" puts mod 0 above mod 1, against the install order.
        await AddDownloadRule("Coll mod 0", "Coll mod 1", CollectionDownloadRuleType.After);

        await LoadoutManager.ApplyCollectionDownloadRules(NexusCollectionLoadoutGroupId.From(collection.Id.Value));

        var rule = LoadoutGroupRule.FindByLoadout(Connection.Db, loadout).Should().ContainSingle().Subject;
        (rule.SourceId, rule.OtherId, rule.Kind).Should().Be((first, second, GroupRuleKind.After));
        OrderedNames(loadout).Should().Equal("Coll mod 1", "Coll mod 0");
    }

    [Fact]
    public async Task ApplyingTheRulesAgainDoesNotDuplicateTheRow()
    {
        var loadout = await CreateLoadout();
        var collection = await AddNexusCollection(loadout, "Coll", collectionId: 1, revisionId: 11, childCount: 2);
        await PrioritiseChildren(loadout, collection);
        await AddDownloadRule("Coll mod 0", "Coll mod 1", CollectionDownloadRuleType.After);
        var id = NexusCollectionLoadoutGroupId.From(collection.Id.Value);

        await LoadoutManager.ApplyCollectionDownloadRules(id);
        await LoadoutManager.ApplyCollectionDownloadRules(id);

        LoadoutGroupRule.FindByLoadout(Connection.Db, loadout).Should().ContainSingle();
        OrderedNames(loadout).Should().Equal("Coll mod 1", "Coll mod 0");
    }

    [Fact]
    public async Task ACollectionRuleSurvivesAnotherRuleBeingAdded()
    {
        var loadout = await CreateLoadout();
        var collection = await AddNexusCollection(loadout, "Coll", collectionId: 1, revisionId: 11, childCount: 3);
        var (first, _) = await PrioritiseChildren(loadout, collection);
        await AddDownloadRule("Coll mod 0", "Coll mod 1", CollectionDownloadRuleType.After);
        await LoadoutManager.ApplyCollectionDownloadRules(NexusCollectionLoadoutGroupId.From(collection.Id.Value));
        var third = ChildGroup("Coll mod 2");

        // Pushing the third group above the first would otherwise be free to reorder the first two.
        var result = await LoadoutManager.AddGroupRule(loadout, third, first, GroupRuleKind.After);

        result.IsCycle.Should().BeFalse();
        var order = OrderedNames(loadout);
        order.IndexOf("Coll mod 0").Should().BeGreaterThan(order.IndexOf("Coll mod 1"));
        order.IndexOf("Coll mod 2").Should().BeGreaterThan(order.IndexOf("Coll mod 0"));
        LoadoutGroupRule.FindByLoadout(Connection.Db, loadout).Should().HaveCount(2);
    }

    [Fact]
    public async Task ARuleNamingADownloadThatIsNotInstalledIsSkipped()
    {
        var loadout = await CreateLoadout();
        var collection = await AddNexusCollection(loadout, "Coll", collectionId: 1, revisionId: 11, childCount: 2);
        await PrioritiseChildren(loadout, collection);
        await AddDownloadWithoutGroup(collection, "Coll missing");
        await AddDownloadRule("Coll mod 0", "Coll missing", CollectionDownloadRuleType.After);

        await LoadoutManager.ApplyCollectionDownloadRules(NexusCollectionLoadoutGroupId.From(collection.Id.Value));

        LoadoutGroupRule.FindByLoadout(Connection.Db, loadout).Should().BeEmpty();
        OrderedNames(loadout).Should().Equal("Coll mod 0", "Coll mod 1");
    }

    [Fact]
    public async Task RulesThatContradictARuleOnTheLoadoutAreRefusedAndNothingIsWritten()
    {
        var loadout = await CreateLoadout();
        var collection = await AddNexusCollection(loadout, "Coll", collectionId: 1, revisionId: 11, childCount: 2);
        var (first, second) = await PrioritiseChildren(loadout, collection);
        // The player put mod 1 above mod 0; the collection wants the opposite.
        (await LoadoutManager.AddGroupRule(loadout, second, first, GroupRuleKind.After)).IsCycle.Should().BeFalse();
        await AddDownloadRule("Coll mod 0", "Coll mod 1", CollectionDownloadRuleType.After);

        var act = async () => await LoadoutManager.ApplyCollectionDownloadRules(NexusCollectionLoadoutGroupId.From(collection.Id.Value));

        await act.Should().ThrowAsync<CollectionRulesCycleException>();
        var rule = LoadoutGroupRule.FindByLoadout(Connection.Db, loadout).Should().ContainSingle().Subject;
        (rule.SourceId, rule.OtherId).Should().Be((second, first));
        OrderedNames(loadout).Should().Equal("Coll mod 0", "Coll mod 1");
    }

    [Fact]
    public async Task ACollectionWhoseRulesContradictEachOtherIsRefused()
    {
        var loadout = await CreateLoadout();
        var collection = await AddNexusCollection(loadout, "Coll", collectionId: 1, revisionId: 11, childCount: 2);
        await PrioritiseChildren(loadout, collection);
        await AddDownloadRule("Coll mod 0", "Coll mod 1", CollectionDownloadRuleType.After);
        await AddDownloadRule("Coll mod 1", "Coll mod 0", CollectionDownloadRuleType.After);

        var act = async () => await LoadoutManager.ApplyCollectionDownloadRules(NexusCollectionLoadoutGroupId.From(collection.Id.Value));

        await act.Should().ThrowAsync<CollectionRulesCycleException>();
        LoadoutGroupRule.FindByLoadout(Connection.Db, loadout).Should().BeEmpty();
    }

    private async Task<(LoadoutItemGroupId First, LoadoutItemGroupId Second)> PrioritiseChildren(LoadoutId loadout, NexusCollectionLoadoutGroup.ReadOnly collection)
    {
        var ids = ChildGroups(collection.Id).OrderBy(static c => c.Name).Select(static c => c.Id).ToArray();
        foreach (var id in ids) await AddPriority(loadout, id);
        return (ids[0], ids[1]);
    }

    private List<(string Name, LoadoutItemGroupId Id)> ChildGroups(EntityId collection)
    {
        var db = Connection.Db;
        return Connection.Query<EntityId>($"SELECT Id FROM mdb_NexusCollectionItemLoadoutGroup(Db => {db}) WHERE Parent = {collection}")
            .Select(id => (LoadoutItem.Load(db, id).Name, LoadoutItemGroupId.From(id.Value)))
            .ToList();
    }

    private LoadoutItemGroupId ChildGroup(string name)
    {
        var db = Connection.Db;
        return Connection.Query<EntityId>($"SELECT Id FROM mdb_NexusCollectionItemLoadoutGroup(Db => {db})")
            .Where(id => LoadoutItem.Load(db, id).Name == name)
            .Select(static id => LoadoutItemGroupId.From(id.Value))
            .Single();
    }

    private List<string> OrderedNames(LoadoutId loadout) => LoadoutItemGroupPriority
        .FindByLoadout(Connection.Db, loadout)
        .OrderBy(static p => p.Priority)
        .Select(static p => p.Target.AsLoadoutItem().Name)
        .ToList();

    private EntityId DownloadNamed(string name)
    {
        var db = Connection.Db;
        return Connection.Query<EntityId>($"SELECT Id FROM mdb_CollectionDownload(Db => {db})")
            .Single(id => CollectionDownload.Name.Get(CollectionDownload.Load(db, id)) == name);
    }

    private async Task AddDownloadWithoutGroup(NexusCollectionLoadoutGroup.ReadOnly collection, string name)
    {
        using var tx = Connection.BeginTransaction();
        _ = new CollectionDownload.New(tx)
        {
            Name = name,
            IsOptional = false,
            ArrayIndex = 99,
            CollectionRevisionId = NexusCollectionLoadoutGroup.RevisionId.Get(collection),
        };
        await tx.Commit();
    }

    private async Task AddDownloadRule(string source, string other, CollectionDownloadRuleType type)
    {
        using var tx = Connection.BeginTransaction();
        _ = new CollectionDownloadRules.New(tx)
        {
            SourceId = CollectionDownloadId.From(DownloadNamed(source).Value),
            OtherId = CollectionDownloadId.From(DownloadNamed(other).Value),
            RuleType = type,
            ArrayIndex = 0,
        };
        await tx.Commit();
    }
}
