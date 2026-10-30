using Avalonia.Controls;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SilverModManager.App;
using SilverModManager.App.UI.Controls.TopBar;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.UI.Tests;

public class ShutdownExplainerWindowTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class Entries : ILogger<ShutdownExplainer>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task ClosingThroughTheTopBarButton_IsLoggedAsTheUsersClose()
    {
        await OnUi(() =>
        {
            var log = new Entries();
            var explainer = new ShutdownExplainer(Substitute.For<IHostApplicationLifetime>(), Substitute.For<IJobMonitor>(), log);
            var topBar = new TopBarView { ViewModel = new TopBarDesignViewModel() };
            var window = Track(new Window { Content = topBar });
            explainer.Watch(window);
            ShowRendered(window);

            topBar.FindControl<Button>("CloseButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

            log.Messages.Should().ContainSingle(m => m.Contains("is closing")).Which.Should().Contain("closed by the user").And.Contain("top bar");
        });
    }

    [Fact]
    public async Task ClosingFromCode_IsLoggedAsTheApplicationsClose()
    {
        await OnUi(() =>
        {
            var log = new Entries();
            var explainer = new ShutdownExplainer(Substitute.For<IHostApplicationLifetime>(), Substitute.For<IJobMonitor>(), log);
            var window = Track(new Window());
            explainer.Watch(window);
            ShowRendered(window);

            window.Close();

            log.Messages.Should().ContainSingle(m => m.Contains("is closing")).Which.Should().Contain("closed by the application");
        });
    }
}
