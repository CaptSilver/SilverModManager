using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentAssertions;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.LeftMenu.Loadout;
using SilverModManager.App.UI.Windows;

namespace SilverModManager.UI.Tests.Windows;

/// <summary>Where the main window puts its parts: the bottom strips belong to the page area, not under the sidebars.</summary>
public class MainWindowLayoutTests(IServiceProvider provider) : AUiTest(provider)
{
    private static (int Column, int ColumnSpan, int Row, int RowSpan) Cell(Window window, string name)
    {
        var control = window.FindControl<Control>(name);
        control.Should().NotBeNull($"the main window has a part named {name}");
        return (Grid.GetColumn(control!), Grid.GetColumnSpan(control!), Grid.GetRow(control!), Grid.GetRowSpan(control!));
    }

    private static int LastRow(Window window) => ((Grid)window.Content!).RowDefinitions.Count - 1;

    [Theory]
    [InlineData("DevelopmentBuildBanner")]
    [InlineData("SecretStoreNotice")]
    public async Task ABottomStrip_SpansOnlyThePageColumn(string name)
    {
        await OnUi(() =>
        {
            var window = new MainWindow();
            var workspace = Cell(window, "WorkspacesBorder");

            var strip = Cell(window, name);

            strip.Column.Should().Be(workspace.Column);
            strip.ColumnSpan.Should().Be(1);
        });
    }

    [Theory]
    [InlineData("SpineBorder")]
    [InlineData("LeftMenuBorder")]
    public async Task ASidebar_RunsToTheBottomOfTheWindow(string name)
    {
        await OnUi(() =>
        {
            var window = new MainWindow();

            var sidebar = Cell(window, name);

            (sidebar.Row + sidebar.RowSpan - 1).Should().Be(LastRow(window));
        });
    }

    private static Border Panel(Window window, string name)
    {
        var border = window.FindControl<Border>(name);
        border.Should().NotBeNull($"the main window has a part named {name}");
        return border!;
    }

    private static Color SurfaceLow() =>
        ((SolidColorBrush)Application.Current!.FindResource("SurfaceLowBrush")!).Color;

    [Theory]
    [InlineData("LeftMenuBorder")]
    [InlineData("WorkspacesBorder")]
    public async Task APagePanel_IsRaisedOnTheLowSurfaceWithRoundedCorners(string name)
    {
        await OnUi(() =>
        {
            var panel = Panel(new MainWindow(), name);

            panel.Background.Should().BeOfType<SolidColorBrush>().Which.Color.Should().Be(SurfaceLow());
            panel.CornerRadius.TopLeft.Should().BeGreaterThan(0);
        });
    }

    [Fact]
    public async Task TheWindowBaseStaysTheDarkestSurface()
    {
        await OnUi(() =>
        {
            var window = new MainWindow();
            var baseColor = ((SolidColorBrush)Application.Current!.FindResource("SurfaceBaseBrush")!).Color;

            window.Background.Should().BeOfType<SolidColorBrush>().Which.Color.Should().Be(baseColor);
            Panel(window, "SpineBorder").Background.Should().BeNull("the spine sits on the window base");
        });
    }

    [Fact]
    public async Task ThePanelsAreSeparatedFromEachOtherAndTheWindowEdgeByAGutter()
    {
        await OnUi(() =>
        {
            var window = new MainWindow();
            var leftMenu = Panel(window, "LeftMenuBorder");
            var workspace = Panel(window, "WorkspacesBorder");

            leftMenu.Margin.Left.Should().BeGreaterThan(0, "a gutter between the spine and the left menu");
            leftMenu.Margin.Bottom.Should().BeGreaterThan(0);
            workspace.Margin.Left.Should().BeGreaterThan(0, "a gutter between the left menu and the page");
            workspace.Margin.Right.Should().BeGreaterThan(0);
        });
    }

    [Fact]
    public async Task GlassTintingTheWindowLeavesThePanelsOpaque()
    {
        await OnUi(() =>
        {
            var window = new MainWindow();
            var tint = GlassAppearance.TintedBackgroundFor(true, WindowTransparencyLevel.Blur, GlassAppearance.DefaultTint, SurfaceLow());
            tint.Should().NotBeNull();
            window.Background = tint;

            foreach (var name in new[] { "LeftMenuBorder", "WorkspacesBorder" })
            {
                var brush = Panel(window, name).Background.Should().BeOfType<SolidColorBrush>().Subject;
                brush.Color.A.Should().Be(255);
                brush.Opacity.Should().Be(1);
            }
        });
    }
    private static Color? BackgroundColor(IBrush? brush) => (brush as ISolidColorBrush)?.Color;

    [Fact]
    public async Task TheLeftMenusOwnHighlightsStandOutFromTheRaisedPanel()
    {
        await OnUi(() =>
        {
            var selected = new LeftMenuNavButton { IsSelected = true };
            var active = new LeftMenuNavButton { IsActive = true };
            var menu = new LoadoutLeftMenuView();
            var stack = new StackPanel { Children = { selected, active, menu } };
            var window = Track(new Window
            {
                Width = 300, Height = 800,
                Content = new Border { Background = new SolidColorBrush(SurfaceLow()), Child = stack },
            });
            ShowRendered(window);

            BackgroundColor(selected.Background).Should().NotBe(SurfaceLow(), "a selected item is lifted off the left menu");
            BackgroundColor(active.Background).Should().NotBe(SurfaceLow(), "an active item is lifted off the left menu");

            var separators = menu.GetVisualDescendants().OfType<Separator>().ToList();
            separators.Should().NotBeEmpty();
            separators.Should().AllSatisfy(separator =>
                BackgroundColor(separator.Background).Should().NotBe(SurfaceLow(), "a separator drawn in the panel colour is invisible"));
        });
    }
}
