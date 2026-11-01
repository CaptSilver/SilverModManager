namespace SilverModManager.App.UI.Windows.Xdnd;

/// <summary>The XDND-relevant X events, copied out of Avalonia's event buffer so nothing downstream sees a pointer.</summary>
public abstract record XIncomingEvent
{
    public sealed record ClientMessage(nint Window, nint MessageType, int Format, nint L0, nint L1, nint L2, nint L3, nint L4) : XIncomingEvent;
    public sealed record SelectionNotify(nint Requestor, nint Selection, nint Target, nint Property, nint Time) : XIncomingEvent;
    public sealed record PropertyNotify(nint Window, nint Atom, nint Time, bool Deleted) : XIncomingEvent;
}
