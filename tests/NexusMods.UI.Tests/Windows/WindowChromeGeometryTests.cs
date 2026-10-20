using Avalonia;
using Avalonia.Controls;
using FluentAssertions;
using NexusMods.App.UI.Windows;

namespace NexusMods.UI.Tests.Windows;

public class WindowChromeGeometryTests
{
    private static readonly Size WindowSize = new(1000, 700);

    [Theory]
    [InlineData(500, 2, WindowEdge.North)]
    [InlineData(500, 698, WindowEdge.South)]
    [InlineData(2, 350, WindowEdge.West)]
    [InlineData(998, 350, WindowEdge.East)]
    [InlineData(2, 2, WindowEdge.NorthWest)]
    [InlineData(998, 2, WindowEdge.NorthEast)]
    [InlineData(2, 698, WindowEdge.SouthWest)]
    [InlineData(998, 698, WindowEdge.SouthEast)]
    // Just past the grip along the edge but still inside the corner zone.
    [InlineData(2, 10, WindowEdge.NorthWest)]
    [InlineData(10, 2, WindowEdge.NorthWest)]
    [InlineData(990, 698, WindowEdge.SouthEast)]
    public void GetResizeEdge_NearBorder_MapsToMatchingEdge(double x, double y, WindowEdge expected)
    {
        WindowChrome.GetResizeEdge(new Point(x, y), WindowSize, WindowState.Normal).Should().Be(expected);
    }

    [Theory]
    [InlineData(500, 350)]
    [InlineData(500, 20)]
    [InlineData(20, 350)]
    [InlineData(500, WindowChrome.GripThickness + 1)]
    [InlineData(WindowChrome.GripThickness + 1, 350)]
    public void GetResizeEdge_InsideWindow_ReturnsNull(double x, double y)
    {
        WindowChrome.GetResizeEdge(new Point(x, y), WindowSize, WindowState.Normal).Should().BeNull();
    }

    [Theory]
    [InlineData(WindowState.Maximized)]
    [InlineData(WindowState.FullScreen)]
    [InlineData(WindowState.Minimized)]
    public void GetResizeEdge_NotNormal_GripsInactive(WindowState state)
    {
        WindowChrome.GetResizeEdge(new Point(2, 2), WindowSize, state).Should().BeNull();
        WindowChrome.GetResizeEdge(new Point(500, 2), WindowSize, state).Should().BeNull();
    }

    [Fact]
    public void GetResizeEdge_OutsideWindow_ReturnsNull()
    {
        WindowChrome.GetResizeEdge(new Point(-3, 100), WindowSize, WindowState.Normal).Should().BeNull();
        WindowChrome.GetResizeEdge(new Point(500, 705), WindowSize, WindowState.Normal).Should().BeNull();
    }
}
