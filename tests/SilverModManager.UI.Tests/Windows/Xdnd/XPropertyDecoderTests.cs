using System.Runtime.InteropServices;
using FluentAssertions;
using SilverModManager.App.UI.Windows.Xdnd;

namespace SilverModManager.UI.Tests.Windows.Xdnd;

/// <summary>How XGetWindowProperty's returned memory becomes an XProperty: Xlib stores format 8, 16 and 32 as char, short and long.</summary>
public class XPropertyDecoderTests
{
    [Fact]
    public void Format8CopiesTheBytes()
    {
        var p = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.Copy(new byte[] { 1, 2, 3, 4, 5 }, 0, p, 5);

            var property = XPropertyDecoder.Decode(type: 31, format: 8, items: 5, p);

            property.Should().BeEquivalentTo(new XProperty(31, 8, [1, 2, 3, 4, 5]), o => o.ComparingByMembers<XProperty>());
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    [Fact]
    public void Format16CopiesTwoBytesPerItem()
    {
        var p = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.Copy(new short[] { 0x0102, 0x0304 }, 0, p, 2);

            XPropertyDecoder.Decode(5, 16, 2, p).Bytes.Should().HaveCount(4);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    [Fact]
    public void Format32ItemsAreCLongsSoEightBytesEachOnThisPlatform()
    {
        var p = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.Copy(new long[] { 0x1111, 0x2222 }, 0, p, 2);

            var property = XPropertyDecoder.Decode(4, 32, 2, p);

            property.Bytes.Should().HaveCount(16);
            property.Longs().Should().Equal(0x1111, 0x2222);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    [Fact]
    public void ZeroItemsGiveAnEmptyPropertyEvenWithoutMemory()
    {
        var property = XPropertyDecoder.Decode(31, 8, 0, 0);

        property.Bytes.Should().BeEmpty();
        property.Format.Should().Be(8);
    }

    [Fact]
    public void FormatZeroMeansTheServerFoundNoSuchProperty()
    {
        XPropertyDecoder.Decode(0, 0, 0, 0).Should().BeNull();
    }
}
