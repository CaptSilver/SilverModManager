using Avalonia.Controls.ApplicationLifetimes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.App;
using NSubstitute;

namespace SilverModManager.UI.Tests;

public class DesktopShutdownOnHostStopTests
{
    private sealed class FakeHostLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
    }

    [Fact]
    public void Test_StoppingTheHostClosesTheDesktopLifetimeOnceOnTheUiThread()
    {
        var host = new FakeHostLifetime();
        var desktop = Substitute.For<IClassicDesktopStyleApplicationLifetime>();
        var posted = new List<Action>();

        new DesktopShutdownOnHostStop(host, NullLogger<DesktopShutdownOnHostStop>.Instance).Attach(desktop, posted.Add);

        host.StopApplication();
        host.StopApplication();

        // Nothing touches the desktop lifetime until the UI thread runs the posted work.
        desktop.DidNotReceiveWithAnyArgs().TryShutdown(default);
        posted.Should().ContainSingle();
        posted[0]();
        desktop.Received(1).TryShutdown(0);
    }

    [Fact]
    public void Test_NothingHappensWhileTheHostKeepsRunning()
    {
        var desktop = Substitute.For<IClassicDesktopStyleApplicationLifetime>();
        var posted = new List<Action>();

        new DesktopShutdownOnHostStop(new FakeHostLifetime(), NullLogger<DesktopShutdownOnHostStop>.Instance).Attach(desktop, posted.Add);

        posted.Should().BeEmpty();
        desktop.DidNotReceiveWithAnyArgs().TryShutdown(default);
    }

    [Fact]
    public void Test_StoppingTheHostAfterTheDesktopExitedDoesNothing()
    {
        var host = new FakeHostLifetime();
        var desktop = Substitute.For<IClassicDesktopStyleApplicationLifetime>();
        var posted = new List<Action>();

        new DesktopShutdownOnHostStop(host, NullLogger<DesktopShutdownOnHostStop>.Instance).Attach(desktop, posted.Add);

        desktop.Exit += Raise.EventWith(new ControlledApplicationLifetimeExitEventArgs(0));
        host.StopApplication();

        posted.Should().BeEmpty();
        desktop.DidNotReceiveWithAnyArgs().TryShutdown(default);
    }

    [Fact]
    public void Test_OnlyTheWindowedGraphRegistersIt()
    {
        static IServiceCollection Graph(bool showUi, bool headlessRun) => new ServiceCollection().AddLogging().AddApp(startupMode: new StartupMode
        {
            RunAsMain = true,
            ShowUI = showUi,
            ExecuteCli = false,
            IsHeadlessRun = headlessRun,
        });

        Graph(showUi: true, headlessRun: false).Should().ContainSingle(d => d.ServiceType == typeof(DesktopShutdownOnHostStop));
        Graph(showUi: false, headlessRun: true).Should().NotContain(d => d.ServiceType == typeof(DesktopShutdownOnHostStop));
        Graph(showUi: false, headlessRun: false).Should().NotContain(d => d.ServiceType == typeof(DesktopShutdownOnHostStop));
    }
}
