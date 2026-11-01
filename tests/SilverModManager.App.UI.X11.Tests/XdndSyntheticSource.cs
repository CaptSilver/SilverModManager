using System.Diagnostics;

namespace SilverModManager.App.UI.X11.Tests;

/// <summary>
/// A drag source that speaks XDND v5 from a Display of its own, so the receiver is exercised by the same messages KWin
/// or Qt send. Single-threaded: the test drives it, and <c>Wait*</c> pump the event queue, answering selection requests
/// as they arrive. xdotool can't do this; it has no XDND.
/// </summary>
internal sealed unsafe class XdndSyntheticSource : IDisposable
{
    private const nint PropertyChangeMask = 1 << 22;
    private const int PropModeReplace = 0;
    private const nint XaAtom = 4;

    private readonly SourceOffer _offer;
    private readonly Atoms _atoms;
    private readonly Queue<StatusReply> _statuses = new();
    private readonly Queue<FinishedReply> _finished = new();
    private nint _display;
    private IncrTransfer? _incr;

    public XdndSyntheticSource(string displayName, SourceOffer offer)
    {
        _offer = offer;
        _display = Xlib.XOpenDisplay(displayName);
        if (_display == 0) throw new InvalidOperationException($"Could not open X display {displayName}");

        _atoms = new Atoms(_display);
        Window = Xlib.XCreateSimpleWindow(_display, Xlib.XDefaultRootWindow(_display), 0, 0, 1, 1, 0, 0, 0);
        Xlib.XSetSelectionOwner(_display, _atoms.XdndSelection, Window, 0);
        Flush();
    }

    public nint Window { get; }

    public nint CopyAction => _atoms.ActionCopy;

    /// <summary>How many times the target asked for <c>text/uri-list</c>.</summary>
    public int ConversionRequests { get; private set; }

    /// <summary>XdndEnter, version 5. With <paramref name="typesInProperty"/> the five types sit in XdndTypeList (bit 0 set), which is how KWin always offers them; without, three are inline.</summary>
    public void Enter(nint target, bool typesInProperty = true)
    {
        nint[] types = [_atoms.TextPlain, _atoms.TextUriList, _atoms.KdeUriList, _atoms.MozUrl, _atoms.OctetStream];
        if (typesInProperty)
        {
            fixed (nint* data = types)
            {
                Xlib.XChangeProperty(_display, Window, _atoms.XdndTypeList, XaAtom, 32, PropModeReplace, (byte*)data, types.Length);
            }
        }

        var flags = (5 << 24) | (typesInProperty ? 1 : 0);
        Send(target, _atoms.XdndEnter, Window, flags, types[0], types[1], types[2]);
    }

    /// <summary>XdndPosition with CurrentTime, as KWin sends it.</summary>
    public void Position(nint target, int rootX, int rootY) =>
        Send(target, _atoms.XdndPosition, Window, 0, (rootX << 16) | rootY, 0, _atoms.ActionCopy);

    public void Drop(nint target) => Send(target, _atoms.XdndDrop, Window, 0, 0, 0, 0);

    public void Leave(nint target) => Send(target, _atoms.XdndLeave, Window, 0, 0, 0, 0);

    /// <summary>Pumps events (serving selection requests) until the target's XdndStatus arrives.</summary>
    public StatusReply WaitStatus(TimeSpan timeout)
    {
        Pump(() => _statuses.Count > 0, timeout, "XdndStatus");
        return _statuses.Dequeue();
    }

    public FinishedReply WaitFinished(TimeSpan timeout)
    {
        Pump(() => _finished.Count > 0, timeout, "XdndFinished");
        return _finished.Dequeue();
    }

    /// <summary>Serves events for a fixed time, for the cases where the test asserts that nothing else happens.</summary>
    public void PumpFor(TimeSpan duration)
    {
        var end = Stopwatch.StartNew();
        while (end.Elapsed < duration)
        {
            Drain();
            Thread.Sleep(2);
        }
    }

    /// <summary>Closes the connection: the window and the selection ownership go with it.</summary>
    public void Dispose()
    {
        if (_display == 0) return;
        Xlib.XCloseDisplay(_display);
        _display = 0;
    }

    private void Pump(Func<bool> done, TimeSpan timeout, string waitingFor)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            Drain();
            if (done()) return;
            if (elapsed.Elapsed > timeout) throw new TimeoutException($"No {waitingFor} within {timeout.TotalSeconds:0.#}s");
            Thread.Sleep(2);
        }
    }

    private void Drain()
    {
        byte* buffer = stackalloc byte[XEventBytes.Size];
        while (Xlib.XPending(_display) > 0)
        {
            Xlib.XNextEvent(_display, buffer);
            Handle(new XEventBytes(buffer));
        }
    }

    private void Handle(XEventBytes e)
    {
        switch (e.Type)
        {
            case XEventBytes.ClientMessage:
                HandleClientMessage(e);
                break;
            case XEventBytes.SelectionRequest:
                HandleSelectionRequest(e);
                break;
            case XEventBytes.PropertyNotify:
                HandlePropertyNotify(e);
                break;
        }
    }

    private void HandleClientMessage(XEventBytes e)
    {
        var type = e.Long(40);
        var l1 = e.Long(64);
        var l2 = e.Long(72);
        var l4 = e.Long(88);
        if (type == _atoms.XdndStatus) _statuses.Enqueue(new StatusReply((l1 & 1) != 0, (l1 & 2) != 0, l4));
        else if (type == _atoms.XdndFinished) _finished.Enqueue(new FinishedReply((l1 & 1) != 0, l2));
    }

    private void HandleSelectionRequest(XEventBytes request)
    {
        byte* reply = stackalloc byte[XEventBytes.Size];
        var isOurData = request.RequestSelection == _atoms.XdndSelection && request.RequestTarget == _atoms.TextUriList;
        if (isOurData) ConversionRequests++;

        if (isOurData is false || _offer.RefuseConversions)
        {
            SendEvent(request.RequestRequestor, XEventBytes.SelectionNotifyFor(reply, request, 0));
            return;
        }

        if (_offer.IncrChunkSize is { } chunk)
        {
            // Select first: the receiver's delete of the marker is what asks for the first chunk.
            Xlib.XSelectInput(_display, request.RequestRequestor, PropertyChangeMask);
            nint size = _offer.UriList.Length;
            Xlib.XChangeProperty(_display, request.RequestRequestor, request.RequestProperty, _atoms.Incr, 32, PropModeReplace, (byte*)&size, 1);
            _incr = new IncrTransfer(request.RequestRequestor, request.RequestProperty, chunk);
        }
        else
        {
            WriteBytes(request.RequestRequestor, request.RequestProperty, _offer.UriList);
        }

        SendEvent(request.RequestRequestor, XEventBytes.SelectionNotifyFor(reply, request, request.RequestProperty));
    }

    private void HandlePropertyNotify(XEventBytes e)
    {
        if (_incr is not { } transfer) return;
        if (e.PropertyDeleted is false || e.PropertyWindow != transfer.Requestor || e.PropertyAtom != transfer.Property) return;

        var remaining = _offer.UriList.Length - transfer.Offset;
        var count = Math.Min(transfer.ChunkSize, remaining);
        WriteBytes(transfer.Requestor, transfer.Property, _offer.UriList.AsSpan(transfer.Offset, count));
        transfer.Offset += count;

        // The zero-length chunk is the last one; whatever the receiver deletes after it is no business of ours.
        if (count == 0) _incr = null;
    }

    private void WriteBytes(nint window, nint property, ReadOnlySpan<byte> bytes)
    {
        fixed (byte* data = bytes.IsEmpty ? new byte[1] : bytes.ToArray())
        {
            Xlib.XChangeProperty(_display, window, property, _atoms.TextUriList, 8, PropModeReplace, data, bytes.Length);
        }
        Flush();
    }

    private void Send(nint target, nint messageType, nint l0, nint l1, nint l2, nint l3, nint l4)
    {
        byte* buffer = stackalloc byte[XEventBytes.Size];
        SendEvent(target, XEventBytes.ClientMessageTo(buffer, target, messageType, l0, l1, l2, l3, l4));
    }

    /// <summary>Propagate false and an empty mask: the server delivers it to the client that owns the window, as XDND and ICCCM require.</summary>
    private void SendEvent(nint window, XEventBytes e)
    {
        Xlib.XSendEvent(_display, window, 0, 0, e.Pointer);
        Flush();
    }

    private void Flush() => Xlib.XFlush(_display);

    private sealed class IncrTransfer(nint requestor, nint property, int chunkSize)
    {
        public nint Requestor { get; } = requestor;
        public nint Property { get; } = property;
        public int ChunkSize { get; } = chunkSize;
        public int Offset { get; set; }
    }

    private sealed class Atoms(nint display)
    {
        public nint XdndEnter { get; } = Xlib.XInternAtom(display, "XdndEnter", 0);
        public nint XdndPosition { get; } = Xlib.XInternAtom(display, "XdndPosition", 0);
        public nint XdndStatus { get; } = Xlib.XInternAtom(display, "XdndStatus", 0);
        public nint XdndLeave { get; } = Xlib.XInternAtom(display, "XdndLeave", 0);
        public nint XdndDrop { get; } = Xlib.XInternAtom(display, "XdndDrop", 0);
        public nint XdndFinished { get; } = Xlib.XInternAtom(display, "XdndFinished", 0);
        public nint XdndTypeList { get; } = Xlib.XInternAtom(display, "XdndTypeList", 0);
        public nint XdndSelection { get; } = Xlib.XInternAtom(display, "XdndSelection", 0);
        public nint ActionCopy { get; } = Xlib.XInternAtom(display, "XdndActionCopy", 0);
        public nint TextUriList { get; } = Xlib.XInternAtom(display, "text/uri-list", 0);
        public nint TextPlain { get; } = Xlib.XInternAtom(display, "text/plain", 0);
        public nint KdeUriList { get; } = Xlib.XInternAtom(display, "application/x-kde4-urilist", 0);
        public nint MozUrl { get; } = Xlib.XInternAtom(display, "text/x-moz-url", 0);
        public nint OctetStream { get; } = Xlib.XInternAtom(display, "application/octet-stream", 0);
        public nint Incr { get; } = Xlib.XInternAtom(display, "INCR", 0);
    }
}
