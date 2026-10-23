using NexusMods.Games.Bethesda.Loot.Metadata;
using NexusMods.Games.Bethesda.Loot.Sorting;
using static NexusMods.Games.Bethesda.Loot.Tests.Sorting.SortingFixture;

namespace NexusMods.Games.Bethesda.Loot.Tests.Sorting;

// Ports the sort_plugins tests in libloot's sorting/plugins.rs, plus coverage for sorting end to end.
//
// Where the 88 tests of libloot's sorting/plugins.rs ended up:
//   plugin_sorting_data            PluginSortingDataTests
//   plugins_graph::check_for_cycles, add_early_loading_plugin_edges, topological_sort   PluginsGraphTests
//   plugins_graph::add_group_edges GroupEdgesTests
//   plugins_graph::add_overlap_edges   OverlapEdgesTests
//   plugins_graph::add_tie_break_edges TieBreakEdgesTests
//   sort_plugins                   this file
public class PluginSorterTests
{
    private static Vertex V(string name, EdgeType edge) => new(name, edge);

    private static IReadOnlyList<string> Sort(
        SortingFixture fixture, IReadOnlyList<PluginSortingData<TestPlugin>> data, params string[] earlyLoaders) =>
        PluginSorter.Sort(data, fixture.GroupsGraph, earlyLoaders).Order;

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
    public void ShouldReportTheEdgesBehindTheOrderOnlyWhenAsked()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        IReadOnlyList<PluginSortingData<TestPlugin>> data =
            [With(fixture, PluginA, masterlistLoadAfter: [PluginB]), fixture.SortingData(PluginB)];

        PluginSorter.Sort(data, fixture.GroupsGraph, [], recordEdges: true).Edges
            .Should().Equal(new SortEdge(PluginB, PluginA, EdgeType.MasterlistLoadAfter));
        PluginSorter.Sort(data, fixture.GroupsGraph, []).Edges.Should().BeEmpty();
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
    public void ShouldUseGroupMetadataWhenDecidingRelativePluginPositions()
    {
        var fixture = WithPlugins(PluginB, PluginA);

        var sorted = PluginSorter.Sort(
            [fixture.GroupSortingData(PluginA, "A"), fixture.GroupSortingData(PluginB, "B")],
            fixture.GroupsGraph, []).Order;

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
    public void ShouldNotChangeTheResultIfGivenItsOwnOutput()
    {
        var fixture = WithPlugins(PluginA, PluginB);

        var sorted = Sort(fixture, [fixture.SortingData(PluginB), fixture.SortingData(PluginA)]);
        sorted.Should().Equal(PluginA, PluginB);

        Sort(fixture, [fixture.SortingData(PluginA), fixture.SortingData(PluginB)]).Should().Equal(PluginA, PluginB);
    }

    [Fact]
    public void LookupIsCaseInsensitive()
    {
        var lookup = WithPlugins("a.esp", "B.esp");
        lookup.Plugin("B.esp").AddMaster("A.ESP");
        Sort(lookup, [lookup.SortingData("a.esp"), lookup.SortingData("B.esp")]).Should().Equal("a.esp", "B.esp");
    }

    [Fact]
    public void VertexOrderingIsUtf8ByteOrder()
    {
        // B is 0x42 and a is 0x61, so B sorts first, although a comes first case-insensitively.
        Utf8ByteOrderComparer.Instance.Compare("B.esp", "a.esp").Should().BeNegative();

        // U+FFFD is EF BF BD in UTF-8 and U+1F600 is F0 9F 98 80, so U+FFFD is first, but as UTF-16
        // code units U+1F600 (D83D DE00) is first.
        Utf8ByteOrderComparer.Instance.Compare("\uFFFD.esp", "\U0001F600.esp").Should().BeNegative();
        string.CompareOrdinal("\U0001F600.esp", "\uFFFD.esp").Should().BeNegative();
    }

    [Fact]
    public void PluginsWithTheSameLoadOrderIndexComeOutInVertexOrder()
    {
        // Tie-breaks walk the plugins in a stable sort by load order index, so plugins that share an
        // index keep the order the sorter added their vertices in.
        var fixture = WithPlugins("a.esp", "B.esp", "\uFFFD.esp", "\U0001F600.esp");
        PluginSortingData<TestPlugin> AtZero(string name) => new(fixture.Plugin(name), null, null, 0);

        Sort(fixture, [AtZero("a.esp"), AtZero("\U0001F600.esp"), AtZero("B.esp"), AtZero("\uFFFD.esp")])
            .Should().Equal("B.esp", "a.esp", "\uFFFD.esp", "\U0001F600.esp");
    }

    [Fact]
    public void ShouldReportWhetherAnyOverlapEdgeWasAdded()
    {
        var fixture = WithPlugins(PluginA, PluginB);
        fixture.Plugin(PluginA).OverrideRecordCount = 2;
        fixture.Plugin(PluginA).AddOverlappingRecords(PluginB);
        fixture.Plugin(PluginB).OverrideRecordCount = 1;

        var overlapping = PluginSorter.Sort(
            [fixture.SortingData(PluginB), fixture.SortingData(PluginA)], fixture.GroupsGraph, []);

        overlapping.Order.Should().Equal(PluginA, PluginB);
        overlapping.OverlapEdgesAdded.Should().BeTrue();

        var plain = WithPlugins(PluginA, PluginB);
        PluginSorter.Sort([plain.SortingData(PluginA), plain.SortingData(PluginB)], plain.GroupsGraph, [])
            .OverlapEdgesAdded.Should().BeFalse();
    }

    [Fact]
    public void ShouldLetOverlapOverrideTheCurrentLoadOrder()
    {
        // B is first in the load order but A overrides more of B's records, so A has to load first.
        var fixture = WithPlugins(PluginB, PluginA);
        fixture.Plugin(PluginA).OverrideRecordCount = 3;
        fixture.Plugin(PluginA).AddOverlappingRecords(PluginB);
        fixture.Plugin(PluginB).OverrideRecordCount = 1;

        Sort(fixture, [fixture.SortingData(PluginA), fixture.SortingData(PluginB)]).Should().Equal(PluginA, PluginB);
    }

    [Fact]
    public void ShouldKeepTheCurrentLoadOrderOfUnrelatedPlugins()
    {
        var fixture = WithPlugins(PluginE, PluginC, PluginA, PluginD, PluginB);

        var sorted = Sort(fixture, [.. new[] { PluginA, PluginB, PluginC, PluginD, PluginE }.Select(fixture.SortingData)]);

        sorted.Should().Equal(PluginE, PluginC, PluginA, PluginD, PluginB);
    }

    [Fact]
    public void ShouldSortRandomGraphsWithoutErrorAndNotChangeTheirOutput()
    {
        var random = new Random(20260622);
        string[] groups = ["A", "B", "C", "default", "E", "F"];

        for (var round = 0; round < 200; round++)
        {
            var names = Enumerable.Range(0, 30).Select(i => $"P{i:D2}.esp").ToArray();
            var fixture = WithPlugins(names);

            // The masters of a plugin are taken from the ones ranked below it, so there is no cycle.
            var rank = Enumerable.Range(0, names.Length).OrderBy(_ => random.Next()).ToArray();
            var byRank = names.Select((name, i) => (name, rank: rank[i])).OrderBy(e => e.rank).Select(e => e.name).ToArray();
            for (var r = 1; r < byRank.Length; r++)
            {
                for (var m = 0; m < r; m++)
                {
                    if (random.Next(10) == 0) fixture.Plugin(byRank[r]).AddMaster(byRank[m]);
                }
            }

            var group = names.ToDictionary(n => n, _ => groups[random.Next(groups.Length)]);
            var data = names.Select(n => fixture.GroupSortingData(n, group[n])).ToList();

            var sorted = PluginSorter.Sort(data, fixture.GroupsGraph, []);
            sorted.Order.Should().HaveCount(names.Length).And.OnlyHaveUniqueItems();
            sorted.UnlinkedNeighbours.Should().BeEmpty();

            foreach (var name in names)
            {
                foreach (var master in fixture.Plugin(name).Masters)
                {
                    sorted.Order.ToList().IndexOf(master).Should().BeLessThan(sorted.Order.ToList().IndexOf(name));
                }
            }

            // Sorting the output, which is now the current load order, changes nothing.
            var position = sorted.Order.Select((n, i) => (n, i)).ToDictionary(e => e.n, e => e.i);
            var again = PluginSorter.Sort(
                names.Select(n => new PluginSortingData<TestPlugin>(
                    fixture.Plugin(n), new PluginMetadata(n) { Group = group[n] }, null, position[n])).ToList(),
                fixture.GroupsGraph, []);

            again.Order.Should().Equal(sorted.Order, $"round {round}");
        }
    }
}
