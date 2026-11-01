using Avalonia;
using Avalonia.Input;
using Microsoft.Extensions.Logging;

namespace SilverModManager.App.UI.Windows.Xdnd;

/// <summary>One target window's XDND conversation, over a transport and nothing else. One instance per window, UI thread only.</summary>
public sealed class XdndSession
{
    private const int MaxVersion = 5;
    private const int TypeListBit = 1;

    private readonly nint _target;
    private readonly IXTransport _x;
    private readonly XdndAtoms _atoms;
    private readonly ILogger _logger;
    private readonly XdndActions _actions;
    private readonly XdndReplier _replies;

    private nint _source;
    private bool _hasFiles;
    private int _requests;
    private PixelPoint _at;
    private DragDropEffects _allowed;

    public XdndSession(nint targetWindow, IXTransport x, XdndAtoms atoms, ILogger logger)
    {
        _target = targetWindow;
        _x = x;
        _atoms = atoms;
        _logger = logger;
        _actions = new XdndActions(atoms);
        _replies = new XdndReplier(targetWindow, x, atoms, _actions);
    }

    public XdndPhase Phase { get; private set; }

    public void Handle(XIncomingEvent e)
    {
        if (e is not XIncomingEvent.ClientMessage message) return;
        if (message.MessageType == _atoms.XdndEnter) OnEnter(message);
        else if (message.MessageType == _atoms.XdndPosition) OnPosition(message);
    }

    private void OnEnter(XIncomingEvent.ClientMessage message)
    {
        Forget();

        var source = message.L0;
        var types = OfferedTypes(source, message);
        if (types is null) return;

        _source = source;
        _hasFiles = types.Contains(_atoms.TextUriList);
        Phase = XdndPhase.Entered;
        if (_hasFiles is false) _logger.LogTrace("Drag offers no text/uri-list; types: {Types}", string.Join(", ", types.Select(NameOf)));
    }

    /// <summary>The three inline types, or the source's XdndTypeList when bit 0 says there are more; null when that list is unreadable (the source vanished).</summary>
    private nint[]? OfferedTypes(nint source, XIncomingEvent.ClientMessage message)
    {
        if ((message.L1 & TypeListBit) == 0) return new[] { message.L2, message.L3, message.L4 }.Where(static t => t != 0).ToArray();

        var list = _x.GetProperty(source, _atoms.XdndTypeList, delete: false);
        if (list is null)
        {
            _logger.LogDebug("Drag source {Source} has no XdndTypeList; ignoring its Enter", source);
            return null;
        }
        if (list.Format != 32)
        {
            _logger.LogDebug("Drag source {Source} wrote XdndTypeList as format {Format}, not 32; ignoring its Enter", source, list.Format);
            return null;
        }
        return list.Longs();
    }

    private void OnPosition(XIncomingEvent.ClientMessage message)
    {
        if (Phase == XdndPhase.Idle) return;
        if (message.L0 != _source)
        {
            _logger.LogDebug("Ignoring XdndPosition from {Window}, the drag source is {Source}", message.L0, _source);
            return;
        }

        var at = _x.FromRoot(_target, RootX(message.L2), RootY(message.L2));
        if (at is null)
        {
            Forget();
            return;
        }

        if (_hasFiles is false)
        {
            _replies.Status(_source, DragDropEffects.None);
            return;
        }

        _at = at.Value;
        _allowed = _actions.ToEffects(message.L4);
        if (Phase == XdndPhase.Entered) RequestFiles(message.L3);
    }

    /// <summary>Asks for the uri list. No Status goes out: the source sends nothing more until it has one, and the answer will carry the window's verdict.</summary>
    private void RequestFiles(nint time)
    {
        var property = _atoms.DataProperties[_requests++ % _atoms.DataProperties.Count];
        _x.ConvertSelection(_atoms.XdndSelection, _atoms.TextUriList, property, _target, time);
        _x.Flush();
        Phase = XdndPhase.FetchingData;
    }

    private void Forget()
    {
        _source = 0;
        _hasFiles = false;
        Phase = XdndPhase.Idle;
    }

    private string NameOf(nint atom) => _x.AtomName(atom) ?? atom.ToString();

    private static int RootX(nint packed) => (short)((packed >> 16) & 0xFFFF);

    private static int RootY(nint packed) => (short)(packed & 0xFFFF);
}
