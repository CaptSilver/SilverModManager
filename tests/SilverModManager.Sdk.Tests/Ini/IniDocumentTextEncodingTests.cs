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
}
