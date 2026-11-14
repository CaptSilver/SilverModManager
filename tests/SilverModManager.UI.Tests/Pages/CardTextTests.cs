using FluentAssertions;
using SilverModManager.App.UI.Controls.Cards;

namespace SilverModManager.UI.Tests.Pages;

public class CardTextTests
{
    [Theory]
    [InlineData(-5, "0")]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1_000, "1K")]
    [InlineData(1_250, "1.2K")]
    [InlineData(12_345, "12K")]
    [InlineData(999_999, "999K")]
    [InlineData(1_000_000, "1M")]
    [InlineData(1_234_567, "1.2M")]
    [InlineData(45_600_000, "45M")]
    [InlineData(2_100_000_000, "2.1B")]
    public void CompactCount_AbbreviatesLargeNumbers(long count, string expected)
    {
        CardText.CompactCount(count).Should().Be(expected);
    }
}
