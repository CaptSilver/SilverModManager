using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using FluentAssertions;
using SilverModManager.Games.FOMOD.UI;
using SilverModManager.UI.Tests.Framework;

namespace SilverModManager.UI.Tests.Windows;

/// <summary>
/// The FOMOD installer window draws its own title and close button, like dialogs. On X11 the window manager adds a
/// second title bar unless the window opts out of system decorations, so the window has to move itself.
/// </summary>
[Collection(HeadlessUi.Collection)]
public class GuidedInstallerWindowChromeTests(IServiceProvider provider) : AUiTest(provider)
{
    private GuidedInstallerWindow ShowWindow()
    {
        var window = Track(new GuidedInstallerWindow { DataContext = new GuidedInstallerWindowViewModel(), Width = 640, Height = 480 });
        ShowRendered(window);
        return window;
    }

    [Fact]
    public async Task TheWindow_DrawsItsOwnChrome()
    {
        await OnUi(() =>
        {
            var window = ShowWindow();
            window.SystemDecorations.Should().Be(SystemDecorations.None);
        });
    }

    [Fact]
    public async Task PressingTheTitleArea_StartsAMove()
    {
        await OnUi(() =>
        {
            var window = ShowWindow();
            var moves = 0;
            window.BeginMove = _ => moves++;

            var titlebar = window.FindControl<Control>("Titlebar")!;
            var point = titlebar.TranslatePoint(new Point(4, 4), window)!.Value;
            window.MouseDown(point, MouseButton.Left);

            moves.Should().Be(1);
        });
    }

    [Fact]
    public async Task PressingTheCloseButtonOrTheContent_DoesNotStartAMove()
    {
        await OnUi(() =>
        {
            var window = ShowWindow();
            var moves = 0;
            window.BeginMove = _ => moves++;

            var close = window.FindControl<Control>("CloseButton")!;
            window.MouseDown(close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), window)!.Value, MouseButton.Left);
            var content = window.FindControl<Control>("ContentBorder")!;
            window.MouseDown(content.TranslatePoint(new Point(20, 20), window)!.Value, MouseButton.Left);

            moves.Should().Be(0);
        });
    }
}
