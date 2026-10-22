using NexusMods.Games.Bethesda.Loot.Sorting;
using static NexusMods.Games.Bethesda.Loot.Tests.Sorting.SortingFixture;

namespace NexusMods.Games.Bethesda.Loot.Tests.Sorting;

// Ports the sort_plugins tests in libloot's sorting/plugins.rs that concern specific edges, early
// loaders, validation and cycles. The tests that depend on group, overlap or tie-break edges are
// not here; the sort is run without those steps (SortStages.None).
public class PluginSorterTests
{
    private static Vertex V(string name, EdgeType edge) => new(name, edge);

    private static IReadOnlyList<string> Sort(
        SortingFixture fixture, IReadOnlyList<PluginSortingData<TestPlugin>> data, params string[] earlyLoaders) =>
        PluginSorter.Sort(data, fixture.GroupsGraph, earlyLoaders, SortStages.None).Order;

    private static void ShouldFailValidationWith(Func<object> act, params Vertex[] cycle) =>
        act.Should().Throw<PluginGraphValidationException>().Which.Cycle.Cycle.Should().Equal(cycle);

    private static PluginSortingData<TestPlugin> With(
        SortingFixture fixture, string name,
        string[]? masterlistLoadAfter = null, string[]? userLoadAfter = null,
        string[]? masterlistReq = null, string[]? userReq = null)
    {
        var data = fixture.SortingData(name);
        if (masterlistLoadAfter is not null) data.MasterlistLoadAfter = masterlistLoadAfter;
        if (userLoadAfter is not null) data.UserLoadAfter = userLoadAfter;
        if (masterlistReq is not null) data.MasterlistRequirements = masterlistReq;
        if (userReq is not null) data.UserRequirements = userReq;
        return data;
    }

    [Fact]
    public void ShouldUseLoadAfterMetadataWhenDecidingRelativePluginPositions()
    {
        var fixture = WithPlugins(PluginA, PluginB);

        var sorted = Sort(fixture, [With(fixture, PluginA, masterlistLoadAfter: [PluginB]), fixture.SortingData(PluginB)]);

        sorted.Should().Equal(PluginB, PluginA);
    }

    [Fact]
    public void ShouldUseRequirementMetadataWhenDecidingRelativePluginPositions()
    {
        var fixture = WithPlugins(PluginA, PluginB);

        var sorted = Sort(fixture, [With(fixture, PluginA, masterlistReq: [PluginB]), fixture.SortingData(PluginB)]);

        sorted.Should().Equal(PluginB, PluginA);
    }

    [Fact]
    public void ShouldUseEarlyLoaderPositionsWhenDecidingRelativePluginPositions()
    {
        var fixture = WithPlugins(PluginB, PluginA);

        var sorted = Sort(fixture, [fixture.SortingData(PluginA), fixture.SortingData(PluginB)], PluginA);

        sorted.Should().Equal(PluginA, PluginB);
    }

    [Fact]
    public void ShouldErrorIfAPluginHasAGroupThatDoesNotExist()
    {
        var fixture = WithPlugins(PluginA);

        var act = () => Sort(fixture, [fixture.GroupSortingData(PluginA, "missing")]);

        act.Should().Throw<UndefinedGroupException>().Which.GroupName.Should().Be("missing");
    }

    [Fact]
    public void ShouldErrorIfACyclicInteractionIsEncountered()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginA).AddMaster(PluginB);
        fixture.Plugin(PluginB).AddMaster(PluginA);

        var act = () => Sort(fixture, [fixture.SortingData(PluginA), fixture.SortingData(PluginB)]);

        act.Should().Throw<CyclicInteractionException>()
            .Which.Cycle.Should().Equal(V(PluginA, EdgeType.Master), V(PluginB, EdgeType.Master));
    }

    [Fact]
    public void ShouldErrorIfAMasterEdgeWouldContradictMasterFlags()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginA).IsMaster = true;
        fixture.Plugin(PluginA).AddMaster(PluginB);

        var act = () => Sort(fixture, [fixture.SortingData(PluginA), fixture.SortingData(PluginB)]);

        ShouldFailValidationWith(act, V(PluginB, EdgeType.Master), V(PluginA, EdgeType.MasterFlag));
    }

    [Fact]
    public void ShouldErrorIfAMasterlistLoadAfterContradictsMasterFlags()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginA).IsMaster = true;

        var act = () => Sort(fixture, [With(fixture, PluginA, masterlistLoadAfter: [PluginB]), fixture.SortingData(PluginB)]);

        ShouldFailValidationWith(act, V(PluginB, EdgeType.MasterlistLoadAfter), V(PluginA, EdgeType.MasterFlag));
    }

    [Fact]
    public void ShouldErrorIfAUserLoadAfterContradictsMasterFlags()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginA).IsMaster = true;

        var act = () => Sort(fixture, [With(fixture, PluginA, userLoadAfter: [PluginB]), fixture.SortingData(PluginB)]);

        ShouldFailValidationWith(act, V(PluginB, EdgeType.UserLoadAfter), V(PluginA, EdgeType.MasterFlag));
    }

    [Fact]
    public void ShouldErrorIfAMasterlistRequirementContradictsMasterFlags()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginA).IsMaster = true;

        var act = () => Sort(fixture, [With(fixture, PluginA, masterlistReq: [PluginB]), fixture.SortingData(PluginB)]);

        ShouldFailValidationWith(act, V(PluginB, EdgeType.MasterlistRequirement), V(PluginA, EdgeType.MasterFlag));
    }

    [Fact]
    public void ShouldErrorIfAUserRequirementContradictsMasterFlags()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginA).IsMaster = true;

        var act = () => Sort(fixture, [With(fixture, PluginA, userReq: [PluginB]), fixture.SortingData(PluginB)]);

        ShouldFailValidationWith(act, V(PluginB, EdgeType.UserRequirement), V(PluginA, EdgeType.MasterFlag));
    }

    [Fact]
    public void ShouldErrorIfAnEarlyLoaderContradictsMasterFlags()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginA).IsMaster = true;

        var act = () => Sort(fixture, [fixture.SortingData(PluginA), fixture.SortingData(PluginB)], PluginB);

        ShouldFailValidationWith(act, V(PluginB, EdgeType.Hardcoded), V(PluginA, EdgeType.MasterFlag));
    }

    [Fact]
    public void ShouldNotErrorIfAMasterEdgeWouldPutABlueprintMasterBeforeAMaster()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginA).IsMaster = true;
        fixture.Plugin(PluginA).IsBlueprintPlugin = true;
        fixture.Plugin(PluginB).IsMaster = true;
        fixture.Plugin(PluginB).AddMaster(PluginA);

        var sorted = Sort(fixture, [fixture.SortingData(PluginA), fixture.SortingData(PluginB)]);

        sorted.Should().Equal(PluginB, PluginA);
    }

    [Fact]
    public void ShouldNotErrorIfAMasterEdgeWouldPutABlueprintMasterBeforeANonMaster()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginA).IsMaster = true;
        fixture.Plugin(PluginA).IsBlueprintPlugin = true;
        fixture.Plugin(PluginB).AddMaster(PluginA);

        var sorted = Sort(fixture, [fixture.SortingData(PluginA), fixture.SortingData(PluginB)]);

        sorted.Should().Equal(PluginB, PluginA);
    }

    [Fact]
    public void ShouldErrorIfAMasterlistLoadAfterWouldPutABlueprintMasterBeforeAMaster()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginA).IsMaster = true;
        fixture.Plugin(PluginB).IsMaster = true;
        fixture.Plugin(PluginB).IsBlueprintPlugin = true;

        var act = () => Sort(fixture, [With(fixture, PluginA, masterlistLoadAfter: [PluginB]), fixture.SortingData(PluginB)]);

        ShouldFailValidationWith(act, V(PluginB, EdgeType.MasterlistLoadAfter), V(PluginA, EdgeType.BlueprintMaster));
    }

    [Fact]
    public void ShouldErrorIfAMasterlistLoadAfterWouldPutABlueprintMasterBeforeANonMaster()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginB).IsMaster = true;
        fixture.Plugin(PluginB).IsBlueprintPlugin = true;

        var act = () => Sort(fixture, [With(fixture, PluginA, masterlistLoadAfter: [PluginB]), fixture.SortingData(PluginB)]);

        ShouldFailValidationWith(act, V(PluginB, EdgeType.MasterlistLoadAfter), V(PluginA, EdgeType.BlueprintMaster));
    }

    [Fact]
    public void ShouldErrorIfAUserLoadAfterWouldPutABlueprintMasterBeforeAMaster()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginA).IsMaster = true;
        fixture.Plugin(PluginB).IsMaster = true;
        fixture.Plugin(PluginB).IsBlueprintPlugin = true;

        var act = () => Sort(fixture, [With(fixture, PluginA, userLoadAfter: [PluginB]), fixture.SortingData(PluginB)]);

        ShouldFailValidationWith(act, V(PluginB, EdgeType.UserLoadAfter), V(PluginA, EdgeType.BlueprintMaster));
    }

    [Fact]
    public void ShouldErrorIfAUserLoadAfterWouldPutABlueprintMasterBeforeANonMaster()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginB).IsMaster = true;
        fixture.Plugin(PluginB).IsBlueprintPlugin = true;

        var act = () => Sort(fixture, [With(fixture, PluginA, userLoadAfter: [PluginB]), fixture.SortingData(PluginB)]);

        ShouldFailValidationWith(act, V(PluginB, EdgeType.UserLoadAfter), V(PluginA, EdgeType.BlueprintMaster));
    }

    [Fact]
    public void ShouldErrorIfAMasterlistRequirementWouldPutABlueprintMasterBeforeAMaster()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginA).IsMaster = true;
        fixture.Plugin(PluginB).IsMaster = true;
        fixture.Plugin(PluginB).IsBlueprintPlugin = true;

        var act = () => Sort(fixture, [With(fixture, PluginA, masterlistReq: [PluginB]), fixture.SortingData(PluginB)]);

        ShouldFailValidationWith(act, V(PluginB, EdgeType.MasterlistRequirement), V(PluginA, EdgeType.BlueprintMaster));
    }

    [Fact]
    public void ShouldErrorIfAMasterlistRequirementWouldPutABlueprintMasterBeforeANonMaster()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginB).IsMaster = true;
        fixture.Plugin(PluginB).IsBlueprintPlugin = true;

        var act = () => Sort(fixture, [With(fixture, PluginA, masterlistReq: [PluginB]), fixture.SortingData(PluginB)]);

        ShouldFailValidationWith(act, V(PluginB, EdgeType.MasterlistRequirement), V(PluginA, EdgeType.BlueprintMaster));
    }

    [Fact]
    public void ShouldErrorIfAUserRequirementWouldPutABlueprintMasterBeforeAMaster()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginA).IsMaster = true;
        fixture.Plugin(PluginB).IsMaster = true;
        fixture.Plugin(PluginB).IsBlueprintPlugin = true;

        var act = () => Sort(fixture, [With(fixture, PluginA, userReq: [PluginB]), fixture.SortingData(PluginB)]);

        ShouldFailValidationWith(act, V(PluginB, EdgeType.UserRequirement), V(PluginA, EdgeType.BlueprintMaster));
    }

    [Fact]
    public void ShouldErrorIfAUserRequirementWouldPutABlueprintMasterBeforeANonMaster()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginB).IsMaster = true;
        fixture.Plugin(PluginB).IsBlueprintPlugin = true;

        var act = () => Sort(fixture, [With(fixture, PluginA, userReq: [PluginB]), fixture.SortingData(PluginB)]);

        ShouldFailValidationWith(act, V(PluginB, EdgeType.UserRequirement), V(PluginA, EdgeType.BlueprintMaster));
    }

    [Fact]
    public void ShouldNotErrorIfAnEarlyLoaderWouldPutABlueprintMasterBeforeAMaster()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginA).IsMaster = true;
        fixture.Plugin(PluginB).IsMaster = true;
        fixture.Plugin(PluginB).IsBlueprintPlugin = true;

        var sorted = Sort(fixture, [fixture.SortingData(PluginA), fixture.SortingData(PluginB)], PluginB);

        sorted.Should().Equal(PluginA, PluginB);
    }

    [Fact]
    public void ShouldNotErrorIfAnEarlyLoaderWouldPutABlueprintMasterBeforeANonMaster()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginB).IsMaster = true;
        fixture.Plugin(PluginB).IsBlueprintPlugin = true;

        var sorted = Sort(fixture, [fixture.SortingData(PluginA), fixture.SortingData(PluginB)], PluginB);

        sorted.Should().Equal(PluginA, PluginB);
    }

    [Fact]
    public void ShouldReturnNothingIfThereAreNoPlugins()
    {
        var fixture = WithPlugins();

        PluginSorter.Sort(Array.Empty<PluginSortingData<TestPlugin>>(), fixture.GroupsGraph, []).Order.Should().BeEmpty();
    }

    [Fact]
    public void ShouldNotDependOnTheOrderPluginsAreGivenIn()
    {
        var fixture = WithPlugins(PluginA, PluginB, PluginC, PluginD);
        fixture.Plugin(PluginA).AddMaster(PluginC);
        fixture.Plugin(PluginD).AddMaster(PluginB);

        var forwards = Sort(fixture, [.. new[] { PluginA, PluginB, PluginC, PluginD }.Select(fixture.SortingData)]);
        var backwards = Sort(fixture, [.. new[] { PluginD, PluginC, PluginB, PluginA }.Select(fixture.SortingData)]);
        var shuffled = Sort(fixture, [.. new[] { PluginC, PluginA, PluginD, PluginB }.Select(fixture.SortingData)]);

        backwards.Should().Equal(forwards);
        shuffled.Should().Equal(forwards);
        forwards.ToList().IndexOf(PluginC).Should().BeLessThan(forwards.ToList().IndexOf(PluginA));
        forwards.ToList().IndexOf(PluginB).Should().BeLessThan(forwards.ToList().IndexOf(PluginD));
    }

    [Fact]
    public void ShouldPutMastersBeforeNonMastersAndBlueprintMastersLast()
    {
        var fixture = WithPlugins(PluginA, PluginB, PluginC);
        fixture.Plugin(PluginA).IsMaster = true;
        fixture.Plugin(PluginA).IsBlueprintPlugin = true;
        fixture.Plugin(PluginC).IsMaster = true;

        var sorted = Sort(fixture, [fixture.SortingData(PluginA), fixture.SortingData(PluginB), fixture.SortingData(PluginC)]);

        sorted.Should().Equal(PluginC, PluginB, PluginA);
    }

    [Fact]
    public void ShouldReportABlueprintMasterCycleBeforeANonMasterCycle()
    {
        // libloot sorts the blueprint masters before the non-masters, so when both have a cycle
        // the blueprint masters' one is the error the user sees.
        var fixture = WithPlugins(PluginA, PluginB, PluginC, PluginD);
        foreach (var name in new[] { PluginA, PluginB })
        {
            fixture.Plugin(name).IsMaster = true;
            fixture.Plugin(name).IsBlueprintPlugin = true;
        }

        fixture.Plugin(PluginA).AddMaster(PluginB);
        fixture.Plugin(PluginB).AddMaster(PluginA);
        fixture.Plugin(PluginC).AddMaster(PluginD);
        fixture.Plugin(PluginD).AddMaster(PluginC);

        var act = () => Sort(fixture, [.. new[] { PluginA, PluginB, PluginC, PluginD }.Select(fixture.SortingData)]);

        act.Should().Throw<CyclicInteractionException>()
            .Which.Cycle.Should().Equal(V(PluginA, EdgeType.Master), V(PluginB, EdgeType.Master));
    }

    [Fact]
    public void ShouldNotChangeTheResultIfGivenItsOwnOutputWhenEdgesFixTheOrder()
    {
        var fixture = WithPlugins(PluginA, PluginB, PluginC);
        fixture.Plugin(PluginA).AddMaster(PluginB);
        fixture.Plugin(PluginB).AddMaster(PluginC);

        var first = Sort(fixture, [.. new[] { PluginB, PluginA, PluginC }.Select(fixture.SortingData)]);
        var second = Sort(fixture, [.. first.Select(fixture.SortingData)]);

        first.Should().Equal(PluginC, PluginB, PluginA);
        second.Should().Equal(first);
    }

    [Fact]
    public void VertexOrderingIsUtf8ByteOrderWhileLookupIsCaseInsensitive()
    {
        // Without tie-break edges, plugins with no edges between them come out in the reverse of
        // vertex order, so the result shows how the vertices were ordered.
        var bytes = WithPlugins("a.esp", "B.esp");
        Sort(bytes, [bytes.SortingData("a.esp"), bytes.SortingData("B.esp")])
            .Should().Equal(["a.esp", "B.esp"], "B (0x42) sorts before a (0x61) as bytes, so the vertex order is B, a");

        // U+FFFD is EF BF BD in UTF-8 and U+1F600 is F0 9F 98 80, so U+FFFD is first, but as UTF-16
        // code units U+1F600 (D83D DE00) is first.
        var supplementary = WithPlugins("\uFFFD.esp", "\U0001F600.esp");
        Sort(supplementary, [supplementary.SortingData("\uFFFD.esp"), supplementary.SortingData("\U0001F600.esp")])
            .Should().Equal("\U0001F600.esp", "\uFFFD.esp");
        string.CompareOrdinal("\U0001F600.esp", "\uFFFD.esp").Should().BeNegative();

        // A master reference in another case still finds the plugin.
        var lookup = WithPlugins("a.esp", "B.esp");
        lookup.Plugin("B.esp").AddMaster("A.ESP");
        Sort(lookup, [lookup.SortingData("a.esp"), lookup.SortingData("B.esp")]).Should().Equal("a.esp", "B.esp");
    }
}
