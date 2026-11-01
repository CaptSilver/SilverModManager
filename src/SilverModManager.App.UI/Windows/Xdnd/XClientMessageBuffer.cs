using System.Runtime.InteropServices;

namespace SilverModManager.App.UI.Windows.Xdnd;

/// <summary>
/// An Xlib <c>XEvent</c> union holding one format-32 <c>XClientMessageEvent</c>, as XSendEvent wants it. Offsets are
/// the 64-bit Linux layout, the one <see cref="XEventReader"/> reads: <c>type</c> 0, <c>serial</c> 8, <c>send_event</c>
/// 16, <c>display</c> 24, <c>window</c> 32, <c>message_type</c> 40, <c>format</c> 48, <c>data.l[5]</c> 56. The memory is
/// native because XSendEvent takes a pointer and the union is 24 longs wide whatever the message uses.
/// </summary>
public sealed unsafe class XClientMessageBuffer : IDisposable
{
    public const int Size = 192;
    private const int ClientMessageType = 33;
    private const int Format32 = 32;

    private readonly void* _memory;

    /// <param name="window">The window the message is addressed to; XDND puts the receiving window in this field.</param>
    public XClientMessageBuffer(nint display, nint window, nint messageType, nint l0, nint l1, nint l2, nint l3, nint l4)
    {
        _memory = NativeMemory.AllocZeroed(Size);
        var p = (byte*)_memory;
        *(int*)p = ClientMessageType;
        *(nint*)(p + 24) = display;
        *(nint*)(p + 32) = window;
        *(nint*)(p + 40) = messageType;
        *(int*)(p + 48) = Format32;
        *(nint*)(p + 56) = l0;
        *(nint*)(p + 64) = l1;
        *(nint*)(p + 72) = l2;
        *(nint*)(p + 80) = l3;
        *(nint*)(p + 88) = l4;
    }

    /// <summary>The address to hand to XSendEvent; valid until <see cref="Dispose"/>.</summary>
    public nint Pointer => (nint)_memory;

    /// <summary>The value at a byte offset of the event; for tests and diagnostics.</summary>
    public T Read<T>(int offset) where T : unmanaged => *(T*)((byte*)_memory + offset);

    public void Dispose() => NativeMemory.Free(_memory);
}
