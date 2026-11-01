namespace SilverModManager.App.UI.X11.Tests;

/// <summary>
/// A 192-byte Xlib XEvent as bytes, with the 64-bit field offsets of the three event kinds an XDND source touches.
/// Offsets are Xlib's: <c>type</c> at 0, the common header to 32, event-specific fields after it.
/// </summary>
internal readonly unsafe ref struct XEventBytes
{
    public const int Size = 192;

    public const int PropertyNotify = 28;
    public const int SelectionRequest = 30;
    public const int SelectionNotify = 31;
    public const int ClientMessage = 33;

    private readonly byte* _p;

    public XEventBytes(byte* p) => _p = p;

    public byte* Pointer => _p;

    public int Type => *(int*)_p;

    public nint Long(int offset) => *(nint*)(_p + offset);

    public int Int(int offset) => *(int*)(_p + offset);

    public void SetLong(int offset, nint value) => *(nint*)(_p + offset) = value;

    public void SetInt(int offset, int value) => *(int*)(_p + offset) = value;

    public static XEventBytes Clear(byte* p)
    {
        new Span<byte>(p, Size).Clear();
        return new XEventBytes(p);
    }

    /// <summary>A format-32 ClientMessage for <paramref name="window"/>.</summary>
    public static XEventBytes ClientMessageTo(byte* p, nint window, nint messageType, nint l0, nint l1, nint l2, nint l3, nint l4)
    {
        var e = Clear(p);
        e.SetInt(0, ClientMessage);
        e.SetLong(32, window);
        e.SetLong(40, messageType);
        e.SetInt(48, 32);
        e.SetLong(56, l0);
        e.SetLong(64, l1);
        e.SetLong(72, l2);
        e.SetLong(80, l3);
        e.SetLong(88, l4);
        return e;
    }

    // SelectionRequest: owner 32, requestor 40, selection 48, target 56, property 64, time 72.
    public nint RequestRequestor => Long(40);
    public nint RequestSelection => Long(48);
    public nint RequestTarget => Long(56);
    public nint RequestProperty => Long(64);
    public nint RequestTime => Long(72);

    /// <summary>The SelectionNotify that answers a SelectionRequest; <paramref name="property"/> 0 refuses.</summary>
    public static XEventBytes SelectionNotifyFor(byte* p, XEventBytes request, nint property)
    {
        var e = Clear(p);
        e.SetInt(0, SelectionNotify);
        e.SetLong(32, request.RequestRequestor);
        e.SetLong(40, request.RequestSelection);
        e.SetLong(48, request.RequestTarget);
        e.SetLong(56, property);
        e.SetLong(64, request.RequestTime);
        return e;
    }

    // PropertyNotify: window 32, atom 40, time 48, state 56 (0 new value, 1 deleted).
    public nint PropertyWindow => Long(32);
    public nint PropertyAtom => Long(40);
    public bool PropertyDeleted => Int(56) == 1;
}
