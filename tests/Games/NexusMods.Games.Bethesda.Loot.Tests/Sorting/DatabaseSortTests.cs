using NexusMods.Games.Bethesda.Loot.Database;
using NexusMods.Games.Bethesda.Loot.Metadata;
using NexusMods.Games.Bethesda.Loot.Sorting;

namespace NexusMods.Games.Bethesda.Loot.Tests.Sorting;

// Ports the database-backed sort entry in libloot's game.rs (sort_plugins, to_plugin_sorting_data).
public class DatabaseSortTests
{
    private static LootDatabase Database(string masterlist, string? userlist = null)
    {
        var database = new LootDatabase();
        database.LoadMasterlist(masterlist);
        if (userlist is not null) database.LoadUserlist(userlist);
        return database;
    }

    private static TestPlugin Master(string name) => new(name) { IsMaster = true };

    [Fact]
    public void SortingDataShouldTakeLoadAfterRequirementsAndGroupsFromTheDatabase()
    {
        var database = Database("""
            groups: [{name: late, after: [default]}]
            plugins:
              - name: A.esp
                group: late
                after: [B.esp]
                req: [C.esp]
            """, """
            plugins:
              - name: A.esp
                group: early
                after: [D.esp]
                req: [E.esp]
            """);

        var (data, unevaluated) = PluginSorter.ToPluginSortingData(database, new TestPlugin("A.esp"), 3);

        data.Group.Should().Be("early");
        data.GroupIsUserMetadata.Should().BeTrue();
        data.MasterlistLoadAfter.Should().Equal("B.esp");
        data.UserLoadAfter.Should().Equal("D.esp");
        data.MasterlistRequirements.Should().Equal("C.esp");
        data.UserRequirements.Should().Equal("E.esp");
        data.LoadOrderIndex.Should().Be(3);
        unevaluated.Should().Be(0);
    }

    [Fact]
    public void SortingDataShouldKeepMasterlistAndUserMetadataApart()
    {
        var database = Database("plugins: [{name: A.esp, after: [B.esp]}]");

        var (data, _) = PluginSorter.ToPluginSortingData(database, new TestPlugin("A.esp"), 0);

        data.Group.Should().Be("default");
        data.MasterlistLoadAfter.Should().Equal("B.esp");
        data.UserLoadAfter.Should().BeEmpty();
    }

    [Fact]
    public void SortingDataShouldDropAndCountConditionedAndConstrainedFiles()
    {
        var database = Database("""
            plugins:
              - name: A.esp
                after:
                  - {name: B.esp, condition: 'file("B.esp")'}
                  - {name: C.esp, constraint: 'file("C.esp")'}
                  - D.esp
                req:
                  - {name: E.esp, constraint: 'file("E.esp")'}
            """, """
            plugins:
              - name: A.esp
                after:
                  - {name: F.esp, condition: 'file("F.esp")', constraint: 'file("F.esp")'}
            """);

        var (data, unevaluated) = PluginSorter.ToPluginSortingData(database, new TestPlugin("A.esp"), 0);

        data.MasterlistLoadAfter.Should().Equal("D.esp");
        data.MasterlistRequirements.Should().BeEmpty();
        data.UserLoadAfter.Should().BeEmpty();
        unevaluated.Should().Be(4);
    }

    [Fact]
    public void ShouldSortThroughTheMasterlistAndUserlist()
    {
        var database = Database(
            "plugins: [{name: A.esp, after: [B.esp]}]",
            "plugins: [{name: B.esp, after: [C.esp]}]");

        var result = PluginSorter.Sort<ISortingPlugin>(
            [new TestPlugin("A.esp"), new TestPlugin("B.esp"), new TestPlugin("C.esp")], database, []);

        result.Order.Should().Equal("C.esp", "B.esp", "A.esp");
        result.Divergences.UnevaluatedConditions.Should().Be(0);
    }

    [Fact]
    public void ShouldRejectAMasterListedToLoadAfterANonMaster()
    {
        var database = Database("plugins: [{name: M.esm, after: [N.esp]}]");

        var act = () => PluginSorter.Sort<ISortingPlugin>([new TestPlugin("N.esp"), Master("M.esm")], database, []);

        act.Should().Throw<PluginGraphValidationException>();
    }

    [Fact]
    public void ShouldPlaceEarlyLoadersFirst()
    {
        var database = new LootDatabase();

        var result = PluginSorter.Sort<ISortingPlugin>([new TestPlugin("A.esp"), new TestPlugin("B.esp")], database, ["B.esp"]);

        result.Order.Should().Equal("B.esp", "A.esp");
    }

    [Fact]
    public void ShouldBuildTheGroupsGraphFromMasterlistAndUserGroups()
    {
        var database = Database(
            "groups: [{name: late, after: [default]}]\nplugins: [{name: A.esp, group: late}]",
            "groups: [{name: later, after: [late]}]\nplugins: [{name: B.esp, group: later}]");

        var result = PluginSorter.Sort<ISortingPlugin>(
            [new TestPlugin("B.esp"), new TestPlugin("A.esp"), new TestPlugin("C.esp")], database, []);

        result.Order.Should().Equal("C.esp", "A.esp", "B.esp");
    }

    [Fact]
    public void ShouldThrowForAPluginInAnUndefinedGroup()
    {
        var database = Database("plugins: [{name: A.esp, group: missing}]");

        var act = () => PluginSorter.Sort<ISortingPlugin>([new TestPlugin("A.esp")], database, []);

        act.Should().Throw<UndefinedGroupException>();
    }

    [Fact]
    public void ShouldThrowWhenTheGroupsDoNotFormAGraph()
    {
        var database = Database("groups: [{name: g, after: [missing]}]");

        var act = () => PluginSorter.Sort<ISortingPlugin>([new TestPlugin("A.esp")], database, []);

        act.Should().Throw<BuildGroupsGraphException>();
    }

    [Fact]
    public void ShouldCountConditionedLoadAfterEntriesAndLeaveTheirEdgesOut()
    {
        // With the edges the order would be C, B, A; without them the tie-break keeps the current order.
        var database = Database("""
            plugins:
              - name: A.esp
                after:
                  - {name: B.esp, condition: 'file("B.esp")'}
                  - {name: C.esp, condition: 'file("C.esp")'}
              - name: B.esp
                after:
                  - {name: C.esp, condition: 'active("C.esp")'}
            """);

        var result = PluginSorter.Sort<ISortingPlugin>(
            [new TestPlugin("A.esp"), new TestPlugin("B.esp"), new TestPlugin("C.esp")], database, []);

        result.Divergences.UnevaluatedConditions.Should().Be(3);
        result.Order.Should().Equal("A.esp", "B.esp", "C.esp");
    }

    [Fact]
    public void ShouldReportEveryPluginWithoutOverlapInputsAsOverlapEdgesSkipped()
    {
        var result = PluginSorter.Sort<ISortingPlugin>([new TestPlugin("A.esp"), new TestPlugin("B.esp")], new LootDatabase(), []);

        result.Divergences.OverlapEdgesSkipped.Should().BeTrue();
    }

    [Fact]
    public void ShouldNotReportOverlapEdgesSkippedWhenAPluginHasOverlapInputs()
    {
        var result = PluginSorter.Sort<ISortingPlugin>(
            [new TestPlugin("A.esp") { OverrideRecordCount = 2 }, new TestPlugin("B.esp")], new LootDatabase(), []);

        result.Divergences.OverlapEdgesSkipped.Should().BeFalse();
    }

    [Fact]
    public void ShouldSortNothingToAnEmptyOrderWithoutReportingSkippedOverlap()
    {
        var result = PluginSorter.Sort<ISortingPlugin>([], new LootDatabase(), []);

        result.Order.Should().BeEmpty();
        result.Divergences.Should().Be(new KnownDivergences(false, 0));
    }
}
