using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using FluentAssertions;
using SilverModManager.App.UI.Windows;
using SilverModManager.UI.Tests.Framework;

namespace SilverModManager.UI.Tests.Windows;

[Collection(HeadlessUi.Collection)]
public class WindowChromeResizeGripTests(IServiceProvider provider) : AUiTest(provider)
{
    private Window Shown(bool canResize)
    {
        var window = Track(new Window { Width = 300, Height = 200, CanResize = canResize, SystemDecorations = SystemDecorations.None, Content = new Border() });
        WindowChrome.AttachResizeGrips(window);
        ShowRendered(window);
        return window;
    }

    [Fact]
    public async Task TheBorderOfAResizableWindow_ShowsAResizeCursor()
    {
        await OnUi(() =>
        {
            var window = Shown(canResize: true);
            window.MouseMove(new Avalonia.Point(150, 1));
            window.Cursor.Should().NotBeNull();
        });
    }

    [Fact]
    public async Task TheBorderOfAFixedSizeWindow_ShowsNoResizeCursor()
    {
        await OnUi(() =>
        {
            var window = Shown(canResize: false);
            window.MouseMove(new Avalonia.Point(150, 1));
            window.Cursor.Should().BeNull();
        });
    }
}
