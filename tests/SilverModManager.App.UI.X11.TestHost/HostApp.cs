using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using SilverModManager.App.UI.Windows.Xdnd;

namespace SilverModManager.App.UI.X11.TestHost;

/// <summary>One window with the real <see cref="XdndReceiver"/> attached, and a stdin watch that ends the process.</summary>
public sealed class HostApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;

        var report = new HostReport(Console.Out);
        var receiver = new XdndReceiver(new StderrLogger<XdndReceiver>(), new DispatcherUiTimer(), new LibX11TransportFactory(), new PlatformX11WindowHandles());
        var window = new DragProbeWindow(report);

        receiver.Attach(window);
        desktop.MainWindow = window;
        desktop.Exit += (_, _) => receiver.Dispose();
        ExitWhenStdinCloses(desktop);

        base.OnFrameworkInitializationCompleted();
    }

    private static void ExitWhenStdinCloses(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var watcher = new Thread(() =>
        {
            while (Console.In.Read() >= 0)
            {
            }
            Dispatcher.UIThread.Post(() => desktop.Shutdown());
        })
        {
            IsBackground = true,
            Name = "stdin watcher",
        };
        watcher.Start();
    }
}
