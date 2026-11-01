using Avalonia;
using Avalonia.Input;
using Microsoft.Extensions.Logging;

namespace SilverModManager.App.UI.Windows.Xdnd;

/// <summary>One target window's XDND conversation, over a transport, a sink and a timer. One instance per window, UI thread only.</summary>
public sealed class XdndSession
{
    private const int TypeListBit = 1;

    /// <summary>How long an owed Status may wait for the data; per chunk during INCR.</summary>
    public static readonly TimeSpan DataTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Delay before asking again after a refused conversion while the Status is still owed (the KWin focus race).</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);


    private readonly nint _target;
    private readonly IXTransport _x;
    private readonly XdndAtoms _atoms;
    private readonly UriListParser _uris;
    private readonly IFileDropFactory _files;
    private readonly IXdndSink _sink;
    private readonly IUiTimer _timer;
    private readonly ILogger _logger;
    private readonly XdndActions _actions;
    private readonly XdndReplier _replies;

    private nint _source;
    private bool _hasFiles;
    private int _requests;
    private PixelPoint _at;
    private DragDropEffects _allowed;
    private DataObject? _data;

    // The fetch. _pendingProperty is the property the live request asked the source to write; 0 when no answer is
    // awaited (also while the files are being opened). _generation changes whenever the fetch is abandoned, so a
    // continuation that was awaiting the file factory can tell it is stale.
    private nint _pendingProperty;
    private nint _requestTime;
    private List<byte>? _incr;
    private IDisposable? _fetchTimeout;
    private IDisposable? _retryTimer;
    private int _generation;
    private bool _refusalLogged;

    public XdndSession(nint targetWindow, IXTransport x, XdndAtoms atoms, UriListParser uris, IFileDropFactory files,
        IXdndSink sink, IUiTimer timer, ILogger logger)
    {
        _target = targetWindow;
        _x = x;
        _atoms = atoms;
        _uris = uris;
        _files = files;
        _sink = sink;
        _timer = timer;
        _logger = logger;
        _actions = new XdndActions(atoms);
        _replies = new XdndReplier(targetWindow, x, atoms, _actions);
    }

    public XdndPhase Phase { get; private set; }

    public void Handle(XIncomingEvent e)
    {
        switch (e)
        {
            case XIncomingEvent.ClientMessage message:
                OnClientMessage(message);
                break;
            case XIncomingEvent.SelectionNotify notify:
                OnSelectionNotify(notify);
                break;
            case XIncomingEvent.PropertyNotify change:
                OnPropertyNotify(change);
                break;
        }
    }

    private void OnClientMessage(XIncomingEvent.ClientMessage message)
    {
        if (message.MessageType == _atoms.XdndEnter) OnEnter(message);
        else if (message.MessageType == _atoms.XdndPosition) OnPosition(message);
        else if (message.MessageType == _atoms.XdndLeave) OnLeave(message);
    }

    private void OnLeave(XIncomingEvent.ClientMessage message)
    {
        if (Phase != XdndPhase.Idle && message.L0 == _source) Forget();
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
        if (_hasFiles is false) LogOfferedTypes(types);
    }

    /// <summary>Naming a type is an X round trip on the UI thread, so the names are only fetched when Trace is on.</summary>
    private void LogOfferedTypes(nint[] types)
    {
        if (_logger.IsEnabled(LogLevel.Trace) is false) return;
        _logger.LogTrace("Drag offers no text/uri-list; types: {Types}", string.Join(", ", types.Select(NameOf)));
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
        else if (Phase == XdndPhase.Ready && _data is { } data) _replies.Status(_source, _sink.Over(_at, data, _allowed));
    }

    /// <summary>Asks for the uri list. No Status goes out: the source sends nothing more until it has one, and the answer will carry the window's verdict.</summary>
    private void RequestFiles(nint time)
    {
        _requestTime = time;
        SendConversion();
        _fetchTimeout = _timer.After(DataTimeout, OnDataTimeout);
        Phase = XdndPhase.FetchingData;
    }

    /// <summary>The data properties alternate per request, because the SelectionNotify that answers carries nothing else to tell the requests apart.</summary>
    private void SendConversion()
    {
        _pendingProperty = _atoms.DataProperties[_requests++ % _atoms.DataProperties.Count];
        _x.ConvertSelection(_atoms.XdndSelection, _atoms.TextUriList, _pendingProperty, _target, _requestTime);
        _x.Flush();
    }

    private void OnSelectionNotify(XIncomingEvent.SelectionNotify notify)
    {
        if (notify.Requestor != _target || notify.Selection != _atoms.XdndSelection || notify.Target != _atoms.TextUriList) return;
        if (notify.Property == 0)
        {
            if (Phase == XdndPhase.FetchingData && _pendingProperty != 0 && _incr is null) OnRefused();
            return;
        }
        if (_atoms.DataProperties.Contains(notify.Property) is false) return;
        if (Phase != XdndPhase.FetchingData || notify.Property != _pendingProperty)
        {
            _logger.LogTrace("Dropping the data of an abandoned request in property {Property}", notify.Property);
            _x.DeleteProperty(_target, notify.Property);
            _x.Flush();
            return;
        }
        ReadReply();
    }

    /// <summary>The value is read and deleted in one request. An INCR marker means the data follows in chunks, and deleting the marker is what starts them.</summary>
    private void ReadReply()
    {
        var value = _x.GetProperty(_target, _pendingProperty, delete: true);
        if (value is null)
        {
            OnRefused();
            return;
        }
        if (value.Type == _atoms.Incr)
        {
            _incr = [];
            DisposeRetry();
            ArmDataTimeout();
            return;
        }
        Complete(value.Bytes);
    }

    private void OnPropertyNotify(XIncomingEvent.PropertyNotify change)
    {
        if (_incr is null || change.Deleted || change.Window != _target || change.Atom != _pendingProperty) return;
        var chunk = _x.GetProperty(_target, _pendingProperty, delete: true);
        if (chunk is null) return;
        if (chunk.Bytes.Length == 0)
        {
            Complete(_incr.ToArray());
            return;
        }
        _incr.AddRange(chunk.Bytes);
        ArmDataTimeout();
    }

    /// <summary>The source refused this request. KWin does that while the window is not yet active, so ask again shortly while the Status is still owed.</summary>
    private void OnRefused()
    {
        _retryTimer ??= _timer.After(RetryDelay, OnRetry);
    }

    private void OnRetry()
    {
        _retryTimer = null;
        if (Phase == XdndPhase.FetchingData && _pendingProperty != 0 && _incr is null) SendConversion();
    }

    private void ArmDataTimeout()
    {
        _fetchTimeout?.Dispose();
        _fetchTimeout = _timer.After(DataTimeout, OnDataTimeout);
    }

    private void OnDataTimeout()
    {
        _fetchTimeout = null;
        if (Phase == XdndPhase.FetchingData) GiveUp();
    }

    /// <summary>
    /// Answers the Status the source is waiting for with a refusal and goes back to Entered, so the next Position
    /// asks again. Without it the source would wait forever, and it sends nothing until it has the answer.
    /// </summary>
    private void GiveUp()
    {
        var property = _pendingProperty;
        var midTransfer = _incr is not null;
        CancelFetch();
        if (midTransfer)
        {
            _x.DeleteProperty(_target, property);
            _x.Flush();
        }
        if (_refusalLogged is false)
        {
            _refusalLogged = true;
            _logger.LogDebug("Drag source {Source} did not give its text/uri-list in time or refused it; refusing until the next position", _source);
        }
        _replies.Status(_source, DragDropEffects.None);
        Phase = XdndPhase.Entered;
    }

    private void Complete(byte[] bytes)
    {
        _incr = null;
        _pendingProperty = 0;
        DisposeRetry();
        _ = EnterSinkAsync(_uris.Parse(bytes), _generation);
    }

    /// <summary>
    /// Opens the files, then enters the window and sends the Status that has been owed since the first Position. The
    /// data timeout stays armed until the window has answered. A continuation that finds the fetch abandoned does nothing.
    /// </summary>
    private async Task EnterSinkAsync(UriListParse parse, int generation)
    {
        try
        {
            var drop = await _files.CreateAsync(parse.LocalPaths);
            if (generation != _generation) return;
            _fetchTimeout?.Dispose();
            _fetchTimeout = null;
            LogLeftOut(parse, drop);
            _data = drop.Data;
            var effect = _sink.Enter(_at, _data, _allowed);
            Phase = XdndPhase.Ready;
            _replies.Status(_source, effect);
        }
        catch (Exception ex)
        {
            if (generation != _generation) return;
            _logger.LogWarning(ex, "Could not hand the dragged files to the window; refusing the drag");
            GiveUp();
        }
    }

    private void LogLeftOut(UriListParse parse, FileDrop drop)
    {
        if (parse.Skipped.Count > 0)
            _logger.LogInformation("Left out of the drag: {Skipped}", string.Join("; ", parse.Skipped.Select(s => $"{s.Uri} ({s.Reason})")));
        if (drop.Missing.Count > 0)
            _logger.LogInformation("Could not open dragged paths: {Missing}", string.Join("; ", drop.Missing));
    }

    /// <summary>Stops everything waiting on the source: both timers, the transfer, and any continuation awaiting the file factory.</summary>
    private void CancelFetch()
    {
        _fetchTimeout?.Dispose();
        _fetchTimeout = null;
        DisposeRetry();
        _pendingProperty = 0;
        _incr = null;
        _generation++;
    }

    private void DisposeRetry()
    {
        _retryTimer?.Dispose();
        _retryTimer = null;
    }

    private void Forget()
    {
        CancelFetch();
        var wasEntered = Phase == XdndPhase.Ready;
        _source = 0;
        _hasFiles = false;
        _data = null;
        _refusalLogged = false;
        Phase = XdndPhase.Idle;
        if (wasEntered) _sink.Leave();
    }

    private string NameOf(nint atom) => _x.AtomName(atom) ?? atom.ToString();

    private static int RootX(nint packed) => (short)((packed >> 16) & 0xFFFF);

    private static int RootY(nint packed) => (short)(packed & 0xFFFF);
}
