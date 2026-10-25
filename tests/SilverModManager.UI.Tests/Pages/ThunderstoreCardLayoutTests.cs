using FluentAssertions;
using SilverModManager.App.UI.Pages.Thunderstore;

namespace SilverModManager.UI.Tests.Pages;

public class ThunderstoreCardLayoutTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(100, 1)]
    [InlineData(260, 1)]
    [InlineData(520, 2)]
    [InlineData(1040, 4)]
    [InlineData(1039, 4)]
    [InlineData(1270, 5)]
    public void Columns_FollowTheWidthAndNeverDropBelowOne(double width, int expected)
        => ThunderstoreCardLayout.ColumnsFor(width).Should().Be(expected);

    [Fact]
    public void Chunk_SplitsIntoLinesOfTheColumnCountWithAShortLastLine()
    {
        var rows = Enumerable.Range(0, 7).Select(i => new ThunderstorePackageRow($"o-p{i}", "1.0.0", 0, false, "", null)).ToArray();

        var lines = ThunderstoreCardLayout.Chunk(rows, 3);

        lines.Select(l => l.Cards.Count).Should().Equal(3, 3, 1);
        lines.SelectMany(l => l.Cards).Should().Equal(rows);
    }

    [Fact]
    public void Chunk_OfNothing_IsEmpty() => ThunderstoreCardLayout.Chunk([], 4).Should().BeEmpty();
}
