using System.Collections;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace SilverModManager.App.UI.Windows.Xdnd;

/// <summary>Everything one window owns for XDND: its tap on Avalonia's event handler, its session and its XdndAware property.</summary>
internal sealed class XdndWindowAttachment : IDisposable
{
    private readonly nint _xid;
    private readonly IXTransport _x;
    private readonly XdndAtoms _atoms;
    private readonly AvaloniaX11EventTap _tap;
    private readonly XdndSession _session;
    private bool _disposed;

    private XdndWindowAttachment(nint xid, IXTransport x, XdndAtoms atoms, AvaloniaX11EventTap tap, XdndSession session)
    {
        _xid = xid;
        _x = x;
        _atoms = atoms;
        _tap = tap;
        _session = session;
    }

    /// <param name="windows">Avalonia's per-XID handler table for this window's platform.</param>
    /// <returns>null when the tap can't install (the window has no entry, i.e. it already closed); then nothing was changed.</returns>
    public static XdndWindowAttachment? TryCreate(IDictionary windows, AvaloniaX11Contract contract, nint xid, IXTransport x, XdndAtoms atoms,
        UriListParser uris, IFileDropFactory files, IXdndSink sink, IUiTimer timer, ILogger logger)
    {
        var session = new XdndSession(xid, x, atoms, uris, files, sink, timer, logger);

        // The tap goes in first: if it can't, the window must not advertise XdndAware, or a source would start drags
        // that nothing answers.
        var tap = AvaloniaX11EventTap.TryInstall(contract, windows, xid, new XEventReader(atoms),
            e => Dispatcher.UIThread.Post(() => session.Handle(e), DispatcherPriority.Input), logger);
        if (tap is null) return null;

        x.SetAtoms(xid, atoms.XdndAware, [XdndSession.Version]);
        x.Flush();
        return new XdndWindowAttachment(xid, x, atoms, tap, session);
    }

    /// <summary>Ends the drag in progress, hands Avalonia's handler back and stops advertising XDND. The property would
    /// die with the window anyway, but a window that outlives its attachment must not stay a target nobody serves.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _session.Cancel();
        _tap.Dispose();
        _x.DeleteProperty(_xid, _atoms.XdndAware);
        _x.Flush();
    }
}
