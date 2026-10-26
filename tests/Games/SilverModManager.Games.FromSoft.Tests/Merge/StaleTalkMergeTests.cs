using FluentAssertions;
using SilverModManager.Games.FromSoft.Merge;
using static SilverModManager.Games.FromSoft.Tests.Merge.StaleTalkFixture;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

public class StaleTalkMergeTests
{
    [Fact]
    public void ThePairMergesAgainstTheBuildTheyWereMadeFrom()
    {
        var act = () => EsdArchiveMerge.ThreeWay(Adder, Remover, Old, Traits);
        act.Should().NotThrow();
    }

    [Fact]
    public void ThePairDoesNotMergeAgainstAPatchedCopy()
    {
        var act = () => EsdArchiveMerge.ThreeWay(Adder, Remover, Patched, Traits);
        act.Should().Throw<EsdMergeException>().WithMessage("*calls machine 20*");
    }
}
