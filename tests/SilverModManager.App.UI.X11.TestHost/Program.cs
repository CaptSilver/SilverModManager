using Avalonia;
using SilverModManager.App.UI.X11.TestHost;

// Runs on the real Avalonia X11 backend, so the process must be started with the display it is to use in its
// environment: XOpenDisplay(NULL) reads the native environment. The test fixture starts it with an empty one.
AppBuilder.Configure<HostApp>()
    .UseX11()
    .UseSkia()
    .With(new X11PlatformOptions { UseDBusMenu = false, UseDBusFilePicker = false })
    .StartWithClassicDesktopLifetime(args);
