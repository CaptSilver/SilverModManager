using NexusMods.Games.Bethesda.Loot.Metadata;
using NexusMods.Games.Bethesda.Loot.Sorting;
using static NexusMods.Games.Bethesda.Loot.Tests.Sorting.SortingFixture;

namespace NexusMods.Games.Bethesda.Loot.Tests.Sorting;

// Ports the add_group_edges tests in libloot's sorting/plugins.rs.
public class GroupEdgesTests
{
    private static EdgeType EdgeTypeBetween(PluginsGraph<TestPlugin> graph, int from, int to) =>
        graph.Inner.FindEdge(from, to)!.Value.Weight;

    private static bool ContainsEdge(PluginsGraph<TestPlugin> graph, int from, int to) =>
        graph.Inner.FindEdge(from, to) is not null;

    private const string PluginA1 = "A1.esp";
    private const string PluginA2 = "A2.esp";
    private const string PluginB1 = "B1.esp";
    private const string PluginB2 = "B2.esp";
    private const string PluginC1 = "C1.esp";
    private const string PluginC2 = "C2.esp";
    private const string PluginD1 = "D1.esp";
    private const string PluginD2 = "D2.esp";
    private const string PluginD3 = "D3.esp";
    private const string PluginF = "F.esp";

    [Fact]
    public void ShouldAddUserGroupEdgeIfSourcePluginIsInGroupDueToUserMetadata()
    {
        var fixture = WithPlugins(PluginA, PluginB);

        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.UserGroupSortingData(PluginA, "A"));
        var b = graph.AddNode(fixture.GroupSortingData(PluginB, "B"));

        graph.AddGroupEdges(fixture.GroupsGraph);

        EdgeTypeBetween(graph, a, b).Should().Be(EdgeType.UserGroup);

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldAddUserGroupEdgeIfTargetPluginIsInGroupDueToUserMetadata()
    {
        var fixture = WithPlugins(PluginA, PluginB);

        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.GroupSortingData(PluginA, "A"));
        var b = graph.AddNode(fixture.UserGroupSortingData(PluginB, "B"));

        graph.AddGroupEdges(fixture.GroupsGraph);

        EdgeTypeBetween(graph, a, b).Should().Be(EdgeType.UserGroup);

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldAddUserGroupEdgeIfGroupPathStartsWithUserMetadata()
    {
        var fixture = WithPlugins(PluginB, PluginD);

        var graph = new PluginsGraph<TestPlugin>();
        var b = graph.AddNode(fixture.GroupSortingData(PluginB, "B"));
        var d = graph.AddNode(fixture.SortingData(PluginD));

        graph.AddGroupEdges(fixture.GroupsGraph);

        EdgeTypeBetween(graph, b, d).Should().Be(EdgeType.UserGroup);

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldAddUserGroupEdgeIfGroupPathEndsWithUserMetadata()
    {
        var fixture = WithPlugins(PluginA, PluginC);

        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.GroupSortingData(PluginA, "A"));
        var c = graph.AddNode(fixture.GroupSortingData(PluginC, "C"));

        graph.AddGroupEdges(fixture.GroupsGraph);

        EdgeTypeBetween(graph, a, c).Should().Be(EdgeType.UserGroup);

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldAddUserGroupEdgeIfGroupPathInvolvesUserMetadata()
    {
        var fixture = WithPlugins(PluginA, PluginD);

        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.GroupSortingData(PluginA, "A"));
        var d = graph.AddNode(fixture.SortingData(PluginD));

        graph.AddGroupEdges(fixture.GroupsGraph);

        EdgeTypeBetween(graph, a, d).Should().Be(EdgeType.UserGroup);

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldAddMasterlistGroupEdgeIfNoUserMetadataIsInvolved()
    {
        var fixture = WithPlugins(PluginA, PluginB);

        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.GroupSortingData(PluginA, "A"));
        var b = graph.AddNode(fixture.GroupSortingData(PluginB, "B"));

        graph.AddGroupEdges(fixture.GroupsGraph);

        EdgeTypeBetween(graph, a, b).Should().Be(EdgeType.MasterlistGroup);

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldAddEdgesBetweenPluginsInIndirectlyConnectedGroupsWhenAnIntermediatePluginEdgeIsSkipped()
    {
        var fixture = WithPlugins(PluginA1, PluginA2, PluginB1, PluginB2, PluginC1, PluginC2);

        var graph = new PluginsGraph<TestPlugin>();
        var a1 = graph.AddNode(fixture.GroupSortingData(PluginA1, "A"));
        var a2 = graph.AddNode(fixture.GroupSortingData(PluginA2, "A"));
        var b1 = graph.AddNode(fixture.GroupSortingData(PluginB1, "B"));
        var b2 = graph.AddNode(fixture.GroupSortingData(PluginB2, "B"));
        var c1 = graph.AddNode(fixture.GroupSortingData(PluginC1, "C"));
        var c2 = graph.AddNode(fixture.GroupSortingData(PluginC2, "C"));

        graph.AddEdge(b1, a1, EdgeType.Master);

        graph.AddGroupEdges(fixture.GroupsGraph);

        // Should be A2.esp -> B1.esp -> A1.esp -> B2.esp -> C1.esp
        //                                                -> C2.esp
        ContainsEdge(graph, b1, a1).Should().BeTrue();
        ContainsEdge(graph, a1, b2).Should().BeTrue();
        ContainsEdge(graph, a2, b1).Should().BeTrue();
        ContainsEdge(graph, a2, b2).Should().BeTrue();
        ContainsEdge(graph, b1, c1).Should().BeTrue();
        ContainsEdge(graph, b1, c2).Should().BeTrue();
        ContainsEdge(graph, b2, c1).Should().BeTrue();
        ContainsEdge(graph, b2, c2).Should().BeTrue();
        ContainsEdge(graph, a1, c1).Should().BeTrue();
        ContainsEdge(graph, a1, c2).Should().BeTrue();
        ContainsEdge(graph, c1, c2).Should().BeFalse();
        ContainsEdge(graph, c2, c1).Should().BeFalse();

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldAddEdgesAcrossEmptyGroups()
    {
        var fixture = WithPlugins(PluginA, PluginC);

        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.GroupSortingData(PluginA, "A"));
        var c = graph.AddNode(fixture.GroupSortingData(PluginC, "C"));

        graph.AddGroupEdges(fixture.GroupsGraph);

        // Should be A.esp -> C.esp
        ContainsEdge(graph, a, c).Should().BeTrue();

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldAddEdgesAcrossTheNonEmptyDefaultGroup()
    {
        var fixture = WithPlugins(PluginA, PluginD, PluginE);

        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.GroupSortingData(PluginA, "A"));
        var d = graph.AddNode(fixture.SortingData(PluginD));
        var e = graph.AddNode(fixture.GroupSortingData(PluginE, "E"));

        graph.AddGroupEdges(fixture.GroupsGraph);

        // Should be A.esp -> D.esp -> E.esp
        //                 ---------->
        ContainsEdge(graph, a, d).Should().BeTrue();
        ContainsEdge(graph, d, e).Should().BeTrue();
        ContainsEdge(graph, a, e).Should().BeTrue();

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldSkipAnEdgeThatWouldCauseACycle()
    {
        var fixture = WithPlugins(PluginA, PluginC);

        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.GroupSortingData(PluginA, "A"));
        var c = graph.AddNode(fixture.GroupSortingData(PluginC, "C"));

        graph.AddEdge(c, a, EdgeType.Master);

        graph.AddGroupEdges(fixture.GroupsGraph);

        ContainsEdge(graph, c, a).Should().BeTrue();
        ContainsEdge(graph, a, c).Should().BeFalse();

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldSkipAnEdgeThatWouldCauseACycleInvolvingOtherNonDefaultGroups()
    {
        var fixture = WithPlugins(PluginA, PluginB, PluginC);

        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.GroupSortingData(PluginA, "A"));
        var b = graph.AddNode(fixture.GroupSortingData(PluginB, "B"));
        var c = graph.AddNode(fixture.GroupSortingData(PluginC, "C"));

        graph.AddEdge(c, a, EdgeType.Master);

        graph.AddGroupEdges(fixture.GroupsGraph);

        ContainsEdge(graph, c, a).Should().BeTrue();
        ContainsEdge(graph, a, b).Should().BeTrue();

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldSkipOnlyEdgesToTheTargetGroupPluginsThatWouldCauseACycle()
    {
        var fixture = WithPlugins(PluginA, PluginC1, PluginC2);

        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.GroupSortingData(PluginA, "A"));
        var c1 = graph.AddNode(fixture.GroupSortingData(PluginC1, "C"));
        var c2 = graph.AddNode(fixture.GroupSortingData(PluginC2, "C"));

        graph.AddEdge(c1, a, EdgeType.Master);

        graph.AddGroupEdges(fixture.GroupsGraph);

        // Should be C1.esp -> A.esp -> C2.esp
        ContainsEdge(graph, c1, a).Should().BeTrue();
        ContainsEdge(graph, a, c2).Should().BeTrue();

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldSkipOnlyEdgesFromAncestorsToTheTargetGroupPluginsThatWouldCauseACycle()
    {
        var fixture = WithPlugins(PluginB, PluginC, PluginD1, PluginD2, PluginD3);

        var graph = new PluginsGraph<TestPlugin>();
        var b = graph.AddNode(fixture.GroupSortingData(PluginB, "B"));
        var c = graph.AddNode(fixture.GroupSortingData(PluginC, "C"));
        var d1 = graph.AddNode(fixture.SortingData(PluginD1));
        var d2 = graph.AddNode(fixture.SortingData(PluginD2));
        var d3 = graph.AddNode(fixture.SortingData(PluginD3));

        graph.AddEdge(d1, b, EdgeType.Master);
        graph.AddEdge(d2, b, EdgeType.Master);
        graph.AddEdge(c, b, EdgeType.Master);
        graph.AddEdge(c, d2, EdgeType.Master);
        graph.AddEdge(c, d3, EdgeType.Master);

        graph.AddGroupEdges(fixture.GroupsGraph);

        // Should be: C.esp -> D2.esp -> B.esp -> D3.esp
        //                  -> D1.esp ->
        //                  -------------------->
        //                  ----------->
        ContainsEdge(graph, d1, b).Should().BeTrue();
        ContainsEdge(graph, d2, b).Should().BeTrue();
        ContainsEdge(graph, c, b).Should().BeTrue();
        ContainsEdge(graph, c, d2).Should().BeTrue();
        ContainsEdge(graph, c, d3).Should().BeTrue();
        ContainsEdge(graph, b, d3).Should().BeTrue();
        ContainsEdge(graph, c, d1).Should().BeTrue();

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldAddPluginEdgesAcrossASuccessorIfAtLeastOneEdgeToTheSuccessorGroupWasSkippedWithSuccessiveDepths()
    {
        var fixture = WithPlugins(PluginA1, PluginA2, PluginB1, PluginB2, PluginC1, PluginC2);

        var graph = new PluginsGraph<TestPlugin>();
        var a1 = graph.AddNode(fixture.GroupSortingData(PluginA1, "A"));
        var a2 = graph.AddNode(fixture.GroupSortingData(PluginA2, "A"));
        var b1 = graph.AddNode(fixture.GroupSortingData(PluginB1, "B"));
        var b2 = graph.AddNode(fixture.GroupSortingData(PluginB2, "B"));
        var c1 = graph.AddNode(fixture.GroupSortingData(PluginC1, "C"));
        var c2 = graph.AddNode(fixture.GroupSortingData(PluginC2, "C"));

        graph.AddEdge(b1, a1, EdgeType.Master);
        graph.AddEdge(c1, b2, EdgeType.Master);

        graph.AddGroupEdges(fixture.GroupsGraph);

        // Should be A2.esp -> B1.esp -> A1.esp -> C1.esp -> B2.esp -> C2.esp
        ContainsEdge(graph, b1, a1).Should().BeTrue();
        ContainsEdge(graph, c1, b2).Should().BeTrue();
        ContainsEdge(graph, a1, b2).Should().BeTrue();
        ContainsEdge(graph, a1, c1).Should().BeTrue();
        ContainsEdge(graph, a1, c2).Should().BeTrue();
        ContainsEdge(graph, a2, b1).Should().BeTrue();
        ContainsEdge(graph, a2, b2).Should().BeTrue();
        ContainsEdge(graph, b1, c1).Should().BeTrue();
        ContainsEdge(graph, b1, c2).Should().BeTrue();
        ContainsEdge(graph, b2, c2).Should().BeTrue();

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldAddPluginEdgesAcrossASuccessorIfAtLeastOneEdgeToTheSuccessorGroupWasSkippedWithSuccessiveDepthsAndADifferentOrder()
    {
        var fixture = WithPlugins(PluginA1, PluginA2, PluginB1, PluginB2, PluginC1, PluginC2);

        var graph = new PluginsGraph<TestPlugin>();
        var a1 = graph.AddNode(fixture.GroupSortingData(PluginA1, "A"));
        var a2 = graph.AddNode(fixture.GroupSortingData(PluginA2, "A"));
        var b1 = graph.AddNode(fixture.GroupSortingData(PluginB1, "B"));
        var b2 = graph.AddNode(fixture.GroupSortingData(PluginB2, "B"));
        var c1 = graph.AddNode(fixture.GroupSortingData(PluginC1, "C"));
        var c2 = graph.AddNode(fixture.GroupSortingData(PluginC2, "C"));

        graph.AddEdge(b1, a1, EdgeType.Master);
        graph.AddEdge(c1, b1, EdgeType.Master);

        graph.AddGroupEdges(fixture.GroupsGraph);

        // Should be A2.esp -> C1.esp -> B1.esp -> A1.esp -> B2.esp -> C2.esp
        ContainsEdge(graph, b1, a1).Should().BeTrue();
        ContainsEdge(graph, c1, b1).Should().BeTrue();
        ContainsEdge(graph, a1, b2).Should().BeTrue();
        ContainsEdge(graph, a1, c2).Should().BeTrue();
        ContainsEdge(graph, a2, b1).Should().BeTrue();
        ContainsEdge(graph, a2, b2).Should().BeTrue();
        ContainsEdge(graph, a2, c1).Should().BeTrue();
        ContainsEdge(graph, b1, c2).Should().BeTrue();
        ContainsEdge(graph, b2, c2).Should().BeTrue();
        ContainsEdge(graph, b2, c1).Should().BeFalse();

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldAddEdgeFromAncestorToSuccessorIfNoneOfAGroupsPluginsCan()
    {
        var fixture = WithPlugins(PluginA, PluginB1, PluginB2, PluginC);

        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.GroupSortingData(PluginA, "A"));
        var b1 = graph.AddNode(fixture.GroupSortingData(PluginB1, "B"));
        var b2 = graph.AddNode(fixture.GroupSortingData(PluginB2, "B"));
        var c = graph.AddNode(fixture.GroupSortingData(PluginC, "C"));

        graph.AddEdge(c, b1, EdgeType.Master);
        graph.AddEdge(c, b2, EdgeType.Master);

        graph.AddGroupEdges(fixture.GroupsGraph);

        // Should be A.esp -> C1.esp -> B1.esp
        //                           -> B2.esp
        ContainsEdge(graph, a, b1).Should().BeTrue();
        ContainsEdge(graph, a, b2).Should().BeTrue();
        ContainsEdge(graph, c, b1).Should().BeTrue();
        ContainsEdge(graph, c, b2).Should().BeTrue();
        ContainsEdge(graph, a, c).Should().BeTrue();
        ContainsEdge(graph, b1, b2).Should().BeFalse();
        ContainsEdge(graph, b2, b1).Should().BeFalse();

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldAddEdgeFromAncestorToSuccessorIfNoneOfAGroupsPluginsCanWithEdgesAcrossTheSkippedGroup()
    {
        var fixture = WithPlugins(PluginA1, PluginA2, PluginB1, PluginB2, PluginC1, PluginC2, PluginD1, PluginD2);

        var graph = new PluginsGraph<TestPlugin>();
        var a1 = graph.AddNode(fixture.GroupSortingData(PluginA1, "A"));
        var a2 = graph.AddNode(fixture.GroupSortingData(PluginA2, "A"));
        var b1 = graph.AddNode(fixture.GroupSortingData(PluginB1, "B"));
        var b2 = graph.AddNode(fixture.GroupSortingData(PluginB2, "B"));
        var c1 = graph.AddNode(fixture.GroupSortingData(PluginC1, "C"));
        var c2 = graph.AddNode(fixture.GroupSortingData(PluginC2, "C"));
        var d1 = graph.AddNode(fixture.SortingData(PluginD1));
        var d2 = graph.AddNode(fixture.SortingData(PluginD2));

        graph.AddEdge(b1, a1, EdgeType.Master);
        graph.AddEdge(c1, b1, EdgeType.Master);
        graph.AddEdge(d1, c1, EdgeType.Master);
        graph.AddEdge(d2, c1, EdgeType.Master);

        graph.AddGroupEdges(fixture.GroupsGraph);

        // Should be:
        // A2.esp -> D1.esp -> C1.esp -> B1.esp -> A1.esp -> B2.esp -> C2.esp
        //        -> D2.esp ->
        ContainsEdge(graph, b1, a1).Should().BeTrue();
        ContainsEdge(graph, c1, b1).Should().BeTrue();
        ContainsEdge(graph, d1, c1).Should().BeTrue();
        ContainsEdge(graph, d2, c1).Should().BeTrue();
        ContainsEdge(graph, a1, b2).Should().BeTrue();
        ContainsEdge(graph, a2, b1).Should().BeTrue();
        ContainsEdge(graph, a2, b2).Should().BeTrue();
        ContainsEdge(graph, a1, c2).Should().BeTrue();
        ContainsEdge(graph, b1, c2).Should().BeTrue();
        ContainsEdge(graph, a2, c1).Should().BeTrue();
        ContainsEdge(graph, a2, d1).Should().BeTrue();
        ContainsEdge(graph, a2, d2).Should().BeTrue();
        ContainsEdge(graph, b2, c1).Should().BeFalse();
        ContainsEdge(graph, d1, d2).Should().BeFalse();
        ContainsEdge(graph, d2, d1).Should().BeFalse();

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldDeprioritiseEdgesFromDefaultGroupPluginsWithDefaultLast()
    {
        var fixture = WithPlugins(PluginB, PluginC, PluginD);

        var graph = new PluginsGraph<TestPlugin>();
        var b = graph.AddNode(fixture.GroupSortingData(PluginB, "B"));
        var c = graph.AddNode(fixture.GroupSortingData(PluginC, "C"));
        var d = graph.AddNode(fixture.SortingData(PluginD));

        graph.AddEdge(d, b, EdgeType.Master);

        graph.AddGroupEdges(fixture.GroupsGraph);

        // Should be D.esp -> B.esp -> C.esp
        ContainsEdge(graph, b, c).Should().BeTrue();
        ContainsEdge(graph, d, b).Should().BeTrue();
        ContainsEdge(graph, c, d).Should().BeFalse();

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldDeprioritiseEdgesFromDefaultGroupPluginsWithDefaultFirst()
    {
        var fixture = WithPlugins(PluginD, PluginE, PluginF);

        var graph = new PluginsGraph<TestPlugin>();
        var d = graph.AddNode(fixture.SortingData(PluginD));
        var e = graph.AddNode(fixture.GroupSortingData(PluginE, "E"));
        var f = graph.AddNode(fixture.GroupSortingData(PluginF, "F"));

        graph.AddEdge(f, d, EdgeType.Master);

        graph.AddGroupEdges(fixture.GroupsGraph);

        // Should be E.esp -> F.esp -> D.esp
        ContainsEdge(graph, e, f).Should().BeTrue();
        ContainsEdge(graph, f, d).Should().BeTrue();
        ContainsEdge(graph, d, e).Should().BeFalse();

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldDeprioritiseEdgesFromDefaultGroupPluginsAcrossSkippedIntermediateGroups()
    {
        var fixture = WithPlugins(PluginD, PluginE, PluginF);

        var graph = new PluginsGraph<TestPlugin>();
        var d = graph.AddNode(fixture.SortingData(PluginD));
        var e = graph.AddNode(fixture.GroupSortingData(PluginE, "E"));
        var f = graph.AddNode(fixture.GroupSortingData(PluginF, "F"));

        graph.AddEdge(e, d, EdgeType.Master);

        graph.AddGroupEdges(fixture.GroupsGraph);

        // Should be E.esp -> D.esp -> F.esp
        ContainsEdge(graph, e, d).Should().BeTrue();
        ContainsEdge(graph, d, f).Should().BeTrue();
        ContainsEdge(graph, f, e).Should().BeFalse();

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldDeprioritiseEdgesFromDefaultGroupPluginsWithD1FirstD2Last()
    {
        var fixture = WithPlugins(PluginD1, PluginD2, PluginE, PluginF);

        var graph = new PluginsGraph<TestPlugin>();
        var d1 = graph.AddNode(fixture.SortingData(PluginD1));
        var d2 = graph.AddNode(fixture.SortingData(PluginD2));
        var e = graph.AddNode(fixture.GroupSortingData(PluginE, "E"));
        var f = graph.AddNode(fixture.GroupSortingData(PluginF, "F"));

        graph.AddEdge(f, d2, EdgeType.Master);

        graph.AddGroupEdges(fixture.GroupsGraph);

        // Should be D1.esp -> E.esp -> F.esp -> D2.esp
        ContainsEdge(graph, e, f).Should().BeTrue();
        ContainsEdge(graph, f, d2).Should().BeTrue();
        ContainsEdge(graph, d1, e).Should().BeTrue();
        ContainsEdge(graph, d2, d1).Should().BeFalse();

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldDeprioritiseEdgesFromDefaultGroupPluginsWithNoIdealResult()
    {
        var fixture = WithPlugins(PluginB, PluginC, PluginD, PluginE, PluginF);

        var graph = new PluginsGraph<TestPlugin>();
        var b = graph.AddNode(fixture.GroupSortingData(PluginB, "B"));
        var c = graph.AddNode(fixture.GroupSortingData(PluginC, "C"));
        var d = graph.AddNode(fixture.SortingData(PluginD));
        var e = graph.AddNode(fixture.GroupSortingData(PluginE, "E"));
        var f = graph.AddNode(fixture.GroupSortingData(PluginF, "F"));

        graph.AddEdge(d, b, EdgeType.Master);
        graph.AddEdge(f, d, EdgeType.Master);

        graph.AddGroupEdges(fixture.GroupsGraph);

        // No ideal result, expected is F.esp -> D.esp -> B.esp -> C.esp -> E.esp
        ContainsEdge(graph, f, d).Should().BeTrue();
        ContainsEdge(graph, d, b).Should().BeTrue();
        ContainsEdge(graph, b, c).Should().BeTrue();
        ContainsEdge(graph, c, e).Should().BeTrue();
        ContainsEdge(graph, e, f).Should().BeFalse();

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldDeprioritiseEdgesFromDefaultGroupPluginsWithDefaultInMiddleAndDBookends()
    {
        var fixture = WithPlugins(PluginB, PluginC, PluginD1, PluginD2, PluginE, PluginF);

        var graph = new PluginsGraph<TestPlugin>();
        var b = graph.AddNode(fixture.GroupSortingData(PluginB, "B"));
        var c = graph.AddNode(fixture.GroupSortingData(PluginC, "C"));
        var d1 = graph.AddNode(fixture.SortingData(PluginD1));
        var d2 = graph.AddNode(fixture.SortingData(PluginD2));
        var e = graph.AddNode(fixture.GroupSortingData(PluginE, "E"));
        var f = graph.AddNode(fixture.GroupSortingData(PluginF, "F"));

        graph.AddEdge(d2, b, EdgeType.Master);
        graph.AddEdge(f, d1, EdgeType.Master);

        graph.AddGroupEdges(fixture.GroupsGraph);

        // Should be D2.esp -> B.esp -> C.esp -> E.esp -> F.esp -> D1.esp
        ContainsEdge(graph, d2, b).Should().BeTrue();
        ContainsEdge(graph, b, c).Should().BeTrue();
        ContainsEdge(graph, c, e).Should().BeTrue();
        ContainsEdge(graph, e, f).Should().BeTrue();
        ContainsEdge(graph, f, d1).Should().BeTrue();
        ContainsEdge(graph, d1, d2).Should().BeFalse();

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldDeprioritiseEdgesFromDefaultGroupPluginsWithDefaultInMiddleAndDThroughout()
    {
        const string PluginD4 = "D4.esp";
        var fixture = WithPlugins(PluginB, PluginC, PluginD1, PluginD2, PluginD3, PluginD4, PluginE, PluginF);

        var graph = new PluginsGraph<TestPlugin>();
        var b = graph.AddNode(fixture.GroupSortingData(PluginB, "B"));
        var c = graph.AddNode(fixture.GroupSortingData(PluginC, "C"));
        var d1 = graph.AddNode(fixture.SortingData(PluginD1));
        var d2 = graph.AddNode(fixture.SortingData(PluginD2));
        var d3 = graph.AddNode(fixture.SortingData(PluginD3));
        var d4 = graph.AddNode(fixture.SortingData(PluginD4));
        var e = graph.AddNode(fixture.GroupSortingData(PluginE, "E"));
        var f = graph.AddNode(fixture.GroupSortingData(PluginF, "F"));

        graph.AddEdge(d2, b, EdgeType.Master);
        graph.AddEdge(d4, c, EdgeType.Master);
        graph.AddEdge(f, d1, EdgeType.Master);

        graph.AddGroupEdges(fixture.GroupsGraph);

        // Should be:
        // D2.esp -> B.esp -> D4.esp -> C.esp -> D3.esp -> E.esp -> F.esp -> D1.esp
        ContainsEdge(graph, d2, b).Should().BeTrue();
        ContainsEdge(graph, b, c).Should().BeTrue();
        ContainsEdge(graph, c, d3).Should().BeTrue();
        ContainsEdge(graph, c, e).Should().BeTrue();
        ContainsEdge(graph, d3, e).Should().BeTrue();
        ContainsEdge(graph, e, f).Should().BeTrue();
        ContainsEdge(graph, f, d1).Should().BeTrue();
        ContainsEdge(graph, d4, c).Should().BeTrue();
        ContainsEdge(graph, b, d4).Should().BeTrue();
        ContainsEdge(graph, d1, d2).Should().BeFalse();
        ContainsEdge(graph, d1, d3).Should().BeFalse();
        ContainsEdge(graph, d1, d4).Should().BeFalse();

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldHandleAsymmetricBranchesInTheGroupsGraph()
    {
        var fixture = WithPlugins(PluginA, PluginB, PluginC, PluginD);

        var groupsGraph = GroupsGraph.Build(
            [
                new("A"),
                new("B") { AfterGroups = ["A"] },
                new("C") { AfterGroups = ["B"] },
                new("D") { AfterGroups = ["A"] },
                new(Group.DefaultName),
            ],
            []
        );

        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.GroupSortingData(PluginA, "A"));
        var b = graph.AddNode(fixture.GroupSortingData(PluginB, "B"));
        var c = graph.AddNode(fixture.GroupSortingData(PluginC, "C"));
        var d = graph.AddNode(fixture.GroupSortingData(PluginD, "D"));

        graph.AddGroupEdges(groupsGraph);

        // Should be A.esp -> B.esp -> C.esp
        //                 -> D.esp
        ContainsEdge(graph, a, b).Should().BeTrue();
        ContainsEdge(graph, b, c).Should().BeTrue();
        ContainsEdge(graph, a, d).Should().BeTrue();
        ContainsEdge(graph, d, b).Should().BeFalse();
        ContainsEdge(graph, d, c).Should().BeFalse();
        ContainsEdge(graph, b, d).Should().BeFalse();
        ContainsEdge(graph, c, d).Should().BeFalse();

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldHandleAsymmetricBranchesInTheGroupsGraphThatMerge()
    {
        var fixture = WithPlugins(PluginA, PluginB, PluginC, PluginD, PluginE);

        var groupsGraph = GroupsGraph.Build(
            [
                new("A"),
                new("B") { AfterGroups = ["A"] },
                new("C") { AfterGroups = ["B"] },
                new("D") { AfterGroups = ["A"] },
                new("E") { AfterGroups = ["C", "D"] },
                new(Group.DefaultName),
            ],
            []
        );

        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.GroupSortingData(PluginA, "A"));
        var b = graph.AddNode(fixture.GroupSortingData(PluginB, "B"));
        var c = graph.AddNode(fixture.GroupSortingData(PluginC, "C"));
        var d = graph.AddNode(fixture.GroupSortingData(PluginD, "D"));
        var e = graph.AddNode(fixture.GroupSortingData(PluginE, "E"));

        graph.AddGroupEdges(groupsGraph);

        // Should be A.esp -> B.esp -> C.esp -> E.esp
        //                 -> D.esp ---------->
        ContainsEdge(graph, a, b).Should().BeTrue();
        ContainsEdge(graph, b, c).Should().BeTrue();
        ContainsEdge(graph, c, e).Should().BeTrue();
        ContainsEdge(graph, a, d).Should().BeTrue();
        ContainsEdge(graph, d, e).Should().BeTrue();
        ContainsEdge(graph, d, b).Should().BeFalse();
        ContainsEdge(graph, d, c).Should().BeFalse();
        ContainsEdge(graph, b, d).Should().BeFalse();
        ContainsEdge(graph, c, d).Should().BeFalse();

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldHandleBranchesInTheGroupsGraphThatFormADiamondPattern()
    {
        var fixture = WithPlugins(PluginA, PluginB, PluginC, PluginD);

        var groupsGraph = GroupsGraph.Build(
            [
                new("A"),
                new("B") { AfterGroups = ["A"] },
                new("C") { AfterGroups = ["A"] },
                new("D") { AfterGroups = ["B", "C"] },
                new(Group.DefaultName),
            ],
            []
        );

        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.GroupSortingData(PluginA, "A"));
        var b = graph.AddNode(fixture.GroupSortingData(PluginB, "B"));
        var c = graph.AddNode(fixture.GroupSortingData(PluginC, "C"));
        var d = graph.AddNode(fixture.GroupSortingData(PluginD, "D"));

        graph.AddGroupEdges(groupsGraph);

        // Should be A.esp -> B.esp -> D.esp
        //                 -> C.esp ->
        ContainsEdge(graph, a, b).Should().BeTrue();
        ContainsEdge(graph, b, d).Should().BeTrue();
        ContainsEdge(graph, a, c).Should().BeTrue();
        ContainsEdge(graph, c, d).Should().BeTrue();
        ContainsEdge(graph, b, c).Should().BeFalse();
        ContainsEdge(graph, c, b).Should().BeFalse();

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldAddEdgesAcrossTheMergePointOfBranchesInTheGroupsGraph()
    {
        var fixture = WithPlugins(PluginA, PluginB, PluginC, PluginD, PluginE);

        var groupsGraph = GroupsGraph.Build(
            [
                new("A"),
                new("B") { AfterGroups = ["A"] },
                new("C") { AfterGroups = ["A"] },
                new("D") { AfterGroups = ["B", "C"] },
                new("E") { AfterGroups = ["D"] },
                new(Group.DefaultName),
            ],
            []
        );

        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.GroupSortingData(PluginA, "A"));
        var b = graph.AddNode(fixture.GroupSortingData(PluginB, "B"));
        var c = graph.AddNode(fixture.GroupSortingData(PluginC, "C"));
        var d = graph.AddNode(fixture.GroupSortingData(PluginD, "D"));
        var e = graph.AddNode(fixture.GroupSortingData(PluginE, "E"));

        graph.AddEdge(d, c, EdgeType.Master);

        graph.AddGroupEdges(groupsGraph);

        // Should be A.esp -> B.esp -> D.esp -> C.esp -> E.esp
        ContainsEdge(graph, d, c).Should().BeTrue();
        ContainsEdge(graph, a, b).Should().BeTrue();
        ContainsEdge(graph, b, d).Should().BeTrue();
        ContainsEdge(graph, d, e).Should().BeTrue();
        ContainsEdge(graph, a, c).Should().BeTrue();
        ContainsEdge(graph, c, e).Should().BeTrue();
        ContainsEdge(graph, b, c).Should().BeFalse();
        ContainsEdge(graph, c, b).Should().BeFalse();
        ContainsEdge(graph, c, d).Should().BeFalse();

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldHandleAGroupsGraphWithMultipleSuccessiveBranches()
    {
        const string PluginG = "G.esp";
        var fixture = WithPlugins(PluginA, PluginB, PluginC, PluginD, PluginE, PluginF, PluginG);

        var groupsGraph = GroupsGraph.Build(
            [
                new("A"),
                new("B") { AfterGroups = ["A"] },
                new("C") { AfterGroups = ["A"] },
                new("D") { AfterGroups = ["B", "C"] },
                new("E") { AfterGroups = ["D"] },
                new("F") { AfterGroups = ["D"] },
                new("G") { AfterGroups = ["E", "F"] },
                new(Group.DefaultName),
            ],
            []
        );

        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.GroupSortingData(PluginA, "A"));
        var b = graph.AddNode(fixture.GroupSortingData(PluginB, "B"));
        var c = graph.AddNode(fixture.GroupSortingData(PluginC, "C"));
        var d = graph.AddNode(fixture.GroupSortingData(PluginD, "D"));
        var e = graph.AddNode(fixture.GroupSortingData(PluginE, "E"));
        var f = graph.AddNode(fixture.GroupSortingData(PluginE, "F"));
        var g = graph.AddNode(fixture.GroupSortingData(PluginE, "G"));

        graph.AddGroupEdges(groupsGraph);

        // Should be:
        // A.esp -> B.esp -> D.esp -> E.esp -> G.esp
        //       -> C.esp ->       -> F.esp ->
        ContainsEdge(graph, a, b).Should().BeTrue();
        ContainsEdge(graph, a, c).Should().BeTrue();
        ContainsEdge(graph, b, d).Should().BeTrue();
        ContainsEdge(graph, c, d).Should().BeTrue();
        ContainsEdge(graph, d, e).Should().BeTrue();
        ContainsEdge(graph, d, f).Should().BeTrue();
        ContainsEdge(graph, e, g).Should().BeTrue();
        ContainsEdge(graph, f, g).Should().BeTrue();
        ContainsEdge(graph, b, c).Should().BeFalse();
        ContainsEdge(graph, c, b).Should().BeFalse();
        ContainsEdge(graph, e, f).Should().BeFalse();
        ContainsEdge(graph, f, e).Should().BeFalse();

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldFindAllGroupsInAllPathsBetweenTwoGroupsWhenIgnoringAPlugin()
    {
        var fixture = WithPlugins(PluginA, PluginB, PluginC, PluginD, PluginE);

        var groupsGraph = GroupsGraph.Build(
            [
                new("A"),
                new("B") { AfterGroups = ["A"] },
                new("C") { AfterGroups = ["B"] },
                new("D") { AfterGroups = ["C"] },
                new(Group.DefaultName) { AfterGroups = ["B", "D"] },
            ],
            []
        );

        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.GroupSortingData(PluginA, "A"));
        var b = graph.AddNode(fixture.GroupSortingData(PluginB, "B"));
        var c = graph.AddNode(fixture.GroupSortingData(PluginC, "C"));
        var d = graph.AddNode(fixture.GroupSortingData(PluginD, "D"));
        var e = graph.AddNode(fixture.SortingData(PluginE));

        graph.AddEdge(e, a, EdgeType.Master);

        graph.AddGroupEdges(groupsGraph);

        // Should be:
        // A.esp -> B.esp -> D.esp -> E.esp -> G.esp
        //       -> C.esp ->       -> F.esp ->
        ContainsEdge(graph, a, b).Should().BeTrue();
        ContainsEdge(graph, b, c).Should().BeTrue();
        ContainsEdge(graph, c, d).Should().BeTrue();

        ContainsEdge(graph, a, e).Should().BeFalse();
        ContainsEdge(graph, b, e).Should().BeFalse();
        ContainsEdge(graph, c, e).Should().BeFalse();
        ContainsEdge(graph, d, e).Should().BeFalse();

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldHandleIsolatedGroups()
    {
        var fixture = WithPlugins(PluginA, PluginB, PluginC);

        var groupsGraph = GroupsGraph.Build(
            [
                new("A"),
                new("B") { AfterGroups = ["A"] },
                new("C"),
                new(Group.DefaultName),
            ],
            []
        );

        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.GroupSortingData(PluginA, "A"));
        var b = graph.AddNode(fixture.GroupSortingData(PluginB, "B"));
        var c = graph.AddNode(fixture.GroupSortingData(PluginC, "C"));

        graph.AddGroupEdges(groupsGraph);

        // Should be A.esp -> B.esp
        //           C.esp
        ContainsEdge(graph, a, b).Should().BeTrue();
        ContainsEdge(graph, a, c).Should().BeFalse();
        ContainsEdge(graph, c, a).Should().BeFalse();
        ContainsEdge(graph, b, c).Should().BeFalse();
        ContainsEdge(graph, c, b).Should().BeFalse();

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldHandleDisconnectedGroupGraphs()
    {
        var fixture = WithPlugins(PluginA, PluginB, PluginC, PluginD);

        var groupsGraph = GroupsGraph.Build(
            [
                new("A"),
                new("B") { AfterGroups = ["A"] },
                new("C"),
                new("D") { AfterGroups = ["C"] },
                new(Group.DefaultName),
            ],
            []
        );

        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.GroupSortingData(PluginA, "A"));
        var b = graph.AddNode(fixture.GroupSortingData(PluginB, "B"));
        var c = graph.AddNode(fixture.GroupSortingData(PluginC, "C"));
        var d = graph.AddNode(fixture.GroupSortingData(PluginD, "D"));

        graph.AddGroupEdges(groupsGraph);

        // Should be A.esp -> B.esp
        //           C.esp -> D.esp
        ContainsEdge(graph, a, b).Should().BeTrue();
        ContainsEdge(graph, c, d).Should().BeTrue();
        ContainsEdge(graph, a, c).Should().BeFalse();
        ContainsEdge(graph, a, d).Should().BeFalse();
        ContainsEdge(graph, b, c).Should().BeFalse();
        ContainsEdge(graph, b, d).Should().BeFalse();
        ContainsEdge(graph, c, a).Should().BeFalse();
        ContainsEdge(graph, c, b).Should().BeFalse();
        ContainsEdge(graph, d, a).Should().BeFalse();
        ContainsEdge(graph, d, b).Should().BeFalse();

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldAddEdgesAcrossTheMergePointOfTwoRootNodePaths()
    {
        var fixture = WithPlugins(PluginA, PluginB, PluginC, PluginD);

        var groupsGraph = GroupsGraph.Build(
            [
                new("A"),
                new("B"),
                new("C") { AfterGroups = ["A", "B"] },
                new("D") { AfterGroups = ["C"] },
                new(Group.DefaultName),
            ],
            []
        );

        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.GroupSortingData(PluginA, "A"));
        var b = graph.AddNode(fixture.GroupSortingData(PluginB, "B"));
        var c = graph.AddNode(fixture.GroupSortingData(PluginC, "C"));
        var d = graph.AddNode(fixture.GroupSortingData(PluginD, "D"));

        graph.AddEdge(c, b, EdgeType.Master);

        graph.AddGroupEdges(groupsGraph);

        // Should be A.esp -> C.esp -> D.esp
        //           B.esp ---------->
        ContainsEdge(graph, c, b).Should().BeTrue();
        ContainsEdge(graph, a, c).Should().BeTrue();
        ContainsEdge(graph, c, d).Should().BeTrue();
        ContainsEdge(graph, b, d).Should().BeTrue();
        ContainsEdge(graph, a, b).Should().BeFalse();
        ContainsEdge(graph, b, a).Should().BeFalse();

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldNotDependOnGroupDefinitionOrderIfThereIsASingleLinearPath()
    {
        var fixture = WithPlugins(PluginB, PluginC, PluginD);

        Group[][] masterlists =
        [
            [
                new("B"),
                new("C") { AfterGroups = ["B"] },
                new(Group.DefaultName) { AfterGroups = ["C"] },
            ],
            [
                new("C") { AfterGroups = ["B"] },
                new("B"),
                new(Group.DefaultName) { AfterGroups = ["C"] },
            ],
        ];

        foreach (var masterlist in masterlists)
        {
            var groupsGraph = GroupsGraph.Build(masterlist, []);

            var graph = new PluginsGraph<TestPlugin>();
            var b = graph.AddNode(fixture.GroupSortingData(PluginB, "B"));
            var c = graph.AddNode(fixture.GroupSortingData(PluginC, "C"));
            var d = graph.AddNode(fixture.SortingData(PluginD));

            graph.AddEdge(d, b, EdgeType.Master);

            graph.AddGroupEdges(groupsGraph);

            // Should be D.esp -> B.esp -> C.esp
            ContainsEdge(graph, b, c).Should().BeTrue();
            ContainsEdge(graph, d, b).Should().BeTrue();
            ContainsEdge(graph, c, d).Should().BeFalse();

            graph.CheckForCycles();
        }
    }

    [Fact]
    public void ShouldNotDependOnGroupDefinitionOrderIfThereAreMultipleRoots()
    {
        var fixture = WithPlugins(PluginA, PluginB, PluginC, PluginD);

        Group[][] masterlists =
        [
            [
                new("A"),
                new("B"),
                new("C") { AfterGroups = ["A", "B"] },
                new("D") { AfterGroups = ["C"] },
                new(Group.DefaultName),
            ],
            [
                new("B"),
                new("A"),
                new("C") { AfterGroups = ["A", "B"] },
                new("D") { AfterGroups = ["C"] },
                new(Group.DefaultName),
            ],
        ];

        foreach (var masterlist in masterlists)
        {
            var groupsGraph = GroupsGraph.Build(masterlist, []);

            var graph = new PluginsGraph<TestPlugin>();
            var a = graph.AddNode(fixture.GroupSortingData(PluginA, "A"));
            var b = graph.AddNode(fixture.GroupSortingData(PluginB, "B"));
            var c = graph.AddNode(fixture.GroupSortingData(PluginC, "C"));
            var d = graph.AddNode(fixture.GroupSortingData(PluginD, "D"));

            graph.AddEdge(d, a, EdgeType.Master);

            graph.AddGroupEdges(groupsGraph);

            // Should be B.esp -> D.esp -> A.esp -> C.esp
            //           B.esp ------------------->
            ContainsEdge(graph, d, a).Should().BeTrue();
            ContainsEdge(graph, a, c).Should().BeTrue();
            ContainsEdge(graph, b, c).Should().BeTrue();
            ContainsEdge(graph, b, d).Should().BeTrue();
            ContainsEdge(graph, a, b).Should().BeFalse();
            ContainsEdge(graph, b, a).Should().BeFalse();
            ContainsEdge(graph, c, d).Should().BeFalse();

            graph.CheckForCycles();
        }
    }

    [Fact]
    public void ShouldNotDependOnBranchingGroupDefinitionOrder()
    {
        var fixture = WithPlugins(PluginA, PluginB, PluginC, PluginD, PluginE);

        Group[][] masterlists =
        [
            [
                new("A"),
                new("B") { AfterGroups = ["A"] },
                new("C") { AfterGroups = ["A"] },
                new("D") { AfterGroups = ["B", "C"] },
                new("E") { AfterGroups = ["D"] },
                new(Group.DefaultName),
            ],
            [
                new("A"),
                new("C") { AfterGroups = ["A"] },
                new("B") { AfterGroups = ["A"] },
                new("D") { AfterGroups = ["B", "C"] },
                new("E") { AfterGroups = ["D"] },
                new(Group.DefaultName),
            ],
        ];

        foreach (var masterlist in masterlists)
        {
            var groupsGraph = GroupsGraph.Build(masterlist, []);

            var graph = new PluginsGraph<TestPlugin>();
            var a = graph.AddNode(fixture.GroupSortingData(PluginA, "A"));
            var b = graph.AddNode(fixture.GroupSortingData(PluginB, "B"));
            var c = graph.AddNode(fixture.GroupSortingData(PluginC, "C"));
            var d = graph.AddNode(fixture.GroupSortingData(PluginD, "D"));
            var e = graph.AddNode(fixture.GroupSortingData(PluginE, "E"));

            graph.AddEdge(e, c, EdgeType.Master);

            graph.AddGroupEdges(groupsGraph);

            // Should be A.esp -> B.esp -> D.esp -> E.esp -> C.esp
            ContainsEdge(graph, a, b).Should().BeTrue();
            ContainsEdge(graph, a, c).Should().BeTrue();
            ContainsEdge(graph, a, d).Should().BeTrue();
            ContainsEdge(graph, a, e).Should().BeTrue();
            ContainsEdge(graph, b, d).Should().BeTrue();
            ContainsEdge(graph, b, e).Should().BeTrue();
            ContainsEdge(graph, d, e).Should().BeTrue();
            ContainsEdge(graph, e, c).Should().BeTrue();

            ContainsEdge(graph, b, c).Should().BeFalse();
            ContainsEdge(graph, c, b).Should().BeFalse();
            ContainsEdge(graph, c, d).Should().BeFalse();
            ContainsEdge(graph, c, e).Should().BeFalse();
            ContainsEdge(graph, d, c).Should().BeFalse();

            graph.CheckForCycles();
        }
    }

    [Fact]
    public void ShouldNotDependOnPluginGraphNodeOrder()
    {
        var fixture = WithPlugins(PluginA1, PluginA2, PluginB, PluginC);

        var groupsGraph = GroupsGraph.Build(
            [
                new("A"),
                new("B") { AfterGroups = ["A"] },
                new("C") { AfterGroups = ["B"] },
                new(Group.DefaultName),
            ],
            []
        );

        (string Name, string Group) pa1 = (PluginA1, "A");
        (string Name, string Group) pa2 = (PluginA2, "A");
        (string Name, string Group) pb = (PluginB, "B");
        (string Name, string Group) pc = (PluginC, "C");

        (string Name, string Group)[][] variations =
        [
            [pa1, pa2, pb, pc],
            [pa1, pa2, pc, pb],
            [pa1, pb, pc, pa2],
            [pa1, pb, pa2, pc],
            [pa1, pc, pa2, pb],
            [pa1, pc, pb, pa2],
            [pa2, pa1, pb, pc],
            [pa2, pa1, pc, pb],
            [pa2, pb, pc, pa1],
            [pa2, pb, pa1, pc],
            [pa2, pc, pa1, pb],
            [pa2, pc, pb, pa1],
            [pb, pa2, pa1, pc],
            [pb, pa2, pc, pa1],
            [pb, pa1, pc, pa2],
            [pb, pa1, pa2, pc],
            [pb, pc, pa2, pa1],
            [pb, pc, pa1, pa2],
            [pc, pa2, pb, pa1],
            [pc, pa2, pa1, pb],
            [pc, pb, pa1, pa2],
            [pc, pb, pa2, pa1],
            [pc, pa1, pa2, pb],
            [pc, pa1, pb, pa2],
        ];

        foreach (var plugins in variations)
        {
            var graph = new PluginsGraph<TestPlugin>();

            foreach (var plugin in plugins)
        {
                graph.AddNode(fixture.GroupSortingData(plugin.Name, plugin.Group));
            }

            var a1 = graph.NodeIndexByName(PluginA1)!.Value;
            var a2 = graph.NodeIndexByName(PluginA2)!.Value;
            var b = graph.NodeIndexByName(PluginB)!.Value;
            var c = graph.NodeIndexByName(PluginC)!.Value;

            graph.AddEdge(c, a1, EdgeType.Master);

            graph.AddGroupEdges(groupsGraph);

            // Should be A2.esp -> C.esp -> A1.esp -> B.esp
            //           A2.esp -------------------->
            ContainsEdge(graph, c, a1).Should().BeTrue();
            ContainsEdge(graph, a1, b).Should().BeTrue();
            ContainsEdge(graph, a2, b).Should().BeTrue();
            ContainsEdge(graph, a2, c).Should().BeTrue();
            ContainsEdge(graph, a1, a2).Should().BeFalse();
            ContainsEdge(graph, a2, a1).Should().BeFalse();
            ContainsEdge(graph, b, c).Should().BeFalse();

            graph.CheckForCycles();
        }
    }

    [Fact]
    public void ShouldStartSearchingFromRootGroupsBeforeGoingInLexicographicalOrder()
    {
        var fixture = WithPlugins(PluginA, PluginB, PluginC, PluginD);

        var groupsGraph = GroupsGraph.Build(
            [
                new("D"),
                new("A") { AfterGroups = ["D"] },
                new("B") { AfterGroups = ["A"] },
                new("C") { AfterGroups = ["B"] },
                new(Group.DefaultName),
            ],
            []
        );

        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.GroupSortingData(PluginA, "A"));
        var b = graph.AddNode(fixture.GroupSortingData(PluginB, "B"));
        var c = graph.AddNode(fixture.GroupSortingData(PluginC, "C"));
        var d = graph.AddNode(fixture.GroupSortingData(PluginD, "D"));

        graph.AddEdge(c, d, EdgeType.Master);

        graph.AddGroupEdges(groupsGraph);

        // Should be C.esp -> D.esp -> A.esp -> B.esp
        // Processing groups lexicographically would give:
        // A.esp -> B.esp -> C.esp -> D.esp
        ContainsEdge(graph, c, d).Should().BeTrue();
        ContainsEdge(graph, d, a).Should().BeTrue();
        ContainsEdge(graph, d, b).Should().BeTrue();
        ContainsEdge(graph, a, b).Should().BeTrue();

        ContainsEdge(graph, a, c).Should().BeFalse();
        ContainsEdge(graph, a, d).Should().BeFalse();
        ContainsEdge(graph, b, a).Should().BeFalse();
        ContainsEdge(graph, b, c).Should().BeFalse();
        ContainsEdge(graph, b, d).Should().BeFalse();
        ContainsEdge(graph, d, c).Should().BeFalse();

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldStartSearchingFromTheRootGroupWithTheLongestPath()
    {
        var fixture = WithPlugins(PluginA, PluginB, PluginC, PluginD, PluginE, PluginF);

        var groupsGraph = GroupsGraph.Build(
            [
                new("D"),
                new("B") { AfterGroups = ["D"] },
                new("C") { AfterGroups = ["B"] },
                new("A"),
                new("E") { AfterGroups = ["C", "A"] },
                new("F") { AfterGroups = ["E"] },
                new(Group.DefaultName),
            ],
            []
        );

        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.GroupSortingData(PluginA, "A"));
        var b = graph.AddNode(fixture.GroupSortingData(PluginB, "B"));
        var c = graph.AddNode(fixture.GroupSortingData(PluginC, "C"));
        var d = graph.AddNode(fixture.GroupSortingData(PluginD, "D"));
        var e = graph.AddNode(fixture.GroupSortingData(PluginE, "E"));
        var f = graph.AddNode(fixture.GroupSortingData(PluginF, "F"));

        graph.AddEdge(f, b, EdgeType.Master);

        graph.AddGroupEdges(groupsGraph);

        // Should be          D.esp -> B.esp -> C.esp -> E.esp
        //  A.esp -> F.esp ----------> B.esp
        //  A.esp -------------------------------------> E.esp
        ContainsEdge(graph, d, b).Should().BeTrue();
        ContainsEdge(graph, b, c).Should().BeTrue();
        ContainsEdge(graph, c, e).Should().BeTrue();
        ContainsEdge(graph, a, f).Should().BeTrue();
        ContainsEdge(graph, a, e).Should().BeTrue();
        ContainsEdge(graph, f, b).Should().BeTrue();

        graph.CheckForCycles();
    }

    [Fact]
    public void DoesNotStartSearchingWithTheLongestPath()
    {
        var fixture = WithPlugins(PluginA, PluginB, PluginC, PluginD, PluginE);

        var groupsGraph = GroupsGraph.Build(
            [
                new("A"),
                new("B") { AfterGroups = ["A"] },
                new("C") { AfterGroups = ["A"] },
                new("D") { AfterGroups = ["C"] },
                new("E") { AfterGroups = ["B", "D"] },
                new(Group.DefaultName),
            ],
            []
        );

        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.GroupSortingData(PluginA, "A"));
        var b = graph.AddNode(fixture.GroupSortingData(PluginB, "B"));
        var c = graph.AddNode(fixture.GroupSortingData(PluginC, "C"));
        var d = graph.AddNode(fixture.GroupSortingData(PluginD, "D"));
        var e = graph.AddNode(fixture.GroupSortingData(PluginE, "E"));

        graph.AddEdge(e, c, EdgeType.Master);

        graph.AddGroupEdges(groupsGraph);

        // Should be A.esp -> B.esp -> E.esp -> C.esp -> D.esp
        ContainsEdge(graph, a, b).Should().BeTrue();
        ContainsEdge(graph, b, e).Should().BeTrue();
        ContainsEdge(graph, e, c).Should().BeTrue();
        ContainsEdge(graph, c, d).Should().BeTrue();

        graph.CheckForCycles();
    }

    [Fact]
    public void ShouldMarkNodesAsUnfinishableIfANodeInTheirSubtreeIsUnfinishable()
    {
        var fixture = WithPlugins(PluginA, PluginB, PluginB1, PluginB2, PluginC, PluginD);

        var groupsGraph = GroupsGraph.Build(
            [
                new("A"),
                new("B") { AfterGroups = ["A"] },
                new("C") { AfterGroups = ["B"] },
                new("D") { AfterGroups = ["C"] },
                new(Group.DefaultName),
            ],
            [
                new("BU1") { AfterGroups = ["B"] },
                new("BU2") { AfterGroups = ["BU1"] },
                new("C") { AfterGroups = ["BU2"] },
            ]
        );

        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.GroupSortingData(PluginA, "A"));
        var b = graph.AddNode(fixture.GroupSortingData(PluginB, "B"));
        var b1 = graph.AddNode(fixture.GroupSortingData(PluginB1, "BU1"));
        var b2 = graph.AddNode(fixture.GroupSortingData(PluginB2, "BU2"));
        var c = graph.AddNode(fixture.GroupSortingData(PluginC, "C"));
        var d = graph.AddNode(fixture.GroupSortingData(PluginD, "D"));

        graph.AddGroupEdges(groupsGraph);

        // Should be A.esp -> B.esp -----------------------> C.esp -> D.esp
        //                          -> BU1.esp -> BU2.esp ->
        ContainsEdge(graph, a, b).Should().BeTrue();
        ContainsEdge(graph, a, c).Should().BeTrue();
        ContainsEdge(graph, a, d).Should().BeTrue();
        ContainsEdge(graph, b, c).Should().BeTrue();
        ContainsEdge(graph, b, d).Should().BeTrue();
        ContainsEdge(graph, b, b1).Should().BeTrue();
        ContainsEdge(graph, b, b2).Should().BeTrue();
        ContainsEdge(graph, b1, b2).Should().BeTrue();
        ContainsEdge(graph, b1, c).Should().BeTrue();
        ContainsEdge(graph, b1, d).Should().BeTrue();
        ContainsEdge(graph, b2, c).Should().BeTrue();
        ContainsEdge(graph, b2, c).Should().BeTrue();
        ContainsEdge(graph, c, d).Should().BeTrue();

        graph.CheckForCycles();
    }
}
