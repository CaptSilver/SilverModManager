using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using SilverModManager.UI.Tests.Framework;

namespace SilverModManager.UI.Tests;

/// <summary>
/// A visible indeterminate bar animates for ever. A render tick has to come back anyway, or the tests that render
/// next to one (the Wabbajack page checking its list) would hold the whole host.
/// </summary>
[Collection(HeadlessUi.Collection)]
public class IndeterminateProgressRenderTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public async Task Render_WhileAnIndeterminateBarIsVisible_Returns()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var bar = new ProgressBar { Minimum = 0, Maximum = 1, IsVisible = false, MinWidth = 280, HorizontalAlignment = HorizontalAlignment.Left };
            var window = Track(new Window { Width = 300, Height = 100, Content = new StackPanel { Children = { bar } } });
            ShowRendered(window);

            // Shown only after the window, as the page does once its check starts.
            bar.IsIndeterminate = true;
            bar.IsVisible = true;
            for (var i = 0; i < 5; i++) Render(window);
        });
    }
}
