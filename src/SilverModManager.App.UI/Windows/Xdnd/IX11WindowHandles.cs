using Avalonia.Controls;

namespace SilverModManager.App.UI.Windows.Xdnd;

/// <summary>The window's XID, or null when its handle descriptor isn't "XID". The seam that lets a headless test
/// reach the transport and contract paths with a made-up XID.</summary>
public interface IX11WindowHandles
{
    nint? XidOf(TopLevel window);
}

/// <summary>Reads the XID from the window's platform handle, the way <see cref="BlurRegion"/> does.</summary>
public sealed class PlatformX11WindowHandles : IX11WindowHandles
{
    public nint? XidOf(TopLevel window) =>
        window.TryGetPlatformHandle() is { HandleDescriptor: "XID" } handle ? handle.Handle : null;
}
