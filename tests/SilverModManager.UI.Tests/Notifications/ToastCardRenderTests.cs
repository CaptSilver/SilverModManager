using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentAssertions;
using SilverModManager.App.UI.Notifications;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Tests.Framework;

namespace SilverModManager.UI.Tests.Notifications;

/// <summary>A toast card templated with the app theme, checked for the strip that tells a failure from a success.</summary>
[Collection(HeadlessUi.Collection)]
public class ToastCardRenderTests(IServiceProvider provider) : AUiTest(provider)
{
    [Theory]
    [InlineData(ToastNotificationVariant.Success, "SuccessStrongBrush")]
    [InlineData(ToastNotificationVariant.Failure, "DangerStrongBrush")]
    public async Task OutcomeToastsShowAStripInTheirColour(ToastNotificationVariant variant, string brushKey)
    {
        var bar = await HeaderBarOf(variant);

        bar.IsVisible.Should().BeTrue();
        bar.Background.Should().NotBeNull().And.BeSameAs(await ThemeResource(brushKey));
    }

    [Fact]
    public async Task NeutralToastsHaveNoStrip()
    {
        var bar = await HeaderBarOf(ToastNotificationVariant.Neutral);

        bar.IsVisible.Should().BeFalse();
    }

    private sealed record HeaderBar(bool IsVisible, IBrush? Background);

    /// <summary>Read on the UI thread, which owns the card's properties.</summary>
    private async Task<HeaderBar> HeaderBarOf(ToastNotificationVariant variant)
    {
        return await OnUi(() =>
        {
            var card = new NotificationCard { Content = WindowNotificationService.BuildNotification("Done.", null, variant) };
            var window = Track(new Window { Width = 500, Height = 200, Content = card });
            ShowRendered(window);

            var bar = card.GetVisualDescendants().OfType<Panel>().Single(p => p.Name == "PART_HeaderBar");
            return Task.FromResult(new HeaderBar(bar.IsVisible, bar.Background));
        });
    }

    private Task<object?> ThemeResource(string key) => OnUi(() => Task.FromResult(
        Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out var resource) ? resource : null));
}
