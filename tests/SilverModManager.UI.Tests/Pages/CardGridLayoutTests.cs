using FluentAssertions;
using SilverModManager.App.UI.Controls.Cards;

namespace SilverModManager.UI.Tests.Pages;

public class CardGridLayoutTests
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
        => CardGridLayout.ColumnsFor(width).Should().Be(expected);

    [Fact]
    public void Chunk_SplitsIntoLinesOfTheColumnCountWithAShortLastLine()
    {
        object[] rows = Enumerable.Range(0, 7).Select(i => (object)$"card {i}").ToArray();

        var lines = CardGridLayout.Chunk(rows, 3);

        lines.Select(l => l.Cards.Count).Should().Equal(3, 3, 1);
        lines.SelectMany(l => l.Cards).Should().Equal(rows);
    }

    [Fact]
    public void Chunk_CanStartPartWayIn()
    {
        object[] rows = Enumerable.Range(0, 7).Select(i => (object)i).ToArray();

        var lines = CardGridLayout.Chunk(rows, 3, startAt: 6);

        lines.Should().ContainSingle().Which.Cards.Should().Equal(6);
    }

    [Fact]
    public void Chunk_OfNothing_IsEmpty() => CardGridLayout.Chunk([], 4).Should().BeEmpty();
}
