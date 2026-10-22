using FluentAssertions;
using NexusMods.Games.FromSoft.Merge;

namespace NexusMods.Games.FromSoft.Tests.Merge;

public class BinderEntrySetsTests
{
    [Fact]
    public void AcceptsThreeSetsOfTheSameEntries()
        => BinderEntrySets.RequireSameEntries([1, 2], [2, 1], [1, 2], "regulations");

    [Fact]
    public void NamesTheOddEntryAndWhoHoldsIt()
    {
        var act = () => BinderEntrySets.RequireSameEntries([1, 99], [1], [1], "regulations");
        act.Should().Throw<MergeException>().WithMessage("the three regulations hold different entries (entry 99: in base)*");
    }

    [Fact]
    public void ListsAtMostFiveEntries()
    {
        var act = () => BinderEntrySets.RequireSameEntries(Enumerable.Range(0, 8), [], [], "archives");
        act.Should().Throw<MergeException>().WithMessage("*entry 4: in base*(and 3 more)*");
    }

    [Fact]
    public void ThrowsWhatTheCallerAsksFor()
    {
        var act = () => BinderEntrySets.RequireSameEntries([1], [], [], "archives", message => new FmgMergeException(message));
        act.Should().Throw<FmgMergeException>();
    }
}
