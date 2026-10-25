using FluentAssertions;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Networking.NexusWebApi.Tests.Browse;

public class NexusModsSearchInputsTests
{
    private static NexusModsSearchQuery Query(string text = "", NexusModsSort sort = NexusModsSort.Endorsements, int offset = 0, int count = 40, bool includeAdult = false)
        => new(NexusModsGameId.From(3333), text, sort, offset, count, includeAdult);

    [Fact]
    public void Filter_NamesTheGameByItsId()
    {
        var filter = NexusModsSearchInputs.Filter(Query());

        filter.GameId.Should().ContainSingle().Which.Value.Should().Be("3333");
        filter.GameId![0].Op.Should().Be(FilterComparisonOperator.Equals);
    }

    [Fact]
    public void Filter_ExcludesAdultContentByDefault()
    {
        var filter = NexusModsSearchInputs.Filter(Query());

        filter.AdultContent.Should().ContainSingle().Which.Value.Should().BeFalse();
        filter.AdultContent![0].Op.Should().Be(FilterComparisonOperator.Equals);
    }

    [Fact]
    public void Filter_LeavesAdultContentAloneWhenItIsAllowed()
    {
        NexusModsSearchInputs.Filter(Query(includeAdult: true)).AdultContent.Should().BeNull();
    }

    [Fact]
    public void Filter_WithNoSearchText_SendsNoNameClause()
    {
        NexusModsSearchInputs.Filter(Query("")).Name.Should().BeNull();
        NexusModsSearchInputs.Filter(Query("   ")).Name.Should().BeNull();
    }

    [Fact]
    public void Filter_WithSearchText_MatchesTheTrimmedTextAsAWildcard()
    {
        var filter = NexusModsSearchInputs.Filter(Query("  skill tree "));

        var name = filter.Name.Should().ContainSingle().Subject;
        name.Value.Should().Be("skill tree");
        name.Op.Should().Be(FilterComparisonOperatorEqualsWildcard.Wildcard);
    }

    [Fact]
    public void Filter_CombinesItsClausesWithAnd()
    {
        NexusModsSearchInputs.Filter(Query("x")).Op.Should().Be(FilterLogicalOperator.And);
    }

    [Fact]
    public void Sort_ByEndorsements_IsMostEndorsedFirst()
    {
        var sort = NexusModsSearchInputs.Sort(Query(sort: NexusModsSort.Endorsements)).Should().ContainSingle().Subject;

        sort.Endorsements!.Direction.Should().Be(SortDirection.Desc);
        sort.Downloads.Should().BeNull();
    }

    [Fact]
    public void Sort_ByDownloads_IsMostDownloadedFirst()
    {
        NexusModsSearchInputs.Sort(Query(sort: NexusModsSort.Downloads)).Single().Downloads!.Direction.Should().Be(SortDirection.Desc);
    }

    [Fact]
    public void Sort_ByUpdated_IsNewestFirst()
    {
        NexusModsSearchInputs.Sort(Query(sort: NexusModsSort.Updated)).Single().UpdatedAt!.Direction.Should().Be(SortDirection.Desc);
    }

    [Fact]
    public void Sort_ByName_IsAlphabetical()
    {
        NexusModsSearchInputs.Sort(Query(sort: NexusModsSort.Name)).Single().Name!.Direction.Should().Be(SortDirection.Asc);
    }
}
