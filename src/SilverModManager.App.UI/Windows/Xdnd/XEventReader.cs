namespace SilverModManager.App.UI.Windows.Xdnd;

/// <summary>
/// Reads the Xlib XEvent at a pointer (the layout Avalonia's XEvent mirrors, 64-bit: <c>type</c> at 0, <c>window</c>
/// at 32, the event-specific fields after it; AvaloniaX11ContractTests checks the offsets against Avalonia's own
/// structs). Null for every event that isn't XDND traffic, without allocating.
/// </summary>
public sealed unsafe class XEventReader(XdndAtoms atoms)
{
    private const int PropertyNotify = 28;
    private const int SelectionNotify = 31;
    private const int ClientMessage = 33;
    private const int PropertyDelete = 1;

    public XIncomingEvent? Read(nint xevent)
    {
        var p = (byte*)xevent;
        return *(int*)p switch
        {
            ClientMessage => ReadClientMessage(p),
            SelectionNotify => ReadSelectionNotify(p),
            PropertyNotify => ReadPropertyNotify(p),
            _ => null,
        };
    }

    private XIncomingEvent? ReadClientMessage(byte* p)
    {
        var messageType = *(nint*)(p + 40);
        if (IsTargetMessage(messageType) is false) return null;

        return new XIncomingEvent.ClientMessage(
            *(nint*)(p + 32), messageType, *(int*)(p + 48),
            *(nint*)(p + 56), *(nint*)(p + 64), *(nint*)(p + 72), *(nint*)(p + 80), *(nint*)(p + 88));
    }

    private XIncomingEvent? ReadSelectionNotify(byte* p)
    {
        var selection = *(nint*)(p + 40);
        if (selection != atoms.XdndSelection) return null;

        return new XIncomingEvent.SelectionNotify(*(nint*)(p + 32), selection, *(nint*)(p + 48), *(nint*)(p + 56), *(nint*)(p + 64));
    }

    private XIncomingEvent? ReadPropertyNotify(byte* p)
    {
        var atom = *(nint*)(p + 40);
        if (IsDataProperty(atom) is false) return null;

        return new XIncomingEvent.PropertyNotify(*(nint*)(p + 32), atom, *(nint*)(p + 48), *(int*)(p + 56) == PropertyDelete);
    }

    /// <summary>The four messages a drop target receives. Status and Finished only ever go to a source, which we never are.</summary>
    private bool IsTargetMessage(nint type) =>
        type == atoms.XdndEnter || type == atoms.XdndPosition || type == atoms.XdndLeave || type == atoms.XdndDrop;

    private bool IsDataProperty(nint atom)
    {
        foreach (var property in atoms.DataProperties)
        {
            if (property == atom) return true;
        }
        return false;
    }
}
