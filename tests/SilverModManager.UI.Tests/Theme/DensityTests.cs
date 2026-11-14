using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using FluentAssertions;
using SilverModManager.App.UI.Settings;
using SilverModManager.App.UI.Windows;
using SilverModManager.UI.Tests.Framework;

namespace SilverModManager.UI.Tests.Theme;

// The Couch merge changes the one Application's resources, and showing a window pumps the dispatcher, so a page
// snapshot rendering in parallel would be drawn at Couch sizes.
[Collection(HeadlessUi.Collection)]
public class DensityTests(IServiceProvider provider) : AUiTest(provider)
{
    [Theory]
    [InlineData(UiDensity.Auto, true, UiDensity.Couch)]
    [InlineData(UiDensity.Auto, false, UiDensity.Desktop)]
    [InlineData(UiDensity.Desktop, true, UiDensity.Desktop)]
    [InlineData(UiDensity.Couch, false, UiDensity.Couch)]
    public void ResolveTurnsAutoIntoCouchOnlyInGameMode(UiDensity setting, bool gameMode, UiDensity expected)
    {
        DensityService.Resolve(setting, gameMode).Should().Be(expected);
    }

    // A TextBlock carrying the theme, shown in a window, reads the size the theme setter points at.
    private double HeadingSize()
    {
        var theme = (ControlTheme)Application.Current!.FindResource("Heading2XLSemiTheme")!;
        var text = new TextBlock { Text = "Heading", Theme = theme };
        var window = Track(new Window { Content = text });
        ShowRendered(window);
        return text.FontSize;
    }

    [Fact]
    public async Task ATextBlockCreatedAfterTheCouchMergeGetsTheCouchHeadingSize()
    {
        var (desktop, couch, after) = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var desktop = HeadingSize();
            double couch;
            using (DensityService.Apply(Application.Current!, UiDensity.Couch)) couch = HeadingSize();
            return (desktop, couch, HeadingSize());
        });

        desktop.Should().Be(60);
        couch.Should().Be(80);
        after.Should().Be(60, "disposing the merge puts the Desktop sizes back");
    }

    [Fact]
    public async Task DesktopMergesNothing()
    {
        var size = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            using (DensityService.Apply(Application.Current!, UiDensity.Desktop)) return HeadingSize();
        });

        size.Should().Be(60);
    }
}
