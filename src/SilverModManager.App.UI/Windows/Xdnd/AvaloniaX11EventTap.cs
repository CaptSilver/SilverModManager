using System.Collections;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;

namespace SilverModManager.App.UI.Windows.Xdnd;

/// <summary>
/// Wraps Avalonia's X event handler for one window so we see its events too. Observe-only: the original handler runs
/// after ours for every event. Everything here is reflection against Avalonia.X11 internals pinned to the referenced
/// version; <see cref="AvaloniaX11Contract"/> checks the members exist and a test fails the build on an upgrade.
/// <para>
/// The wrapper is a generic <c>TapHandler&lt;XEvent&gt;</c> built at runtime, because XEvent is internal. It is turned
/// into Avalonia's own delegate type with <see cref="Delegate.CreateDelegate(Type, object, System.Reflection.MethodInfo)"/>,
/// and Avalonia's original delegate is re-bound to our <see cref="RefHandler{T}"/> the same way. Both bindings work
/// against the real assembly (the tests run them), so the DynamicMethod fallback was not needed and is not written.
/// </para>
/// </summary>
public sealed class AvaloniaX11EventTap : IDisposable
{
    private readonly IDictionary _windows;
    private readonly nint _xid;
    private readonly Delegate _original;
    private readonly Delegate _installed;
    private bool _disposed;

    private AvaloniaX11EventTap(IDictionary windows, nint xid, Delegate original, Delegate installed)
    {
        _windows = windows;
        _xid = xid;
        _original = original;
        _installed = installed;
    }

    /// <returns>null when the contract does not resolve, <paramref name="window"/>.PlatformImpl is not an X11Window, or its XID has no entry (already closed).</returns>
    public static AvaloniaX11EventTap? TryInstall(TopLevel window, nint xid, XEventReader reader, Action<XIncomingEvent> observer, ILogger logger)
    {
        var impl = window.PlatformImpl;
        if (impl is null) return null;

        var contract = AvaloniaX11Contract.TryResolve(impl.GetType().Assembly, out var failure);
        if (contract is null)
        {
            logger.LogDebug("Avalonia's X11 backend is not the one the tap knows: {Failure}", failure);
            return null;
        }

        var windows = contract.WindowsOf(impl);
        return windows is null ? null : TryInstall(contract, windows, xid, reader, observer, logger);
    }

    internal static AvaloniaX11EventTap? TryInstall(AvaloniaX11Contract contract, IDictionary windows, nint xid, XEventReader reader, Action<XIncomingEvent> observer, ILogger logger)
    {
        if (windows.Contains(xid) is false || windows[xid] is not Delegate original) return null;

        try
        {
            var handlerType = typeof(TapHandler<>).MakeGenericType(contract.XEventType);
            var handler = Activator.CreateInstance(handlerType, original, reader, observer, logger)
                ?? throw new InvalidOperationException($"{handlerType} could not be created");
            var installed = Delegate.CreateDelegate(contract.EventHandlerType, handler, handlerType.GetMethod(nameof(TapHandler<int>.Handle))
                ?? throw new MissingMethodException(handlerType.FullName, nameof(TapHandler<int>.Handle)));

            windows[xid] = installed;
            return new AvaloniaX11EventTap(windows, xid, original, installed);
        }
        catch (Exception ex) when (ex is ArgumentException or MissingMethodException or System.Reflection.TargetInvocationException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not wrap Avalonia's X11 event handler for window {Xid}", xid);
            return null;
        }
    }

    /// <summary>Restores Avalonia's handler if the entry is still ours; a no-op when X11Window.Dispose already removed it.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_windows.Contains(_xid) && ReferenceEquals(_windows[_xid], _installed)) _windows[_xid] = _original;
    }
}

internal delegate void RefHandler<T>(ref T ev);

/// <summary>The wrapper body: copy the event out, hand it to the observer, then always call Avalonia's handler.</summary>
internal sealed unsafe class TapHandler<T> where T : struct
{
    private readonly RefHandler<T> _original;
    private readonly XEventReader _reader;
    private readonly Action<XIncomingEvent> _observer;
    private readonly ILogger _logger;
    private bool _warned;

    public TapHandler(Delegate original, XEventReader reader, Action<XIncomingEvent> observer, ILogger logger)
    {
        _original = (RefHandler<T>)Delegate.CreateDelegate(typeof(RefHandler<T>), original.Target, original.Method);
        _reader = reader;
        _observer = observer;
        _logger = logger;
    }

    public void Handle(ref T ev)
    {
        // An exception escaping here would unwind DispatchX11Events and stop Avalonia's whole event drain.
        try
        {
            var read = _reader.Read((nint)Unsafe.AsPointer(ref ev));
            if (read is not null) _observer(read);
        }
        catch (Exception ex)
        {
            if (_warned is false)
            {
                _warned = true;
                _logger.LogWarning(ex, "The XDND observer threw; the event still reaches Avalonia");
            }
        }

        _original(ref ev);
    }
}
