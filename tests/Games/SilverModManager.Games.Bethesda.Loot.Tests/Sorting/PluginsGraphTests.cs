using SilverModManager.Games.Bethesda.Loot.Sorting;
using static SilverModManager.Games.Bethesda.Loot.Tests.Sorting.SortingFixture;

namespace SilverModManager.Games.Bethesda.Loot.Tests.Sorting;

// Ports the plugins_graph tests in libloot's sorting/plugins.rs that concern specific edges,
// early loaders and cycles, plus coverage for the graph helpers around them.
public class PluginsGraphTests
{
    private static Vertex V(string name, EdgeType edge) => new(name, edge);

    private static EdgeType EdgeTypeBetween(PluginsGraph<TestPlugin> graph, int from, int to) =>
        graph.Inner.FindEdge(from, to)!.Value.Weight;

    private static bool ContainsEdge(PluginsGraph<TestPlugin> graph, int from, int to) =>
        graph.Inner.FindEdge(from, to) is not null;

    #region CheckForCycles

    [Fact]
    public void CheckForCyclesShouldSucceedIfThereIsNoCycle()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.SortingData(PluginA));
        var b = graph.AddNode(fixture.SortingData(PluginB));

        graph.AddEdge(a, b, EdgeType.Master);

        graph.CheckForCycles();
    }

    [Fact]
    public void CheckForCyclesShouldErrorIfThereIsACycle()
    {
        var fixture = WithPlugins(PluginA, PluginB, PluginC);
        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.SortingData(PluginA));
        var b = graph.AddNode(fixture.SortingData(PluginB));

        graph.AddEdge(a, b, EdgeType.Master);
        graph.AddEdge(b, a, EdgeType.Master);

        var act = graph.CheckForCycles;

        act.Should().Throw<CyclicInteractionException>()
            .Which.Cycle.Should().Equal(V(PluginA, EdgeType.Master), V(PluginB, EdgeType.Master));
    }

    [Fact]
    public void CheckForCyclesShouldOnlyGivePluginsThatArePartOfTheCycle()
    {
        var fixture = WithPlugins(PluginA, PluginB, PluginC);
        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.SortingData(PluginA));
        var b = graph.AddNode(fixture.SortingData(PluginB));
        var c = graph.AddNode(fixture.SortingData(PluginC));

        graph.AddEdge(a, b, EdgeType.Master);
        graph.AddEdge(b, c, EdgeType.Master);
        graph.AddEdge(b, a, EdgeType.MasterFlag);

        var act = graph.CheckForCycles;

        act.Should().Throw<CyclicInteractionException>()
            .Which.Cycle.Should().Equal(V(PluginA, EdgeType.Master), V(PluginB, EdgeType.MasterFlag));
    }

    #endregion

    [Fact]
    public void TopologicalSortShouldReturnEmptyListIfThereAreNoPlugins()
    {
        new PluginsGraph<TestPlugin>().TopologicalSort().Should().BeEmpty();
    }

    [Fact]
    public void TopologicalSortShouldNameAPluginInTheCycleIfThereIsOne()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.SortingData(PluginA));
        var b = graph.AddNode(fixture.SortingData(PluginB));
        // Past the path cache, which would refuse the second edge.
        graph.Inner.AddEdge(a, b, EdgeType.Master);
        graph.Inner.AddEdge(b, a, EdgeType.Master);

        var act = graph.TopologicalSort;

        act.Should().Throw<CycleInvolvingPluginException>().Which.PluginName.Should().BeOneOf(PluginA, PluginB);
    }

    #region AddEarlyLoadingPluginEdges

    [Fact]
    public void AddEarlyLoadingPluginEdgesShouldAddNoEdgesIfThereAreNoEarlyLoadingPlugins()
    {
        var fixture = WithPlugins(PluginA, PluginB, PluginC);
        var graph = new PluginsGraph<TestPlugin>();
        var nodes = new[] { PluginA, PluginB, PluginC }.Select(n => graph.AddNode(fixture.SortingData(n))).ToArray();

        graph.AddEarlyLoadingPluginEdges([]);

        foreach (var from in nodes)
        {
            foreach (var to in nodes) ContainsEdge(graph, from, to).Should().BeFalse();
        }
    }

    [Fact]
    public void AddEarlyLoadingPluginEdgesShouldAddEdgesBetweenConsecutiveEarlyLoadersSkippingMissingPlugins()
    {
        var fixture = WithPlugins(PluginA, PluginC, PluginD);
        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.SortingData(PluginA));
        var c = graph.AddNode(fixture.SortingData(PluginC));
        var d = graph.AddNode(fixture.SortingData(PluginD));

        graph.AddEarlyLoadingPluginEdges([PluginA, PluginB, PluginC, PluginD]);

        ContainsEdge(graph, a, c).Should().BeTrue();
        ContainsEdge(graph, c, d).Should().BeTrue();
        ContainsEdge(graph, a, d).Should().BeFalse();
        graph.CheckForCycles();
    }

    [Fact]
    public void AddEarlyLoadingPluginEdgesShouldAddEdgesFromOnlyTheLastInstalledEarlyLoaderToAllNonEarlyLoaderPlugins()
    {
        var fixture = WithPlugins(PluginA, PluginB, PluginD, PluginE);
        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.SortingData(PluginA));
        var b = graph.AddNode(fixture.SortingData(PluginB));
        var d = graph.AddNode(fixture.SortingData(PluginD));
        var e = graph.AddNode(fixture.SortingData(PluginE));

        graph.AddEarlyLoadingPluginEdges([PluginA, PluginB, PluginC]);

        ContainsEdge(graph, a, b).Should().BeTrue();
        ContainsEdge(graph, b, d).Should().BeTrue();
        ContainsEdge(graph, b, e).Should().BeTrue();
        ContainsEdge(graph, a, d).Should().BeFalse();
        ContainsEdge(graph, a, e).Should().BeFalse();
        graph.CheckForCycles();
    }

    [Fact]
    public void AddEarlyLoadingPluginEdgesShouldOrderEarlyLoadersByTheirPositionInTheListIgnoringCase()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.SortingData(PluginA));
        var b = graph.AddNode(fixture.SortingData(PluginB));

        graph.AddEarlyLoadingPluginEdges(["b.ESP", "a.esp"]);

        ContainsEdge(graph, b, a).Should().BeTrue();
        ContainsEdge(graph, a, b).Should().BeFalse();
    }

    #endregion

    #region AddSpecificEdges

    [Fact]
    public void AddSpecificEdgesShouldAddEdgesOfEachKindFromTheFileToThePlugin()
    {
        var fixture = WithPlugins(PluginA, PluginB, PluginC, PluginD, PluginE, "F.esp");
        fixture.Plugin(PluginA).AddMaster(PluginB);

        var a = new PluginSortingData<TestPlugin>(fixture.Plugin(PluginA), null, null, 0);
        a.MasterlistRequirements = [PluginC];
        a.UserRequirements = [PluginD];
        a.MasterlistLoadAfter = [PluginE];
        a.UserLoadAfter = ["F.esp"];

        var graph = new PluginsGraph<TestPlugin>();
        var nodeA = graph.AddNode(a);
        var others = new[] { PluginB, PluginC, PluginD, PluginE, "F.esp" }
            .Select(n => graph.AddNode(fixture.SortingData(n))).ToArray();

        graph.AddSpecificEdges();

        EdgeTypeBetween(graph, others[0], nodeA).Should().Be(EdgeType.Master);
        EdgeTypeBetween(graph, others[1], nodeA).Should().Be(EdgeType.MasterlistRequirement);
        EdgeTypeBetween(graph, others[2], nodeA).Should().Be(EdgeType.UserRequirement);
        EdgeTypeBetween(graph, others[3], nodeA).Should().Be(EdgeType.MasterlistLoadAfter);
        EdgeTypeBetween(graph, others[4], nodeA).Should().Be(EdgeType.UserLoadAfter);
    }

    [Fact]
    public void AddSpecificEdgesShouldSkipFilesThatAreNotInTheGraph()
    {
        var fixture = WithPlugins(PluginA);
        fixture.Plugin(PluginA).AddMaster("Missing.esm");
        var a = new PluginSortingData<TestPlugin>(fixture.Plugin(PluginA), null, null, 0);
        a.UserLoadAfter = ["Missing.esp"];

        var graph = new PluginsGraph<TestPlugin>();
        graph.AddNode(a);

        graph.AddSpecificEdges();

        graph.Inner.OutEdgesNewestFirst(0).Should().BeEmpty();
    }

    [Fact]
    public void AddSpecificEdgesShouldMatchMasterNamesIgnoringCase()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginA).AddMaster("b.ESP");
        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.SortingData(PluginA));
        var b = graph.AddNode(fixture.SortingData(PluginB));

        graph.AddSpecificEdges();

        EdgeTypeBetween(graph, b, a).Should().Be(EdgeType.Master);
    }

    [Fact]
    public void AddSpecificEdgesShouldPutMastersBeforeNonMastersAsASafetyNet()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginB).IsMaster = true;
        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.SortingData(PluginA));
        var b = graph.AddNode(fixture.SortingData(PluginB));

        graph.AddSpecificEdges();

        EdgeTypeBetween(graph, b, a).Should().Be(EdgeType.MasterFlag);
        ContainsEdge(graph, a, b).Should().BeFalse();
    }

    [Fact]
    public void AddSpecificEdgesShouldAddEdgesInTheOrderOfTheEdgeKinds()
    {
        // The kinds are added in a fixed order, so for the same file the first kind wins and the
        // later ones are implied by the path cache.
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginA).AddMaster(PluginB);
        var a = new PluginSortingData<TestPlugin>(fixture.Plugin(PluginA), null, null, 0);
        a.UserLoadAfter = [PluginB];
        var graph = new PluginsGraph<TestPlugin>();
        var nodeA = graph.AddNode(a);
        var b = graph.AddNode(fixture.SortingData(PluginB));

        graph.AddSpecificEdges();

        graph.Inner.OutEdgesNewestFirst(b).Should().ContainSingle()
            .Which.Weight.Should().Be(EdgeType.Master);
        graph.Inner.InEdgesNewestFirst(nodeA).Should().ContainSingle();
    }

    #endregion

    #region Graph helpers

    [Fact]
    public void AddEdgeShouldSkipAnEdgeWhoseEndpointsAreAlreadyConnectedByACachedPath()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.SortingData(PluginA));
        var b = graph.AddNode(fixture.SortingData(PluginB));

        graph.AddEdge(a, b, EdgeType.Master);
        graph.AddEdge(a, b, EdgeType.UserLoadAfter);

        graph.Inner.OutEdgesNewestFirst(a).Should().ContainSingle().Which.Weight.Should().Be(EdgeType.Master);
        graph.IsPathCached(a, b).Should().BeTrue();
        graph.IsPathCached(b, a).Should().BeFalse();
    }

    [Fact]
    public void PathExistsShouldFindMultiEdgePathsAndCacheWhatTheSearchSaw()
    {
        var fixture = WithPlugins(PluginA, PluginB, PluginC, PluginD);
        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.SortingData(PluginA));
        var b = graph.AddNode(fixture.SortingData(PluginB));
        var c = graph.AddNode(fixture.SortingData(PluginC));
        var d = graph.AddNode(fixture.SortingData(PluginD));
        graph.AddEdge(a, b, EdgeType.Master);
        graph.AddEdge(b, c, EdgeType.Master);
        graph.AddEdge(c, d, EdgeType.Master);

        graph.IsPathCached(a, c).Should().BeFalse();
        graph.PathExists(a, d).Should().BeTrue();
        graph.IsPathCached(a, c).Should().BeTrue();
        graph.PathExists(d, a).Should().BeFalse();
    }

    [Fact]
    public void FindPathShouldReturnTheNodesFromStartToEnd()
    {
        var fixture = WithPlugins(PluginA, PluginB, PluginC, PluginD);
        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.SortingData(PluginA));
        var b = graph.AddNode(fixture.SortingData(PluginB));
        var c = graph.AddNode(fixture.SortingData(PluginC));
        var d = graph.AddNode(fixture.SortingData(PluginD));
        graph.AddEdge(a, b, EdgeType.Master);
        graph.AddEdge(b, c, EdgeType.Master);
        graph.AddEdge(c, d, EdgeType.Master);

        graph.FindPath(a, d).Should().Equal(a, b, c, d);
    }

    [Fact]
    public void FindPathShouldReturnNullIfThereIsNoPath()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.SortingData(PluginA));
        var b = graph.AddNode(fixture.SortingData(PluginB));

        graph.FindPath(a, b).Should().BeNull();
    }

    [Fact]
    public void CheckPathIsHamiltonianShouldReturnTheFirstPairWithoutAnEdge()
    {
        var fixture = WithPlugins(PluginA, PluginB, PluginC);
        var graph = new PluginsGraph<TestPlugin>();
        var a = graph.AddNode(fixture.SortingData(PluginA));
        var b = graph.AddNode(fixture.SortingData(PluginB));
        var c = graph.AddNode(fixture.SortingData(PluginC));
        graph.AddEdge(a, b, EdgeType.Master);

        graph.CheckPathIsHamiltonian([a, b, c]).Should().Be((b, c));
        graph.AddEdge(b, c, EdgeType.Master);
        graph.CheckPathIsHamiltonian([a, b, c]).Should().BeNull();
    }

    #endregion

    #region Name comparison

    [Fact]
    public void LookupDiffersFromUnicaseOnKelvinSign()
    {
        // libloot compares names with unicase, which folds U+212A (Kelvin sign) to "k"; OrdinalIgnoreCase
        // does not. No plugin is named this way in practice, so the gap is accepted.
        var fixture = WithPlugins("k.esp");
        var graph = new PluginsGraph<TestPlugin>();
        var k = graph.AddNode(fixture.SortingData("k.esp"));

        graph.NodeIndexByName("K.ESP").Should().Be(k);
        graph.NodeIndexByName("\u212A.esp").Should().BeNull();
    }

    #endregion
}
