using System.Text;
using FluentAssertions;
using NexusMods.Games.Bethesda.LoadOrder.Formats;

namespace NexusMods.Games.Bethesda.LoadOrder.Tests.Formats;

public class PluginListEncodingTests
{
    private const string Name = "Ünïcödé.esp";

    [Fact]
    public void Windows1252RoundTripsANonAsciiName()
    {
        var bytes = PluginListEncoding.Windows1252.GetBytes(Name);

        bytes.Should().Equal(0xDC, (byte)'n', 0xEF, (byte)'c', 0xF6, (byte)'d', 0xE9, (byte)'.', (byte)'e', (byte)'s', (byte)'p');
        PluginListEncoding.DecodeWindows1252(bytes).Should().Be(Name);
    }

    [Fact]
    public void Windows1252HoldsTheCharactersLatin1LeavesOut()
    {
        // 0x80 is the euro sign in 1252 and a control character in Latin-1.
        PluginListEncoding.Windows1252.GetBytes("€.esp").Should().Equal(0x80, (byte)'.', (byte)'e', (byte)'s', (byte)'p');
    }

    [Fact]
    public void ANameOutsideWindows1252CannotBeEncoded()
    {
        PluginListEncoding.CanEncode(PluginListEncoding.Windows1252, "日本.esp").Should().BeFalse();
        PluginListEncoding.CanEncode(PluginListEncoding.Windows1252, Name).Should().BeTrue();
        var act = () => PluginListEncoding.Windows1252.GetBytes("日本.esp");
        act.Should().Throw<EncoderFallbackException>();
    }

    [Fact]
    public void LoadOrderTextIsReadAsUtf8WhenItIsValidUtf8()
    {
        PluginListEncoding.DecodeUtf8OrWindows1252(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(Name)).ToArray())
            .Should().Be(Name);
    }

    [Fact]
    public void LoadOrderTextFallsBackToWindows1252WhenItIsNotValidUtf8()
    {
        PluginListEncoding.DecodeUtf8OrWindows1252(PluginListEncoding.Windows1252.GetBytes(Name)).Should().Be(Name);
    }

    [Fact]
    public void Windows1252RoundTripsEveryByte()
    {
        // Morrowind.ini is rewritten through this encoding, so bytes outside any plugin name must survive too,
        // including 0x81, 0x8D, 0x8F, 0x90 and 0x9D, which 1252 leaves unassigned.
        var bytes = Enumerable.Range(0, 256).Select(b => (byte)b).ToArray();

        PluginListEncoding.Windows1252.GetBytes(PluginListEncoding.DecodeWindows1252(bytes)).Should().Equal(bytes);
    }

    [Fact]
    public void StrictUtf8DecodingDropsTheBomAndRefusesBytesThatArentUtf8()
    {
        PluginListEncoding.DecodeUtf8([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Name)]).Should().Be(Name);

        var act = () => PluginListEncoding.DecodeUtf8([0xDC, (byte)'.', (byte)'e', (byte)'s', (byte)'p']);
        act.Should().Throw<DecoderFallbackException>();
    }
}
