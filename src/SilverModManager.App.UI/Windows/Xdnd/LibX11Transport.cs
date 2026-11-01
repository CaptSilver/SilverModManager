using System.Runtime.InteropServices;
using Avalonia;

namespace SilverModManager.App.UI.Windows.Xdnd;

/// <summary>
/// libX11 over <c>LibraryImport</c>, the style of <see cref="BlurRegion"/>: a Display of our own that only sends
/// requests. Nothing selects input on it or reads events from it, so the only thing that can arrive is an X error,
/// which Avalonia's process-wide handler absorbs. It must be opened after Avalonia's <c>XInitThreads</c>, which is why
/// <see cref="XdndReceiver"/> opens it at the first window and not earlier. UI thread only.
/// </summary>
public sealed unsafe partial class LibX11Transport : IXTransport
{
    private const string LibX11 = "libX11.so.6";
    private const nint XaAtom = 4;
    private const int PropModeReplace = 0;
    private const nint AnyPropertyType = 0;

    // A property read takes the whole value in one request: the length is in 32-bit units and well above any URI list.
    private const nint WholeProperty = 0x1FFFFFFF;

    private nint _display;

    private LibX11Transport(nint display)
    {
        _display = display;
        RootWindow = XDefaultRootWindow(display);
    }

    /// <param name="displayName">The display to open, always explicit. Null or blank returns null without any libX11 call:
    /// <c>XOpenDisplay(NULL)</c> would read the native environment, which a test cannot change from .NET.</param>
    /// <returns>null when there is no name, no libX11, or the server refuses the connection.</returns>
    public static LibX11Transport? TryOpen(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return null;

        try
        {
            var display = XOpenDisplay(displayName);
            return display == 0 ? null : new LibX11Transport(display);
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    public nint RootWindow { get; }

    public nint InternAtom(string name) => XInternAtom(Open(), name, onlyIfExists: false);

    public string? AtomName(nint atom)
    {
        if (atom == 0) return null;

        var name = XGetAtomName(Open(), atom);
        if (name == 0) return null;
        try
        {
            return Marshal.PtrToStringUTF8(name);
        }
        finally
        {
            XFree(name);
        }
    }

    public void SetAtoms(nint window, nint property, ReadOnlySpan<nint> atoms)
    {
        fixed (nint* data = atoms)
        {
            XChangeProperty(Open(), window, property, XaAtom, 32, PropModeReplace, data, atoms.Length);
        }
    }

    public XProperty? GetProperty(nint window, nint property, bool delete)
    {
        var status = XGetWindowProperty(Open(), window, property, 0, WholeProperty, delete, AnyPropertyType,
            out var type, out var format, out var items, out _, out var data);
        if (status != 0) return null;

        try
        {
            return XPropertyDecoder.Decode(type, format, items, data);
        }
        finally
        {
            if (data != 0) XFree(data);
        }
    }

    public void DeleteProperty(nint window, nint property) => XDeleteProperty(Open(), window, property);

    public void SendClientMessage(nint destination, nint messageType, nint l0, nint l1, nint l2, nint l3, nint l4)
    {
        var display = Open();
        using var message = new XClientMessageBuffer(display, destination, messageType, l0, l1, l2, l3, l4);
        XSendEvent(display, destination, propagate: false, eventMask: 0, message.Pointer);
    }

    public void ConvertSelection(nint selection, nint target, nint property, nint requestor, nint time) =>
        XConvertSelection(Open(), selection, target, property, requestor, time);

    public PixelPoint? FromRoot(nint window, int rootX, int rootY)
    {
        var translated = XTranslateCoordinates(Open(), RootWindow, window, rootX, rootY, out var x, out var y, out _);
        return translated ? new PixelPoint(x, y) : null;
    }

    public void Flush() => XFlush(Open());

    public void Dispose()
    {
        var display = Interlocked.Exchange(ref _display, 0);
        if (display != 0) XCloseDisplay(display);
    }

    private nint Open() => _display != 0 ? _display : throw new ObjectDisposedException(nameof(LibX11Transport));

    [LibraryImport(LibX11, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint XOpenDisplay(string name);

    [LibraryImport(LibX11)]
    private static partial int XCloseDisplay(nint display);

    [LibraryImport(LibX11)]
    private static partial nint XDefaultRootWindow(nint display);

    [LibraryImport(LibX11, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint XInternAtom(nint display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);

    [LibraryImport(LibX11)]
    private static partial nint XGetAtomName(nint display, nint atom);

    [LibraryImport(LibX11)]
    private static partial int XFree(nint data);

    // Format-32 property data is an array of C longs, which are pointer-sized on 64-bit Linux.
    [LibraryImport(LibX11)]
    private static partial int XChangeProperty(nint display, nint window, nint property, nint type, int format, int mode, nint* data, int elements);

    [LibraryImport(LibX11)]
    private static partial int XGetWindowProperty(nint display, nint window, nint property, nint offset, nint length,
        [MarshalAs(UnmanagedType.Bool)] bool delete, nint requestedType, out nint actualType, out int actualFormat,
        out nint items, out nint bytesAfter, out nint data);

    [LibraryImport(LibX11)]
    private static partial int XDeleteProperty(nint display, nint window, nint property);

    [LibraryImport(LibX11)]
    private static partial int XSendEvent(nint display, nint window, [MarshalAs(UnmanagedType.Bool)] bool propagate, nint eventMask, nint xevent);

    [LibraryImport(LibX11)]
    private static partial int XConvertSelection(nint display, nint selection, nint target, nint property, nint requestor, nint time);

    [LibraryImport(LibX11)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool XTranslateCoordinates(nint display, nint source, nint destination, int sourceX, int sourceY,
        out int destinationX, out int destinationY, out nint child);

    [LibraryImport(LibX11)]
    private static partial int XFlush(nint display);
}
