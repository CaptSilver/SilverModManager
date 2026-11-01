using System.Runtime.InteropServices;

namespace SilverModManager.App.UI.X11.Tests;

/// <summary>The libX11 calls the synthetic drag source makes, on a Display of its own.</summary>
internal static unsafe partial class Xlib
{
    private const string LibX11 = "libX11.so.6";

    [LibraryImport(LibX11)]
    public static partial int XInitThreads();

    [LibraryImport(LibX11, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint XOpenDisplay(string name);

    [LibraryImport(LibX11)]
    public static partial int XCloseDisplay(nint display);

    [LibraryImport(LibX11)]
    public static partial nint XDefaultRootWindow(nint display);

    [LibraryImport(LibX11)]
    public static partial nint XCreateSimpleWindow(nint display, nint parent, int x, int y, uint width, uint height, uint borderWidth, nuint border, nuint background);

    [LibraryImport(LibX11)]
    public static partial int XDestroyWindow(nint display, nint window);

    [LibraryImport(LibX11, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint XInternAtom(nint display, string name, int onlyIfExists);

    [LibraryImport(LibX11)]
    public static partial int XSetSelectionOwner(nint display, nint selection, nint owner, nint time);

    [LibraryImport(LibX11)]
    public static partial int XSelectInput(nint display, nint window, nint mask);

    [LibraryImport(LibX11)]
    public static partial int XPending(nint display);

    [LibraryImport(LibX11)]
    public static partial int XNextEvent(nint display, byte* xevent);

    [LibraryImport(LibX11)]
    public static partial int XChangeProperty(nint display, nint window, nint property, nint type, int format, int mode, byte* data, int elements);

    [LibraryImport(LibX11)]
    public static partial int XSendEvent(nint display, nint window, int propagate, nint mask, byte* xevent);

    [LibraryImport(LibX11)]
    public static partial int XFlush(nint display);

    [LibraryImport(LibX11)]
    public static partial int XSync(nint display, int discard);
}
