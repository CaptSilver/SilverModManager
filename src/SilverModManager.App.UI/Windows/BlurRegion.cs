using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;

namespace SilverModManager.App.UI.Windows;

/// <summary>
/// The area KWin blurs behind a window. Avalonia's X11 backend asks for blur behind the whole window rectangle
/// (_KDE_NET_WM_BLUR_BEHIND_REGION = 0), so a window with rounded corners shows square blurred corners outside its
/// curve. This writes the rounded shape instead, as the list of rectangles the property takes. Avalonia only writes
/// the property when blur is switched on or off, so the shape written after that stays until the next switch.
/// </summary>
public static partial class BlurRegion
{
    /// <summary>
    /// Rectangles covering <paramref name="size"/> minus the parts of its corners outside a circle of
    /// <paramref name="radius"/>: one-pixel rows across the top and bottom curves and one block between them.
    /// A pixel is covered when its centre is inside the curve.
    /// </summary>
    public static IReadOnlyList<PixelRect> Rounded(PixelSize size, int radius)
    {
        if (size.Width <= 0 || size.Height <= 0) return [];
        var r = Math.Min(radius, Math.Min(size.Width, size.Height) / 2);
        if (r <= 0) return [new PixelRect(0, 0, size.Width, size.Height)];

        var rects = new List<PixelRect>(2 * r + 1);
        for (var y = 0; y < r; y++)
        {
            var dy = r - (y + 0.5);
            var inset = (int)Math.Ceiling(r - Math.Sqrt(r * r - dy * dy) - 0.5);
            var width = size.Width - 2 * inset;
            if (width <= 0) continue;
            rects.Add(new PixelRect(inset, y, width, 1));
            rects.Add(new PixelRect(inset, size.Height - 1 - y, width, 1));
        }

        var middle = size.Height - 2 * r;
        if (middle > 0) rects.Add(new PixelRect(0, r, size.Width, middle));
        return rects;
    }

    /// <summary>The property's value: x, y, width and height of each rectangle in turn.</summary>
    public static nint[] ToCardinals(IReadOnlyList<PixelRect> region) =>
        region.SelectMany(r => new nint[] { r.X, r.Y, r.Width, r.Height }).ToArray();

    /// <summary>
    /// Writes the rounded region for <paramref name="window"/>'s current size. Call it only while KWin blurs the
    /// window; a window that isn't an X11 one is left alone.
    /// </summary>
    public static void Apply(Window window, double radius, ILogger logger)
    {
        if (window.TryGetPlatformHandle() is not { HandleDescriptor: "XID" } handle)
        {
            logger.LogTrace("Blur region not set: the window has no X11 handle");
            return;
        }

        var display = Display.Value;
        if (display == 0)
        {
            logger.LogTrace("Blur region not set: no X display connection");
            return;
        }

        var scale = window.RenderScaling;
        var size = PixelSize.FromSize(window.ClientSize, scale);
        var data = ToCardinals(Rounded(size, (int)Math.Round(radius * scale)));

        var atom = XInternAtom(display, "_KDE_NET_WM_BLUR_BEHIND_REGION", onlyIfExists: false);
        XChangeProperty(display, handle.Handle, atom, XaCardinal, format: 32, PropModeReplace, data, data.Length);
        XFlush(display);
        logger.LogTrace("Blur region set on window {Window:x}: {Width}x{Height} px, radius {Radius} px, {Rectangles} rectangles",
            handle.Handle, size.Width, size.Height, (int)Math.Round(radius * scale), data.Length / 4);
    }

    // A connection of our own: window properties live on the server, so any client may set them, and Avalonia's
    // own display connection is internal.
    private static readonly Lazy<nint> Display = new(() => XOpenDisplay(0));

    private const nint XaCardinal = 6;
    private const int PropModeReplace = 0;

    [LibraryImport("libX11.so.6")]
    private static partial nint XOpenDisplay(nint name);

    [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint XInternAtom(nint display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);

    // Format-32 property data is an array of C longs, which are pointer-sized on 64-bit Linux.
    [LibraryImport("libX11.so.6")]
    private static partial int XChangeProperty(nint display, nint window, nint property, nint type, int format, int mode, nint[] data, int elements);

    [LibraryImport("libX11.so.6")]
    private static partial int XFlush(nint display);
}
