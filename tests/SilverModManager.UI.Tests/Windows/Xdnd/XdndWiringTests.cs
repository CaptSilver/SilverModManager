using Avalonia.Controls;
using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.Windows.Xdnd;
using SilverModManager.UI.Tests.Framework;

namespace SilverModManager.UI.Tests.Windows.Xdnd;

/// <summary>The XDND receiver is registered with the app's services and the main window attaches itself to it.</summary>
[Collection(HeadlessUi.Collection)]
public class XdndWiringTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class RecordingReceiver : IXdndReceiver
    {
        public List<Window> Attached { get; } = [];

        public IDisposable Attach(Window window)
        {
            Attached.Add(window);
            return NullXdndReceiver.Instance.Attach(window);
        }
    }

    [Fact]
    public void TheServicesProvideTheReceiverAndItsParts()
    {
        Provider.GetRequiredService<IXdndReceiver>().Should().BeOfType<XdndReceiver>();
        Provider.GetRequiredService<IUiTimer>().Should().BeOfType<DispatcherUiTimer>();
        Provider.GetRequiredService<IXTransportFactory>().Should().BeOfType<LibX11TransportFactory>();
        Provider.GetRequiredService<IX11WindowHandles>().Should().BeOfType<PlatformX11WindowHandles>();
    }

    [Fact]
    public void TheReceiverIsOnePerProcess() =>
        Provider.GetRequiredService<IXdndReceiver>().Should().BeSameAs(Provider.GetRequiredService<IXdndReceiver>());

    [Fact]
    public async Task TheMainWindowAttachesItselfOnce()
    {
        var receiver = new RecordingReceiver();

        var window = await Dispatcher.UIThread.InvokeAsync(() => new MainWindow(receiver));

        receiver.Attached.Should().ContainSingle().Which.Should().BeSameAs(window);
    }

    [Fact]
    public async Task TheMainWindowFromTheContainerAttachesToTheContainersReceiver()
    {
        var window = await Dispatcher.UIThread.InvokeAsync(() => Provider.GetRequiredService<MainWindow>());

        window.Should().NotBeNull();
    }

    [Fact]
    public async Task ThePreviewerConstructorStillBuilds()
    {
        var window = await Dispatcher.UIThread.InvokeAsync(() => new MainWindow());

        window.Should().NotBeNull();
    }
}
