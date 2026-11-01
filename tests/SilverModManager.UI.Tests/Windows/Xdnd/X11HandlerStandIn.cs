using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using SilverModManager.App.UI.Windows.Xdnd;

namespace SilverModManager.UI.Tests.Windows.Xdnd;

/// <summary>
/// Avalonia's per-window handler table as a plain dictionary holding a delegate of Avalonia's real EventHandler type,
/// and a way to push raw 192-byte events through whatever delegate is installed, the way XNextEvent's loop does.
/// </summary>
public sealed class X11HandlerStandIn
{
    public const nint Xid = 0x400001;

    public AvaloniaX11Contract Contract { get; } =
        AvaloniaX11Contract.TryResolve(typeof(Avalonia.X11PlatformOptions).Assembly, out _)
        ?? throw new InvalidOperationException("Avalonia.X11 contract does not resolve");

    public Dictionary<nint, Delegate> Windows { get; } = [];

    /// <summary>How many events reached the stand-in for X11Window.OnEvent.</summary>
    public int OriginalCalls { get; private set; }

    public Delegate Original { get; }

    public X11HandlerStandIn()
    {
        Original = Delegate.CreateDelegate(Contract.EventHandlerType, this,
            typeof(X11HandlerStandIn).GetMethod(nameof(OriginalHandler), BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(Contract.XEventType));
        Windows[Xid] = Original;
    }

    private void OriginalHandler<T>(ref T ev) where T : struct => OriginalCalls++;

    public void Push(byte[] raw)
    {
        try
        {
            typeof(X11HandlerStandIn).GetMethod(nameof(PushTyped), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(Contract.XEventType).Invoke(null, [Windows[Xid], raw]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }

    private static void PushTyped<T>(Delegate handler, byte[] raw) where T : struct
    {
        T ev = default;
        raw.CopyTo(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref ev, 1)));
        var call = (RefHandler<T>)Delegate.CreateDelegate(typeof(RefHandler<T>), handler.Target, handler.Method);
        call(ref ev);
    }

    /// <summary>An Xlib ClientMessage (type 33, format 32) to <see cref="Xid"/>.</summary>
    public static byte[] ClientMessage(nint messageType, nint l0, nint l1, nint l2, nint l3, nint l4)
    {
        var raw = new byte[192];
        BinaryPrimitives.WriteInt32LittleEndian(raw, 33);
        BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(32), Xid);
        BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(40), messageType);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(48), 32);
        foreach (var (index, value) in new[] { l0, l1, l2, l3, l4 }.Index())
            BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(56 + index * 8), value);
        return raw;
    }
}
