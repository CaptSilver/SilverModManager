using FluentAssertions;

namespace SilverModManager.Abstractions.Games.Merge.Tests;

public class MergeLossTests
{
    [Fact]
    public void ALossNamingAStandInIsNoLoss()
    {
        MergeLoss.Between(MergeNames.OtherMod, "Alpha").Should().BeNull();
        MergeLoss.Between("Beta", MergeNames.Game).Should().BeNull();
    }

    [Fact]
    public void ALossBetweenTwoNamedModsKeepsBothNames()
        => MergeLoss.Between("Beta", "Alpha").Should().Be(new MergeLoss("Beta", "Alpha"));
}
