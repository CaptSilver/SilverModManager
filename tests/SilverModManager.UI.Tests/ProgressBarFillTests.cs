using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using SilverModManager.App.UI.Pages.Collections;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.UI.Tests.Framework;
using SilverModManager.UI.Tests.Pages;

namespace SilverModManager.UI.Tests;

/// <summary>
/// Reads how much of its track each of the two install bars (the collection header's and the Downloads tab's)
/// fills, the way a person sees it. Both only set a 0 to 1 value, so a bar at 0.5 has to fill half of its track
/// whatever happened to it before: hidden, indeterminate, or resized.
/// </summary>
[Collection(HeadlessUi.Collection)]
public class ProgressBarFillTests(IServiceProvider provider) : AUiTest(provider)
{
    private const string Locating = "Locating files in archives: 25871 of 51774";

    /// <summary>The share of the bar's track its indicator covers, from the laid-out sizes.</summary>
    private static double Fill(ProgressBar bar)
    {
        var indicator = bar.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Indicator");
        var track = indicator.GetVisualParent() as Visual ?? throw new InvalidOperationException("The indicator has no track");
        return track.Bounds.Width <= 0 ? double.NaN : indicator.Bounds.Width / track.Bounds.Width;
    }

    private static string Describe(ProgressBar bar)
    {
        var indicator = bar.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Indicator");
        return $"Value={bar.Value} bar={bar.Bounds.Width:F1} track={indicator.GetVisualParent()?.Bounds.Width:F1} indicator={indicator.Bounds.Width:F1}";
    }

    private async Task<(Window Window, ProgressBar Bar)> ShowBarAsTheDownloadsTabHasIt()
        => await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var bar = new ProgressBar { Minimum = 0, Maximum = 1, IsVisible = false, MinWidth = 280, HorizontalAlignment = HorizontalAlignment.Left };
            var window = Track(new Window { Width = 900, Height = 200, Content = new StackPanel { Margin = new Thickness(24, 8), Children = { bar } } });
            ShowRendered(window);
            return (window, bar);
        });

    [Fact]
    public async Task ABarShownAfterTheWindowFillsHalfWhenItsValueIsHalf()
    {
        var (window, bar) = await ShowBarAsTheDownloadsTabHasIt();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            bar.IsVisible = true;
            bar.Value = 0.5;
            Render(window);
            Fill(bar).Should().BeApproximately(0.5, 0.01, Describe(bar));
        });
    }

    [Fact]
    public async Task ABarThatWasIndeterminateFillsHalfWhenItsValueIsHalf()
    {
        var (window, bar) = await ShowBarAsTheDownloadsTabHasIt();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // The check before an install counts nothing at first; no render tick while it animates for ever.
            bar.IsIndeterminate = true;
            bar.IsVisible = true;
            bar.IsIndeterminate = false;
            bar.Value = 0.2;
            Render(window);
            bar.Value = 0.5;
            Render(window);
            Fill(bar).Should().BeApproximately(0.5, 0.01, Describe(bar));
        });
    }

    [Fact]
    public async Task ABarStillFillsHalfAfterItsWindowIsResized()
    {
        var (window, bar) = await ShowBarAsTheDownloadsTabHasIt();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            bar.IsVisible = true;
            bar.Value = 0.5;
            Render(window);
            window.Width = 1400;
            Render(window);
            bar.Value = 0.6;
            Render(window);
            Fill(bar).Should().BeApproximately(0.6, 0.01, Describe(bar));
        });
    }

    [Fact]
    public async Task AStretchedBarKeepsItsFillWhenItsWindowIsResizedWithoutANewValue()
    {
        var (window, bar) = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var bar = new ProgressBar { Minimum = 0, Maximum = 1 };
            var window = Track(new Window { Width = 900, Height = 200, Content = new StackPanel { Margin = new Thickness(24, 8), Children = { bar } } });
            ShowRendered(window);
            return (window, bar);
        });

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            bar.Value = 0.5;
            Render(window);
            window.Width = 1400;
            Render(window);
            Fill(bar).Should().BeApproximately(0.5, 0.01, Describe(bar));
            window.Width = 500;
            Render(window);
            Fill(bar).Should().BeApproximately(0.5, 0.01, Describe(bar));
        });
    }

    [Fact]
    public async Task ABarSetWhileItsPanelWasHiddenFillsHalfOnceThePanelIsShown()
    {
        var (window, panel, bar) = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var bar = new ProgressBar { Minimum = 0, Maximum = 1 };
            var panel = new StackPanel { IsVisible = false, Children = { new TextBlock { Text = "Writing files" }, bar } };
            var window = Track(new Window { Width = 900, Height = 200, Content = new Border { Child = panel } });
            ShowRendered(window);
            return (window, panel, bar);
        });

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            bar.Value = 0.5;
            Render(window);
            panel.IsVisible = true;
            Render(window);
            Fill(bar).Should().BeApproximately(0.5, 0.01, Describe(bar));
        });
    }

    [Fact]
    public async Task TheDownloadsTabBarFillsHalfForHalfOfTheArchivesChecked()
    {
        var viewModel = new WabbajackModlistDesignViewModel { CheckText = "Checking archives against your library: 25871 of 51774", CheckFraction = 25871d / 51774 };
        var (window, view) = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var view = new WabbajackModlistView { DataContext = viewModel };
            var window = Track(new Window { Width = 1200, Height = 900, Content = view });
            ShowRendered(window);
            return (window, view);
        });

        await EventuallyOnUi(() =>
        {
            Render(window);
            var bar = ViewProbe.Named<ProgressBar>(view, "StatusBar");
            bar.IsVisible.Should().BeTrue();
            Fill(bar).Should().BeApproximately(0.5, 0.01, Describe(bar));
        });
    }

    [Fact]
    public async Task TheCollectionHeaderBarFillsHalfForHalfOfTheItemsDone()
    {
        var viewModel = new CollectionLoadoutDesignViewModel { InstallProgress = new CollectionInstallStatus(Locating, 25871d / 51774) };
        var (window, view) = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var view = new CollectionLoadoutView { DataContext = viewModel };
            var window = Track(new Window { Width = 1200, Height = 900, Content = view });
            ShowRendered(window);
            return (window, view);
        });

        await EventuallyOnUi(() =>
        {
            Render(window);
            var bar = ViewProbe.Named<ProgressBar>(view, "InstallProgressBar");
            bar.IsVisible.Should().BeTrue();
            Fill(bar).Should().BeApproximately(0.5, 0.01, Describe(bar));
        });
    }
}
