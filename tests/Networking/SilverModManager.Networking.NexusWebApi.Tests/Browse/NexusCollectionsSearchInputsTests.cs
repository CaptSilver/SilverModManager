using FluentAssertions;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Networking.NexusWebApi.Tests.Browse;

public class NexusCollectionsSearchInputsTests
{
    private static NexusCollectionsSearchQuery Query(string text = "", NexusCollectionsSort sort = NexusCollectionsSort.Endorsements, bool includeAdult = false)
        => new(NexusModsGameId.From(3333), text, sort, 0, 40, includeAdult);

    [Fact]
    public void Filter_NamesTheGameByItsId()
    {
        var filter = NexusCollectionsSearchInputs.Filter(Query());

        filter.GameId.Should().ContainSingle().Which.Value.Should().Be("3333");
        filter.GameId![0].Op.Should().Be(FilterComparisonOperator.Equals);
    }

    [Fact]
    public void Filter_ExcludesAdultContentByDefault_AndLeavesItAloneWhenAllowed()
    {
        var filter = NexusCollectionsSearchInputs.Filter(Query());
        filter.AdultContent.Should().ContainSingle().Which.Value.Should().BeFalse();

        NexusCollectionsSearchInputs.Filter(Query(includeAdult: true)).AdultContent.Should().BeNull();
    }

    [Fact]
    public void Filter_ListsOnlyCollectionsWithAPublishedRevision()
    {
        NexusCollectionsSearchInputs.Filter(Query()).HasPublishedRevision.Should().ContainSingle().Which.Value.Should().BeTrue();
    }

    [Fact]
    public void Filter_WithNoSearchText_SendsNoNameClause()
    {
        NexusCollectionsSearchInputs.Filter(Query("  ")).Name.Should().BeNull();
    }

    [Fact]
    public void Filter_WithSearchText_MatchesTheTrimmedTextAsAWildcard()
    {
        var name = NexusCollectionsSearchInputs.Filter(Query(" skill tree ")).Name.Should().ContainSingle().Subject;

        name.Value.Should().Be("skill tree");
        name.Op.Should().Be(FilterComparisonOperator.Wildcard);
    }

    [Fact]
    public void Sort_MapsEachChoiceToItsField()
    {
        NexusCollectionsSearchInputs.Sort(Query(sort: NexusCollectionsSort.Endorsements)).Should().ContainSingle().Which.Endorsements!.Direction.Should().Be(SortDirection.Desc);
        NexusCollectionsSearchInputs.Sort(Query(sort: NexusCollectionsSort.Downloads)).Should().ContainSingle().Which.Downloads!.Direction.Should().Be(SortDirection.Desc);
        NexusCollectionsSearchInputs.Sort(Query(sort: NexusCollectionsSort.Updated)).Should().ContainSingle().Which.UpdatedAt!.Direction.Should().Be(SortDirection.Desc);
    }
}
