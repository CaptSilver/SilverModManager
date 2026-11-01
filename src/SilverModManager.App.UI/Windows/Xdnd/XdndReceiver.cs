using System.Reflection;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;

namespace SilverModManager.App.UI.Windows.Xdnd;

/// <summary>
/// Attaches the XDND receiver to the app's X11 windows. Everything unmanaged is opened lazily, at the first window
/// that has an XID: <c>XInitThreads</c> has to precede every other libX11 call in the process, and Avalonia makes it
/// during platform initialisation, so nothing here may open a display from a constructor. If the receiver cannot run
/// (no display, Avalonia already speaks XDND, Avalonia's internals moved) it stays off for the process and says why once.
/// </summary>
public sealed class XdndReceiver : IXdndReceiver, IDisposable
{
    private const string AvaloniaX11 = "Avalonia.X11";

    private readonly ILogger<XdndReceiver> _logger;
    private readonly IUiTimer _timer;
    private readonly IXTransportFactory _transports;
    private readonly IX11WindowHandles _handles;
    private readonly AvaloniaNativeXdnd _native;
    private readonly UriListParser _uris = new();
    private readonly Dictionary<nint, XdndWindowAttachment> _attachments = [];
    private readonly List<WindowSubscription> _subscriptions = [];

    private bool _started;
    private bool _disposed;
    private Runtime? _runtime;

    public XdndReceiver(ILogger<XdndReceiver> logger, IUiTimer timer, IXTransportFactory transports, IX11WindowHandles handles)
        : this(logger, timer, transports, handles, new AvaloniaNativeXdnd())
    {
    }

    internal XdndReceiver(ILogger<XdndReceiver> logger, IUiTimer timer, IXTransportFactory transports, IX11WindowHandles handles, AvaloniaNativeXdnd native)
    {
        _logger = logger;
        _timer = timer;
        _transports = transports;
        _handles = handles;
        _native = native;
    }

    public IDisposable Attach(Window window)
    {
        if (_disposed) return NullXdndReceiver.Instance.Attach(window);

        var subscription = new WindowSubscription(this, window);
        _subscriptions.Add(subscription);
        subscription.Start();
        return subscription;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var subscription in _subscriptions.ToList()) subscription.Dispose();
        _attachments.Clear();
        _runtime?.Transport.Dispose();
        _runtime = null;
    }

    private void TryAttach(WindowSubscription subscription)
    {
        var window = subscription.Window;
        if (_handles.XidOf(window) is not { } xid)
        {
            _logger.LogTrace("XDND not attached: the window's handle is not an X11 window");
            return;
        }

        if (_attachments.ContainsKey(xid)) return;
        if (Start() is not { } runtime) return;

        if (runtime.Contract.WindowsOf(window.PlatformImpl!) is not { } windows)
        {
            _logger.LogDebug("XDND not attached to window {Xid}: its platform impl is not an Avalonia X11 window", xid);
            return;
        }

        var attachment = XdndWindowAttachment.TryCreate(windows, runtime.Contract, xid, runtime.Transport, runtime.Atoms, _uris,
            new FileDropData(window.StorageProvider), new AvaloniaDragRouter(window), _timer, _logger);
        if (attachment is null)
        {
            _logger.LogDebug("XDND not attached to window {Xid}: it has no X11 event handler (already closed)", xid);
            return;
        }

        _attachments[xid] = attachment;
        subscription.Attached(xid, attachment);
    }

    /// <summary>The first call decides for the process: runs the checks and opens the transport, then every later call returns the same answer.</summary>
    private Runtime? Start()
    {
        if (_started) return _runtime;
        _started = true;

        var assembly = FindAvaloniaX11();
        if (assembly is null)
        {
            _logger.LogWarning("The XDND receiver is off: the {Assembly} assembly is not loaded", AvaloniaX11);
            return null;
        }

        if (_native.IsPresentIn(assembly))
        {
            _logger.LogInformation("The XDND receiver is off: Avalonia handles XDND itself ({Assembly} {Version})", AvaloniaX11, assembly.GetName().Version);
            return null;
        }

        var contract = AvaloniaX11Contract.TryResolve(assembly, out var failure);
        if (contract is null)
        {
            _logger.LogWarning("The XDND receiver is off: Avalonia's X11 backend changed ({Failure})", failure);
            return null;
        }

        var transport = _transports.TryOpen();
        if (transport is null)
        {
            _logger.LogInformation("The XDND receiver is off: no X display could be opened");
            return null;
        }

        return _runtime = new Runtime(contract, transport, new XdndAtoms(transport));
    }

    private static Assembly? FindAvaloniaX11()
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == AvaloniaX11);
        if (loaded is not null) return loaded;

        try
        {
            return Assembly.Load(AvaloniaX11);
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }
    }

    private sealed record Runtime(AvaloniaX11Contract Contract, IXTransport Transport, XdndAtoms Atoms);

    /// <summary>One <see cref="Attach"/> call: waits for the window to open when it has no XID yet, and lets go when the window closes or the caller disposes it.</summary>
    private sealed class WindowSubscription(XdndReceiver owner, Window window) : IDisposable
    {
        private XdndWindowAttachment? _attachment;
        private nint _xid;
        private bool _disposed;

        public Window Window { get; } = window;

        public void Start()
        {
            Window.Closed += OnClosed;
            if (owner._handles.XidOf(Window) is not null) owner.TryAttach(this);
            else Window.Opened += OnOpened;
        }

        public void Attached(nint xid, XdndWindowAttachment attachment)
        {
            _xid = xid;
            _attachment = attachment;
        }

        private void OnOpened(object? sender, EventArgs e)
        {
            Window.Opened -= OnOpened;
            if (_disposed is false) owner.TryAttach(this);
        }

        private void OnClosed(object? sender, EventArgs e) => Dispose();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Window.Opened -= OnOpened;
            Window.Closed -= OnClosed;
            owner._subscriptions.Remove(this);
            if (_attachment is null) return;

            owner._attachments.Remove(_xid);
            _attachment.Dispose();
            _attachment = null;
        }
    }
}
