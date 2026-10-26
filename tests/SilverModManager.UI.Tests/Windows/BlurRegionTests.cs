using Avalonia;
using FluentAssertions;
using SilverModManager.App.UI.Windows;

namespace SilverModManager.UI.Tests.Windows;

/// <summary>The area KWin blurs behind a window with rounded corners, as the rectangles its blur property takes.</summary>
public class BlurRegionTests
{
    // PixelRect.Contains counts the right and bottom edges as inside; the rectangles here are half-open pixel spans.
    private static bool InRect(PixelRect r, int x, int y) => x >= r.X && x < r.Right && y >= r.Y && y < r.Bottom;

    private static bool Covers(IReadOnlyList<PixelRect> region, int x, int y) => region.Any(r => InRect(r, x, y));

    [Fact]
    public void WithoutRoundingTheRegionIsTheWholeWindow()
    {
        BlurRegion.Rounded(new PixelSize(300, 200), 0).Should().Equal(new PixelRect(0, 0, 300, 200));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void TheCornerPixelsAreLeftOutAndTheEdgesBetweenThemAreCovered(int radius)
    {
        var size = new PixelSize(300, 200);
        var region = BlurRegion.Rounded(size, radius);

        foreach (var (x, y) in new[] { (0, 0), (size.Width - 1, 0), (0, size.Height - 1), (size.Width - 1, size.Height - 1) })
            Covers(region, x, y).Should().BeFalse($"corner pixel ({x},{y}) is outside the curve");

        Covers(region, size.Width / 2, 0).Should().BeTrue("the middle of the top edge");
        Covers(region, size.Width / 2, size.Height - 1).Should().BeTrue("the middle of the bottom edge");
        Covers(region, 0, size.Height / 2).Should().BeTrue("the middle of the left edge");
        Covers(region, size.Width - 1, size.Height / 2).Should().BeTrue("the middle of the right edge");
        Covers(region, radius, radius).Should().BeTrue("inside the curve");
    }

    [Fact]
    public void TheRegionFollowsACircleOfTheRadius()
    {
        const int radius = 8;
        var size = new PixelSize(100, 100);
        var region = BlurRegion.Rounded(size, radius);

        // A pixel centre inside the corner circle is covered, one outside it is not.
        for (var y = 0; y < radius; y++)
        for (var x = 0; x < radius; x++)
        {
            var dx = radius - (x + 0.5);
            var dy = radius - (y + 0.5);
            var inside = dx * dx + dy * dy <= radius * radius;
            Covers(region, x, y).Should().Be(inside, $"pixel ({x},{y}) of the top-left corner");
            Covers(region, size.Width - 1 - x, size.Height - 1 - y).Should().Be(inside, $"the mirrored pixel of the bottom-right corner");
        }
    }

    [Fact]
    public void RectanglesNeverOverlapOrLeaveTheWindow()
    {
        var size = new PixelSize(120, 40);
        var region = BlurRegion.Rounded(size, 8);

        region.Should().OnlyContain(r => r.Width > 0 && r.Height > 0 && r.X >= 0 && r.Y >= 0 && r.Right <= size.Width && r.Bottom <= size.Height);
        for (var y = 0; y < size.Height; y++)
        for (var x = 0; x < size.Width; x++)
            region.Count(r => InRect(r, x, y)).Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public void AWindowSmallerThanTwoRadiiShrinksTheRadius()
    {
        var region = BlurRegion.Rounded(new PixelSize(10, 6), 8);

        region.Should().NotBeEmpty();
        region.Should().OnlyContain(r => r.Right <= 10 && r.Bottom <= 6 && r.Width > 0 && r.Height > 0);
    }

    [Fact]
    public void ThePropertyValueIsXYWidthHeightPerRectangle()
    {
        BlurRegion.ToCardinals([new PixelRect(1, 2, 3, 4), new PixelRect(5, 6, 7, 8)]).Should().Equal(1, 2, 3, 4, 5, 6, 7, 8);
    }
}
