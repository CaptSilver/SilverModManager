using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.App.UI.Windows;
using SilverModManager.UI.Tests.Framework;

namespace SilverModManager.UI.Tests.Windows;

/// <summary>The window tells its view model whether it is the active window, including a view model that arrives after the window is already active.</summary>
[Collection(HeadlessUi.GlassSettingsCollection)]
public class MainWindowActiveTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public async Task AViewModelAssignedAfterTheWindowIsActive_GetsTheCurrentActiveState()
    {
        var viewModel = await Dispatcher.UIThread.InvokeAsync(() => Provider.GetRequiredService<MainWindowViewModel>());

        var window = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var window = Track(new MainWindow());
            ShowRendered(window);
            window.Activate();
            Render(window);
            window.IsActive.Should().BeTrue("the headless window is the active one");
            window.ViewModel = viewModel;
            return window;
        });

        await EventuallyOnUi(() => viewModel.IsActive.Should().BeTrue());
        window.Should().NotBeNull();
    }
}
