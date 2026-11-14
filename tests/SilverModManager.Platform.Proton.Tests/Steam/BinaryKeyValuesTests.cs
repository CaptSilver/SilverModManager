using FluentAssertions;
using SilverModManager.Platform.Proton.Steam.BinaryKeyValues;

namespace SilverModManager.Platform.Proton.Tests.Steam;

public class BinaryKeyValuesTests
{
    private static IBinaryKeyValues Kv => new HandWrittenBinaryKeyValues();

    [Fact]
    public void ParseSaveUnchanged_BytesIdentical()
    {
        var fixture = ShortcutsVdfFixture.Bytes();

        Kv.Write(Kv.Read(fixture)).Should().Equal(fixture);
    }

    [Fact]
    public void ParseSaveUnchanged_BytesIdenticalWithGapsInTheKeys()
    {
        var fixture = ShortcutsVdfFixture.Bytes(("0", 1, "A", "\"/a\""), ("3", 2, "B", "\"/b\""), ("7", 3, "C", "\"/c\""));

        Kv.Write(Kv.Read(fixture)).Should().Equal(fixture);
    }

    [Fact]
    public void AppidIsReadAsInt32AndAppnameAsString()
    {
        var root = Kv.Read(ShortcutsVdfFixture.Bytes());

        var child = ((BinaryKvMap)((BinaryKvMap)root.Children.Single()).Children.Single());
        child.Children.OfType<BinaryKvInt32>().Single(c => c.Name == "appid").Value.Should().Be(unchecked((int)0x8123_4567));
        child.Children.OfType<BinaryKvString>().Single(c => c.Name == "appname").Value.Should().Be("Game");
    }

    [Fact]
    public void AnUnknownTypeByteIsARefusalNotASilentSkip()
    {
        var bytes = ShortcutsVdfFixture.Bytes();
        // The first child's first node starts right after "00 shortcuts 00 00 0 00"; turn its type byte into 0x07.
        var offset = 1 + "shortcuts".Length + 1 + 1 + "0".Length + 1;
        bytes[offset].Should().Be(0x02);
        bytes[offset] = 0x07;

        var act = () => Kv.Read(bytes);

        act.Should().Throw<InvalidDataException>().WithMessage($"*{offset}*");
    }

    [Fact]
    public void TruncatedDataIsARefusal()
    {
        var bytes = ShortcutsVdfFixture.Bytes();

        var act = () => Kv.Read(bytes.AsSpan(0, bytes.Length - 3));

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void BytesAfterTheFinalEndMarkerAreARefusal()
    {
        var act = () => Kv.Read([.. ShortcutsVdfFixture.Bytes(), 0x00]);

        act.Should().Throw<InvalidDataException>();
    }
}
