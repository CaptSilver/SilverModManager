using System.Buffers.Binary;
using FluentAssertions;
using SilverModManager.App.UI.Windows.Xdnd;

namespace SilverModManager.UI.Tests.Windows.Xdnd;

/// <summary>Decoding of the 192-byte Xlib XEvent buffer. The buffers are laid out here by hand from Xlib's structs, so the offsets are pinned independently of the reader.</summary>
public class XEventReaderTests
{
    private const int MotionNotify = 6;
    private const int PropertyNotify = 28;
    private const int SelectionNotify = 31;
    private const int ClientMessage = 33;

    private readonly FakeXTransport _x = new();
    private readonly XdndAtoms _atoms;
    private readonly XEventReader _reader;

    public XEventReaderTests()
    {
        _atoms = new XdndAtoms(_x);
        _reader = new XEventReader(_atoms);
    }

    private static void PutLong(byte[] buffer, int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset), value);

    private static byte[] ClientMessageBuffer(nint window, nint messageType, int format, params long[] data)
    {
        var buffer = new byte[192];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, ClientMessage);
        PutLong(buffer, 32, window);
        PutLong(buffer, 40, messageType);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(48), format);
        for (var i = 0; i < data.Length; i++) PutLong(buffer, 56 + 8 * i, data[i]);
        return buffer;
    }

    private static byte[] SelectionNotifyBuffer(nint requestor, nint selection, nint target, nint property, nint time)
    {
        var buffer = new byte[192];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, SelectionNotify);
        PutLong(buffer, 32, requestor);
        PutLong(buffer, 40, selection);
        PutLong(buffer, 48, target);
        PutLong(buffer, 56, property);
        PutLong(buffer, 64, time);
        return buffer;
    }

    private static byte[] PropertyNotifyBuffer(nint window, nint atom, nint time, int state)
    {
        var buffer = new byte[192];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, PropertyNotify);
        PutLong(buffer, 32, window);
        PutLong(buffer, 40, atom);
        PutLong(buffer, 48, time);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(56), state);
        return buffer;
    }

    private unsafe XIncomingEvent? Read(byte[] buffer)
    {
        fixed (byte* p = buffer) return _reader.Read((nint)p);
    }

    [Fact]
    public void ClientMessageDecodesWindowTypeFormatAndFiveLongs()
    {
        var buffer = ClientMessageBuffer(0x400001, _atoms.XdndPosition, 32, 0x3000001, 0, (300L << 16) | 200, 1234, _atoms.XdndActionCopy);

        Read(buffer).Should().Be(new XIncomingEvent.ClientMessage(0x400001, _atoms.XdndPosition, 32, 0x3000001, 0, (300 << 16) | 200, 1234, _atoms.XdndActionCopy));
    }

    [Theory]
    [InlineData("XdndEnter")]
    [InlineData("XdndPosition")]
    [InlineData("XdndLeave")]
    [InlineData("XdndDrop")]
    public void EveryMessageATargetReceivesIsRead(string name)
    {
        var type = _x.InternAtom(name);

        Read(ClientMessageBuffer(9, type, 32, 1)).Should().BeOfType<XIncomingEvent.ClientMessage>().Which.MessageType.Should().Be(type);
    }

    [Fact]
    public void SelectionNotifyDecodesItsFiveFields()
    {
        var buffer = SelectionNotifyBuffer(0x400001, _atoms.XdndSelection, _atoms.TextUriList, _atoms.DataProperties[1], 777);

        Read(buffer).Should().Be(new XIncomingEvent.SelectionNotify(0x400001, _atoms.XdndSelection, _atoms.TextUriList, _atoms.DataProperties[1], 777));
    }

    [Fact]
    public void SelectionNotifyWithNoPropertyReadsAsNone()
    {
        var buffer = SelectionNotifyBuffer(5, _atoms.XdndSelection, _atoms.TextUriList, 0, 1);

        Read(buffer).Should().BeOfType<XIncomingEvent.SelectionNotify>().Which.Property.Should().Be(0);
    }

    [Fact]
    public void PropertyNotifyNewValueIsNotDeleted()
    {
        var buffer = PropertyNotifyBuffer(0x400001, _atoms.DataProperties[0], 55, state: 0);

        Read(buffer).Should().Be(new XIncomingEvent.PropertyNotify(0x400001, _atoms.DataProperties[0], 55, Deleted: false));
    }

    [Fact]
    public void PropertyNotifyDeleteIsDeleted()
    {
        var buffer = PropertyNotifyBuffer(0x400001, _atoms.DataProperties[1], 55, state: 1);

        Read(buffer).Should().Be(new XIncomingEvent.PropertyNotify(0x400001, _atoms.DataProperties[1], 55, Deleted: true));
    }

    [Fact]
    public void MotionNotifyIsNotXdndTraffic()
    {
        var buffer = new byte[192];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, MotionNotify);

        Read(buffer).Should().BeNull();
    }

    [Fact]
    public void WmProtocolsClientMessageIsFilteredOut()
    {
        Read(ClientMessageBuffer(9, _x.InternAtom("WM_PROTOCOLS"), 32, _x.InternAtom("WM_DELETE_WINDOW"))).Should().BeNull();
    }

    [Fact]
    public void PropertyNotifyForAnotherAtomIsFilteredOut()
    {
        Read(PropertyNotifyBuffer(9, _x.InternAtom("_NET_WM_STATE"), 1, 0)).Should().BeNull();
    }

    [Fact]
    public void SelectionNotifyOnClipboardIsFilteredOut()
    {
        Read(SelectionNotifyBuffer(9, _x.InternAtom("CLIPBOARD"), _atoms.TextUriList, _atoms.DataProperties[0], 1)).Should().BeNull();
    }

    [Fact]
    public void ReadingNeverMutatesTheBuffer()
    {
        var buffer = ClientMessageBuffer(9, _atoms.XdndDrop, 32, 1, 2, 3, 4, 5);
        var copy = buffer.ToArray();

        Read(buffer);

        buffer.Should().Equal(copy);
    }
}
