using FluentAssertions;
using SilverModManager.App.UI.Windows.Xdnd;

namespace SilverModManager.UI.Tests.Windows.Xdnd;

/// <summary>The XClientMessageEvent the transport hands to XSendEvent, read back with the layout XEventReader decodes.</summary>
public class XClientMessageBufferTests
{
    private readonly XdndAtoms _atoms = new(new FakeXTransport());

    [Fact]
    public void BufferIsAsLargeAsXlibsXEventUnion()
    {
        XClientMessageBuffer.Size.Should().Be(192);
    }

    [Fact]
    public void FieldsSitAtTheOffsetsXlibDefines()
    {
        using var buffer = new XClientMessageBuffer(display: 0x1234, window: 0x4000007, messageType: _atoms.XdndStatus, 0x4000007, 3, 0, 0x10002, _atoms.XdndActionCopy);

        // XdndStatus is not an incoming message, so the reader's filter would drop it: check the raw offsets.
        buffer.Read<int>(0).Should().Be(33);
        buffer.Read<nint>(24).Should().Be(0x1234);
        buffer.Read<nint>(32).Should().Be(0x4000007);
        buffer.Read<nint>(40).Should().Be(_atoms.XdndStatus);
        buffer.Read<int>(48).Should().Be(32);
        buffer.Read<nint>(56).Should().Be(0x4000007);
        buffer.Read<nint>(64).Should().Be(3);
        buffer.Read<nint>(72).Should().Be(0);
        buffer.Read<nint>(80).Should().Be(0x10002);
        buffer.Read<nint>(88).Should().Be(_atoms.XdndActionCopy);
    }

    [Fact]
    public void AnIncomingMessageTypeDecodesToTheSameRecord()
    {
        using var buffer = new XClientMessageBuffer(0, 0x99, _atoms.XdndPosition, 7, 0, 5 << 16 | 6, 0, _atoms.XdndActionCopy);

        var decoded = new XEventReader(_atoms).Read(buffer.Pointer);

        decoded.Should().Be(new XIncomingEvent.ClientMessage(0x99, _atoms.XdndPosition, 32, 7, 0, 5 << 16 | 6, 0, _atoms.XdndActionCopy));
    }

    [Fact]
    public void UnusedBytesAreZero()
    {
        using var buffer = new XClientMessageBuffer(1, 2, 3, 4, 5, 6, 7, 8);

        for (var offset = 96; offset < XClientMessageBuffer.Size; offset += 8)
            buffer.Read<long>(offset).Should().Be(0);
        buffer.Read<long>(8).Should().Be(0);
    }
}
