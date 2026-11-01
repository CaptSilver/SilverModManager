using Avalonia.Input;

namespace SilverModManager.App.UI.Windows.Xdnd;

/// <summary>Writes the messages a target sends back to a drag source. Every message carries our window in l[0] and is flushed at once, since the source is waiting on it.</summary>
public sealed class XdndReplier(nint targetWindow, IXTransport x, XdndAtoms atoms, XdndActions actions)
{
    private const int AcceptBit = 1;
    private const int ContinuousPositionsBit = 2;

    /// <summary>XdndStatus: accepted when <paramref name="effect"/> is anything but None, with that action. Bit 1 is always set (positions keep coming on every motion) and the rectangle is empty.</summary>
    public void Status(nint source, DragDropEffects effect)
    {
        var accepted = effect != DragDropEffects.None;
        var flags = ContinuousPositionsBit | (accepted ? AcceptBit : 0);
        x.SendClientMessage(source, atoms.XdndStatus, targetWindow, flags, 0, 0, accepted ? actions.ToAtom(effect) : 0);
        x.Flush();
    }
}
