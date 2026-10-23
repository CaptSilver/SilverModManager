using SilverModManager.Games.Bethesda.Loot.Metadata;
using SilverModManager.Games.Bethesda.Loot.Sorting;

namespace SilverModManager.Games.Bethesda.Loot.Tests.Sorting;

// Ports the tests in libloot's sorting/groups.rs, plus coverage for the traversal helpers on GroupsGraph.
public class GroupsGraphTests
{
    private static Group G(string name, params string[] after) => new(name) { AfterGroups = after };

    private static Vertex V(string name, EdgeType? edge = null) => new(name, edge);

    #region Build

    [Fact]
    public void BuildShouldErrorIfAnAfterGroupDoesNotExist()
    {
        var act = () => GroupsGraph.Build([G("b", "a")], []);

        act.Should().Throw<BuildGroupsGraphException>()
            .Which.UndefinedGroup!.GroupName.Should().Be("a");
    }

    [Fact]
    public void BuildShouldErrorIfMasterlistGroupLoadsAfterUserGroup()
    {
        var act = () => GroupsGraph.Build([G("b", "a")], [G("a")]);

        act.Should().Throw<BuildGroupsGraphException>()
            .Which.UndefinedGroup!.GroupName.Should().Be("a");
    }

    [Fact]
    public void BuildShouldErrorIfAfterGroupsAreCyclic()
    {
        var masterlist = new[] { G("a"), G("b", "a") };
        var userlist = new[] { G("a", "c"), G("c", "b") };

        var act = () => GroupsGraph.Build(masterlist, userlist);

        act.Should().Throw<BuildGroupsGraphException>()
            .Which.Cycle!.Cycle.Should().Equal(
                V("a", EdgeType.MasterlistLoadAfter),
                V("b", EdgeType.UserLoadAfter),
                V("c", EdgeType.UserLoadAfter));
    }

    [Fact]
    public void CyclicInteractionErrorShouldOnlyIncludeGroupsThatArePartOfTheCycle()
    {
        var masterlist = new[] { G("a", "b"), G("b", "a"), G("c", "b") };

        var act = () => GroupsGraph.Build(masterlist, []);

        act.Should().Throw<BuildGroupsGraphException>()
            .Which.Cycle!.Cycle.Should().Equal(
                V("a", EdgeType.MasterlistLoadAfter),
                V("b", EdgeType.MasterlistLoadAfter));
    }

    [Fact]
    public void BuildShouldLetAUserEdgeReplaceAMasterlistEdgeBetweenTheSameGroups()
    {
        var graph = GroupsGraph.Build([G("a"), G("b", "a")], [G("b", "a")]);

        graph.FindPath("a", "b").Should().Equal(V("a", EdgeType.UserLoadAfter), V("b"));
    }

    #endregion

    #region FindPath

    [Fact]
    public void FindPathShouldErrorIfTheFromGroupDoesNotExist()
    {
        var graph = GroupsGraph.Build([G("a"), G("b", "a")], []);

        var act = () => graph.FindPath("c", "a");

        act.Should().Throw<GroupsPathException>().Which.Kind.Should().Be(GroupsPathExceptionKind.UndefinedGroup);
    }

    [Fact]
    public void FindPathShouldErrorIfTheToGroupDoesNotExist()
    {
        var graph = GroupsGraph.Build([G("a"), G("b", "a")], []);

        var act = () => graph.FindPath("a", "c");

        act.Should().Throw<GroupsPathException>().Which.GroupName.Should().Be("c");
    }

    [Fact]
    public void FindPathShouldReturnAnEmptyListIfThereIsNoPath()
    {
        var graph = GroupsGraph.Build([G("a"), G("b")], []);

        graph.FindPath("a", "b").Should().BeEmpty();
    }

    [Fact]
    public void FindPathShouldFindTheShortestPathIfThereIsNoUserMetadata()
    {
        var masterlist = new[] { G("a"), G("b", "a"), G("c", "a"), G("d", "c"), G("e", "b", "d") };
        var graph = GroupsGraph.Build(masterlist, []);

        graph.FindPath("a", "e").Should().Equal(
            V("a", EdgeType.MasterlistLoadAfter),
            V("b", EdgeType.MasterlistLoadAfter),
            V("e"));
    }

    [Fact]
    public void FindPathShouldPreferThePathThroughUserMetadata()
    {
        var masterlist = new[] { G("a"), G("b", "a"), G("c", "a"), G("e", "b") };
        var userlist = new[] { G("d", "c"), G("e", "d") };
        var graph = GroupsGraph.Build(masterlist, userlist);

        graph.FindPath("a", "e").Should().Equal(
            V("a", EdgeType.MasterlistLoadAfter),
            V("c", EdgeType.UserLoadAfter),
            V("d", EdgeType.UserLoadAfter),
            V("e"));
    }

    [Fact]
    public void FindPathShouldNotDependOnTheAfterGroupDefinitionOrder()
    {
        var masterlists = new[]
        {
            new[] { G("a"), G("b", "a"), G("c", "a"), G("d", "b", "c"), G("e", "d") },
            new[] { G("a"), G("b", "a"), G("c", "a"), G("d", "c", "b"), G("e", "d") },
        };

        foreach (var masterlist in masterlists)
        {
            GroupsGraph.Build(masterlist, []).FindPath("a", "e").Should().Equal(
                V("a", EdgeType.MasterlistLoadAfter),
                V("b", EdgeType.MasterlistLoadAfter),
                V("d", EdgeType.MasterlistLoadAfter),
                V("e"));
        }
    }

    #endregion

    #region Traversal

    private static string[] Names(GroupsGraph graph, IEnumerable<int> nodes) => nodes.Select(graph.NodeName).ToArray();

    [Fact]
    public void SortedGroupNodesBreakTiesByInsertionOrder()
    {
        // The user-only group sorts before both masterlist groups by name but is added after them,
        // and every group is a root with the same path length.
        var graph = GroupsGraph.Build([G("m"), G("default")], [G("a")]);

        Names(graph, graph.SortedGroupNodes()).Should().Equal("default", "m", "a");
    }

    [Fact]
    public void SortedGroupNodesPutRootsFirstByDescendingPathLength()
    {
        var graph = GroupsGraph.Build([G("x"), G("y", "x")], [G("a")]);

        // x is a root with a path of two groups, a is a root with one, y is not a root.
        Names(graph, graph.SortedGroupNodes()).Should().Equal("x", "a", "y");
    }

    [Fact]
    public void SortedGroupNodesUseTheLongestPathFromEachRoot()
    {
        var graph = GroupsGraph.Build([G("a"), G("b"), G("c", "b"), G("d", "c"), G("e", "a")], []);

        Names(graph, graph.SortedGroupNodes()).Should().Equal("b", "a", "c", "d", "e");
    }

    [Fact]
    public void DefaultGroupNodeShouldFindTheDefaultGroup()
    {
        var graph = GroupsGraph.Build([G("a"), G("default", "a")], []);

        graph.NodeName(graph.DefaultGroupNode()).Should().Be("default");
    }

    [Fact]
    public void DefaultGroupNodeShouldErrorIfThereIsNoDefaultGroup()
    {
        var graph = GroupsGraph.Build([G("a")], []);

        var act = graph.DefaultGroupNode;

        act.Should().Throw<UndefinedGroupException>().Which.GroupName.Should().Be("default");
    }

    [Fact]
    public void NodeByNameShouldErrorForAnUnknownGroup()
    {
        var graph = GroupsGraph.Build([G("a")], []);

        graph.NodeByName("a").Should().Be(0);
        var act = () => graph.NodeByName("b");

        act.Should().Throw<UndefinedGroupException>().Which.Message.Should().Be("the group \"b\" does not exist");
    }

    [Fact]
    public void SortedByNameShouldOrderByCodePointLikeRustStrings()
    {
        // U+FF5E sorts after U+1F600 in UTF-16 order but before it in UTF-8 and code point order.
        var graph = GroupsGraph.Build([G(char.ConvertFromUtf32(0x1F600)), G("\uFF5E")], []);

        Names(graph, graph.SortedGroupNodes()).Should().Equal("\uFF5E", char.ConvertFromUtf32(0x1F600));
    }

    #endregion

    #region Exceptions and display

    [Fact]
    public void DisplayCycleShouldJoinVerticesWithEdgeTypesAndRepeatTheFirst()
    {
        var cycle = new[] { V("a", EdgeType.MasterlistLoadAfter), V("b", EdgeType.UserLoadAfter) };

        CyclicInteractionException.DisplayCycle(cycle)
            .Should().Be("a --[Masterlist Load After]-> b --[User Load After]-> a");
        new CyclicInteractionException(cycle).Message.Should().Be(
            "cyclic interaction detected: a --[Masterlist Load After]-> b --[User Load After]-> a");
    }

    [Fact]
    public void EdgeTypesShouldHaveLibLootDisplayStrings()
    {
        Enum.GetValues<EdgeType>().Select(e => e.ToDisplayString()).Should().Equal(
            "Hardcoded", "Master Flag", "Master", "Masterlist Requirement", "User Requirement",
            "Masterlist Load After", "User Load After", "Masterlist Group", "User Group",
            "Record Overlap", "Asset Overlap", "Tie Break", "Blueprint Master");
    }

    [Fact]
    public void GroupsPathExceptionShouldDescribeEachKind()
    {
        var cycle = new[] { V("a", EdgeType.UserLoadAfter) };
        GroupsPathException.FromBuildError(new BuildGroupsGraphException(new CyclicInteractionException(cycle)))
            .Message.Should().Be("found a cycle: a --[User Load After]-> a");
        GroupsPathException.FromBuildError(new BuildGroupsGraphException(new UndefinedGroupException("g")))
            .Message.Should().Be("the group \"g\" does not exist");
        new GroupsPathException(PathfindingException.NegativeCycle()).Message
            .Should().Be("failed to find a path in the groups graph");
        PathfindingException.NegativeCycle().Message
            .Should().Be("encountered a cycle of user-defined \"load after\" relationships");
        new BuildGroupsGraphException(new UndefinedGroupException("g")).Message
            .Should().Be("encountered an undefined group");
        new BuildGroupsGraphException(new CyclicInteractionException(cycle)).Message
            .Should().Be("the groups graph is cyclic");
    }

    #endregion
}
