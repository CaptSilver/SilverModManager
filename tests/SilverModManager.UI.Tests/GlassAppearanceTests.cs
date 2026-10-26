using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.App.UI.Pages.Settings;
using SilverModManager.App.UI.Settings;
using SilverModManager.App.UI.Controls.Settings.SettingEntries;
using SilverModManager.App.UI.Windows;
using SilverModManager.Sdk.Settings;
using SilverModManager.UI.Tests.Framework;

namespace SilverModManager.UI.Tests;

public class GlassAppearanceTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly Color Surface = Color.Parse("#14181c");

    [Fact]
    public void GlassIsOffByDefaultWithAFortyPercentTint()
    {
        var defaults = new AppearanceSettings();
        defaults.GlassEnabled.Should().BeFalse();
        defaults.GlassTint.Should().Be(0.4);
        GlassAppearance.TintAlpha(defaults.GlassTint).Should().Be(0x66);
    }

    [Theory]
    [InlineData(0.0, 0.2)]
    [InlineData(0.2, 0.2)]
    [InlineData(0.55, 0.55)]
    [InlineData(0.8, 0.8)]
    [InlineData(1.0, 0.8)]
    [InlineData(double.NaN, 0.4)]
    public void TheTintIsKeptInsideItsRange(double stored, double expected)
    {
        GlassAppearance.ClampTint(stored).Should().Be(expected);
    }

    [Fact]
    public void TheHintAsksForBlurOnlyWhenGlassIsOn()
    {
        GlassAppearance.HintFor(false).Should().Equal(WindowTransparencyLevel.None);
        GlassAppearance.HintFor(true).Should().Equal(WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent);
    }

    [Fact]
    public void WithoutBlurThereIsNoTint()
    {
        // Transparent alone is see-through without blur: unreadable over a busy desktop, so it falls back too.
        foreach (var actual in new[] { WindowTransparencyLevel.None, WindowTransparencyLevel.Transparent })
            GlassAppearance.TintedBackgroundFor(true, actual, 0.4, Surface).Should().BeNull();
    }

    [Fact]
    public void GlassOffHasNoTintEvenWhenTheCompositorBlurs()
    {
        GlassAppearance.TintedBackgroundFor(false, WindowTransparencyLevel.Blur, 0.4, Surface).Should().BeNull();
    }

    [Theory]
    [InlineData(0.4, 0x66)]
    [InlineData(0.0, 0x33)]
    [InlineData(1.0, 0xCC)]
    public async Task WithBlurTheSurfaceIsTintedAndNeverBelowTheFloor(double tint, byte alpha)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var brush = (SolidColorBrush)GlassAppearance.TintedBackgroundFor(true, WindowTransparencyLevel.Blur, tint, Surface)!;
            brush.Color.A.Should().Be(alpha);
            (brush.Color.R, brush.Color.G, brush.Color.B).Should().Be((Surface.R, Surface.G, Surface.B));
        });
    }

    [Theory]
    [InlineData("SurfaceMidBrush")]
    [InlineData("SurfaceHighBrush")]
    [InlineData("SurfaceInvertedBrush")]
    public async Task DataSurfaceBrushesStayOpaque(string key)
    {
        // The tint only ever reaches a window's own Background, so the surfaces data sit on are not driven down by it.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Application.Current!.TryFindResource(key, out var resource).Should().BeTrue();
            ((ISolidColorBrush)resource!).Color.A.Should().Be(255);
        });
    }

    [Fact]
    public async Task AWindowIsOpaqueWhereTheCompositorGivesNoBlur()
    {
        var settings = Provider.GetRequiredService<ISettingsManager>();
        var glass = Provider.GetRequiredService<GlassAppearance>();
        settings.Update<AppearanceSettings>(s => s with { GlassEnabled = true, GlassTint = 0.3 });
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // The main window sets SurfaceBaseBrush in its axaml; with no blur that is what stays painted.
                Application.Current!.TryFindResource("SurfaceBaseBrush", out var surface).Should().BeTrue();
                var window = new Window { Background = (IBrush)surface! };
                using var attachment = glass.Attach(window, "SurfaceBase");

                GlassAppearance.IsBlurred(window.ActualTransparencyLevel).Should().BeFalse("the headless platform has no blur");
                window.TransparencyLevelHint.Should().Equal(WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent);
                window.Background.Should().BeSameAs(surface);
                ((ISolidColorBrush)window.Background!).Color.A.Should().Be(255);
            });
        }
        finally
        {
            settings.Update<AppearanceSettings>(s => new AppearanceSettings());
        }
    }

    [Fact]
    public async Task WithoutBlurAWindowKeepsItsOwnBackground()
    {
        // The advanced installer sets no background and shows the theme's window colour; Glass must not repaint
        // it with a surface colour unless the compositor actually blurs.
        var settings = Provider.GetRequiredService<ISettingsManager>();
        var glass = Provider.GetRequiredService<GlassAppearance>();
        foreach (var enabled in new[] { false, true })
        {
            settings.Update<AppearanceSettings>(s => s with { GlassEnabled = enabled });
            try
            {
                await WindowHost.Flush();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var window = new Window();
                    var themed = window.Background;
                    var attachment = glass.Attach(window, "SurfaceLow");
                    window.Background.Should().BeSameAs(themed);
                    attachment.Dispose();
                    window.Background.Should().BeSameAs(themed);
                });
            }
            finally
            {
                settings.Update<AppearanceSettings>(_ => new AppearanceSettings());
            }
        }
    }

    [Fact]
    public async Task TheMainWindowViewModelFollowsTheSettingLive()
    {
        var settings = Provider.GetRequiredService<ISettingsManager>();
        settings.Update<AppearanceSettings>(_ => new AppearanceSettings());
        var viewModel = await Dispatcher.UIThread.InvokeAsync(() => Provider.GetRequiredService<MainWindowViewModel>());
        try
        {
            viewModel.TransparencyLevelHint.Should().Equal(WindowTransparencyLevel.None);

            settings.Update<AppearanceSettings>(s => s with { GlassEnabled = true });
            await WindowHost.Flush();
            viewModel.TransparencyLevelHint.Should().Equal(WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent);

            settings.Update<AppearanceSettings>(s => s with { GlassEnabled = false });
            await WindowHost.Flush();
            viewModel.TransparencyLevelHint.Should().Equal(WindowTransparencyLevel.None);
        }
        finally
        {
            settings.Update<AppearanceSettings>(_ => new AppearanceSettings());
        }
    }

    [Fact]
    public void TheSettingsPageOffersGlassAndATintSliderInOneSection()
    {
        var page = (SettingsPageViewModel)Provider.GetRequiredService<ISettingsPageViewModel>();

        var toggle = page.SettingEntries.Single(e => e.InteractionControlViewModel is ISettingToggleViewModel && e.Config.Options.DisplayName == "Glass");
        var slider = page.SettingEntries.Single(e => e.InteractionControlViewModel is ISettingSliderViewModel && e.Config.Options.DisplayName == "Glass tint");

        slider.Config.Options.Section.Should().Be(toggle.Config.Options.Section);
        var options = slider.Config.ContainerOptions.Should().BeOfType<SliderContainerOptions>().Subject;
        options.Minimum.Should().Be(GlassAppearance.MinTint);
        options.Maximum.Should().Be(GlassAppearance.MaxTint);
        toggle.Config.Options.DisplayName.Should().NotContain("Frosted");
        toggle.Config.Options.DescriptionFactory(false).Should().Contain("Desktop Effects");
    }

    [Fact]
    public async Task TheSliderControlShowsTheValueWithinTheSettingsBounds()
    {
        var page = (SettingsPageViewModel)Provider.GetRequiredService<ISettingsPageViewModel>();
        var viewModel = page.SettingEntries.Select(e => e.InteractionControlViewModel).OfType<ISettingSliderViewModel>().Single();

        var (control, window) = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var control = new SettingSliderControl { ViewModel = viewModel };
            var window = new Window { Content = control };
            window.Show();
            return (control, window);
        });
        await WindowHost.Flush();
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var slider = control.GetVisualDescendants().OfType<Slider>().Single();
                slider.Minimum.Should().Be(GlassAppearance.MinTint);
                slider.Maximum.Should().Be(GlassAppearance.MaxTint);
                slider.Value.Should().BeApproximately(viewModel.DoubleContainer.CurrentValue, 1e-9);
                control.GetVisualDescendants().OfType<TextBlock>().Single().Text.Should().Be(viewModel.ValueText);

                slider.Value = 0.6;
                viewModel.DoubleContainer.CurrentValue.Should().BeApproximately(0.6, 1e-9);
                viewModel.DoubleContainer.HasChanged.Should().BeTrue();
                viewModel.ValueText.Should().Be("60%");
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(window.Close);
        }
    }
}
