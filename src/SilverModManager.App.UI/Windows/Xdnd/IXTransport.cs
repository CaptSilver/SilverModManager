using System.Runtime.InteropServices;
using Avalonia;

namespace SilverModManager.App.UI.Windows.Xdnd;

/// <summary>The X requests the receiver makes, on a connection of our own. One instance per process; UI thread only.</summary>
public interface IXTransport : IDisposable
{
    /// <summary>The root window of the default screen; targets of XTranslateCoordinates.</summary>
    nint RootWindow { get; }

    nint InternAtom(string name);
    string? AtomName(nint atom);

    /// <summary>XChangeProperty, type ATOM, format 32, PropModeReplace.</summary>
    void SetAtoms(nint window, nint property, ReadOnlySpan<nint> atoms);

    /// <summary>XGetWindowProperty of the whole property (AnyPropertyType); null when the window or property is gone.
    /// <paramref name="delete"/> removes it in the same request (how INCR chunks are acknowledged).</summary>
    XProperty? GetProperty(nint window, nint property, bool delete);

    void DeleteProperty(nint window, nint property);

    /// <summary>XSendEvent of a format-32 ClientMessage with propagate False and event_mask 0, as XDND requires.</summary>
    void SendClientMessage(nint destination, nint messageType, nint l0, nint l1, nint l2, nint l3, nint l4);

    void ConvertSelection(nint selection, nint target, nint property, nint requestor, nint time);

    /// <summary>XTranslateCoordinates from the root to <paramref name="window"/>; null when the window is gone.</summary>
    PixelPoint? FromRoot(nint window, int rootX, int rootY);

    void Flush();
}

/// <summary>A property as the server returned it. Format-32 data is C longs (pointer-sized); <see cref="Longs"/> decodes them.</summary>
public sealed record XProperty(nint Type, int Format, byte[] Bytes)
{
    public nint[] Longs()
    {
        if (Format != 32) throw new InvalidOperationException($"Property has format {Format}, only format 32 holds longs");
        return MemoryMarshal.Cast<byte, nint>(Bytes).ToArray();
    }
}
