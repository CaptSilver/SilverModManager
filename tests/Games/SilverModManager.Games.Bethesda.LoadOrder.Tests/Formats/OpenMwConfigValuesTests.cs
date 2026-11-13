using FluentAssertions;
using SilverModManager.Games.Bethesda.LoadOrder.Formats;

namespace SilverModManager.Games.Bethesda.LoadOrder.Tests.Formats;

public class OpenMwConfigValuesTests
{
    [Fact]
    public void QuotedValuesUnescapeAmpersandAndQuote() =>
        OpenMwConfigValues.Values("data=\"/g/a&\"b&&c\"\n", "data").Should().Equal("/g/a\"b&c");

    [Fact]
    public void TextAfterTheClosingQuoteIsIgnored() =>
        OpenMwConfigValues.Values("data=\"/g/a\" trailing\n", "data").Should().Equal("/g/a");

    [Fact]
    public void UnquotedValuesAreTakenToTheEndOfTheLine() =>
        OpenMwConfigValues.Values("data=/g/some folder/x\ncontent=A.esp\n", "data").Should().Equal("/g/some folder/x");

    [Fact]
    public void EveryLineOfTheKeyIsReturnedInFileOrder() =>
        OpenMwConfigValues.Values("data=\"/a\"\nfallback=x,1\ndata=\"/b\"\n", "data").Should().Equal("/a", "/b");

    [Fact]
    public void LastIsTheFinalValueOrNull()
    {
        OpenMwConfigValues.Last("data-local=/a\ndata-local=/b\n", "data-local").Should().Be("/b");
        OpenMwConfigValues.Last("content=A.esp\n", "data-local").Should().BeNull();
    }

    [Fact]
    public void CommentsAndLongerKeysAreNotTheKey() =>
        OpenMwConfigValues.Values("#data=/a\n##data=/b\ndata-local=/c\n", "data").Should().BeEmpty();

    [Fact]
    public void CrlfAndMissingFinalNewlineAreFine()
    {
        OpenMwConfigValues.Values("data=\"/a\"\r\ndata=/b\r\ndata=\"/c\"", "data").Should().Equal("/a", "/b", "/c");
    }

    [Fact]
    public void AnUnterminatedQuoteTakesTheRestOfTheLine() =>
        OpenMwConfigValues.Values("data=\"/a b\n", "data").Should().Equal("/a b");
}
