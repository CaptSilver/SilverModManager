using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.LeftMenu.Loadout;
using SilverModManager.App.UI.Settings;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Settings;

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

    private static int LastRow(Window window) => ((Grid)((Border)window.Content!).Child!).RowDefinitions.Count - 1;

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

    private static Color ThemeColor(string key) => ((SolidColorBrush)Application.Current!.FindResource(key)!).Color;

    // WCAG relative luminance and contrast ratio, so "clearly darker" is a number rather than a hex comparison.
    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    private static double Contrast(Color a, Color b)
    {
        var (light, dark) = Luminance(a) >= Luminance(b) ? (Luminance(a), Luminance(b)) : (Luminance(b), Luminance(a));
        return (light + 0.05) / (dark + 0.05);
    }

    [Fact]
    public async Task TheWindowBaseIsItsOwnDarkSurface()
    {
        await OnUi(() =>
        {
            var window = new MainWindow();

            Panel(window, "WindowFrame").Background.Should().BeOfType<SolidColorBrush>().Which.Color.Should().Be(ThemeColor("SurfaceWindowBrush"));
            Panel(window, "WindowFrame").ClipToBounds.Should().BeTrue("content is cut to the frame's corners");
            window.Background.Should().BeAssignableTo<ISolidColorBrush>().Which.Color.A.Should().Be(0, "only the frame paints, so its corners can show the desktop");
            Panel(window, "SpineBorder").Background.Should().BeNull("the spine sits on the window base");
        });
    }

    [Theory]
    [InlineData(WindowState.Normal, "Transparent", 8)]
    [InlineData(WindowState.Normal, "Blur", 8)]
    [InlineData(WindowState.Normal, "None", 0)]
    [InlineData(WindowState.Maximized, "Transparent", 0)]
    [InlineData(WindowState.FullScreen, "Blur", 0)]
    [InlineData(WindowState.Minimized, "Transparent", 8)]
    public void TheWindowIsRoundedOnlyWhenFloatingOnACompositor(WindowState state, string level, double radius)
    {
        // WindowTransparencyLevel is a struct, so the cases name it.
        var actual = level switch
        {
            "Transparent" => WindowTransparencyLevel.Transparent,
            "Blur" => WindowTransparencyLevel.Blur,
            _ => WindowTransparencyLevel.None,
        };
        // Without transparency the corners would be filled black; a window that fills the screen has square corners.
        MainWindow.FrameCornerRadiusFor(state, actual).Should().Be(new CornerRadius(radius));
    }

    [Fact]
    public async Task TheFrameRoundsWhileFloatingAndSquaresWhenMaximized()
    {
        await OnUi(() =>
        {
            var window = Track(new MainWindow());
            using var attachment = window.AttachGlass(Provider.GetRequiredService<GlassAppearance>());
            ShowRendered(window);
            var frame = Panel(window, "WindowFrame");

            window.TransparencyLevelHint.Should().Contain(WindowTransparencyLevel.Transparent, "rounded corners need a transparent window even with Glass off");
            window.ActualTransparencyLevel.Should().Be(WindowTransparencyLevel.Transparent, "the headless platform grants plain transparency");
            frame.CornerRadius.Should().Be(new CornerRadius(8));

            window.WindowState = WindowState.Maximized;
            frame.CornerRadius.Should().Be(new CornerRadius(0));

            window.WindowState = WindowState.Normal;
            frame.CornerRadius.Should().Be(new CornerRadius(8));
        });
    }

    [Fact]
    public async Task TheGuttersReadClearlyDarkerThanThePanels()
    {
        await OnUi(() =>
        {
            Contrast(ThemeColor("SurfaceWindowBrush"), ThemeColor("SurfaceLowBrush"))
                .Should().BeGreaterThanOrEqualTo(1.25, "the dark base between the panels has to be visible at a glance");
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
    public async Task UnderGlassThePanelsCoverMoreOfTheDesktopThanTheWindowBase()
    {
        await OnUi(() =>
        {
            var frame = GlassAppearance.TintedBackgroundFor(true, WindowTransparencyLevel.Blur, GlassAppearance.DefaultTint, SurfaceLow());
            var panel = GlassAppearance.PanelBackgroundFor(true, WindowTransparencyLevel.Blur, GlassAppearance.DefaultTint, SurfaceLow());

            ((SolidColorBrush)panel!).Color.A.Should().BeGreaterThan(((SolidColorBrush)frame!).Color.A, "text sits on the panels, so they cover more of the desktop than the gutters do");
            ((SolidColorBrush)panel).Color.A.Should().BeLessThan(255, "the blurred desktop shows faintly through the panels too");
        });
    }

    [Fact]
    public async Task WithoutBlurThePanelsStayOpaqueUnderGlass()
    {
        await OnUi(() =>
        {
            var window = new MainWindow();
            var settings = Provider.GetRequiredService<ISettingsManager>();
            settings.Update<AppearanceSettings>(s => s with { GlassEnabled = true });
            try
            {
                using var attachment = window.AttachGlass(Provider.GetRequiredService<GlassAppearance>());

                GlassAppearance.IsBlurred(window.ActualTransparencyLevel).Should().BeFalse("the headless platform has no blur");
                foreach (var name in new[] { "LeftMenuBorder", "WorkspacesBorder" })
                    Panel(window, name).Background.Should().BeOfType<SolidColorBrush>().Which.Color.Should().Be(SurfaceLow());
            }
            finally
            {
                settings.Update<AppearanceSettings>(_ => new AppearanceSettings());
            }
        });
    }

    private static Color? BackgroundColor(IBrush? brush) => (brush as ISolidColorBrush)?.Color;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AWorkspacePanelLetsThePageAreaBehindItShowThrough(bool oneTab)
    {
        await OnUi(() =>
        {
            var panel = new PanelView();
            // Set from the view model's tab count in the app; there is no view model here.
            ((IPseudoClasses)typeof(StyledElement).GetProperty("PseudoClasses", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(panel)!)
                .Set(":one-tab", oneTab);
            var window = Track(new Window { Width = 600, Height = 400, Content = panel });
            ShowRendered(window);

            foreach (var name in new[] { "PanelClipBorder", "TabHeaderBorder" })
            {
                var border = panel.GetVisualDescendants().OfType<Border>().Single(b => b.Name == name);
                BackgroundColor(border.Background)?.A.Should().BeLessThan(255, $"{name} painted opaque hides the page panel's Glass tint");
            }
        });
    }

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
