using System.Text;
using FluentAssertions;
using SilverModManager.Sdk.Ini;

namespace SilverModManager.Sdk.Tests.Ini;

public class IniDocumentTextEncodingTests
{
    [Theory]
    [InlineData(IniEncoding.Utf8, new byte[] { })]
    [InlineData(IniEncoding.Utf8Bom, new byte[] { 0xEF, 0xBB, 0xBF })]
    [InlineData(IniEncoding.Utf16LittleEndian, new byte[] { 0xFF, 0xFE })]
    [InlineData(IniEncoding.Latin1, new byte[] { })]
    public void TextParsedWithAnEncodingWritesBackInIt(IniEncoding encoding, byte[] mark)
    {
        var document = IniDocument.Parse("[General]\r\nsName=Renée\r\n", encoding);

        document.Encoding.Should().Be(encoding);
        document.ToBytes().Take(mark.Length).Should().Equal(mark);
        IniDocument.Parse(document.ToBytes()).Get("General", "sName").Should().Be("Renée");
    }

    [Fact]
    public void ALatin1DocumentIsOneBytePerCharacter()
    {
        IniDocument.Parse("k=é\r\n", IniEncoding.Latin1).ToBytes().Should().Equal(Encoding.Latin1.GetBytes("k=é\r\n"));
    }

    [Fact]
    public void AWindows1252FileReadsItsCurlyQuoteAndWritesItBackAsTheSameByte()
    {
        byte[] bytes = [.. "[General]\r\nsName=It".Select(c => (byte)c), 0x92, .. "s\r\n".Select(c => (byte)c)];

        var document = IniDocument.Parse(bytes);

        document.Get("General", "sName").Should().Be("It\u2019s");
        document.ToBytes().Should().Equal(bytes);
    }

    [Fact]
    public void SettingACharacterWindows1252HoldsWritesItsOneByteInALegacyFile()
    {
        var document = IniDocument.Parse("k=\u00e9\r\n", IniEncoding.Latin1);
        document.Set("General", "sName", "It\u2019s \u20ac");

        document.ToBytes().Should().Contain([0x92, 0x80]);
    }

    [Fact]
    public void EveryByteOfALegacyFileRoundTrips()
    {
        byte[] bytes = [.. "k=".Select(c => (byte)c), .. Enumerable.Range(0x80, 0x80).Select(b => (byte)b), 0x0D, 0x0A];

        IniDocument.Parse(bytes).ToBytes().Should().Equal(bytes);
    }
}
