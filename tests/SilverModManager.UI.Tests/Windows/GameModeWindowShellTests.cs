using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia;
using FluentAssertions;
using SilverModManager.App.UI.Windows;

namespace SilverModManager.UI.Tests.Windows;

public class GameModeWindowShellTests(IServiceProvider provider) : AUiTest(provider)
{
    [Theory]
    [InlineData(true, WindowState.Maximized)]
    [InlineData(false, WindowState.Normal)]
    public async Task OnlyInGameModeTheWindowStartsMaximized(bool gameMode, WindowState expected)
    {
        var state = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var window = Track(new Window());
            GameModeWindowShell.Apply(window, gameMode);
            return window.WindowState;
        });

        state.Should().Be(expected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PopupsAreOverlaysOnlyInGameMode(bool gameMode)
    {
        var options = new X11PlatformOptions();

        GameModeWindowShell.Apply(options, gameMode);

        options.OverlayPopups.Should().Be(gameMode);
    }
}
