using FluentAssertions;
using SilverModManager.Wabbajack.Acquire.Hosts;

namespace SilverModManager.Wabbajack.Tests.Acquire.Hosts;

public class HtmlTagsTests
{
    [Fact]
    public void AttributesAreReadWhateverTheirQuoting()
    {
        var tags = HtmlTags.Find("""<p><A HREF="https://a.test/x?y=1&amp;z=2" id='go' data-n=5 disabled>text</A></p>""", "a");

        var tag = tags.Should().ContainSingle().Subject;
        tag["href"].Should().Be("https://a.test/x?y=1&z=2");
        tag["id"].Should().Be("go");
        tag["data-n"].Should().Be("5");
        tag["disabled"].Should().Be("");
    }

    [Fact]
    public void OnlyTheNamedTagIsReturnedInDocumentOrder()
    {
        var tags = HtmlTags.Find("""<a href="1"></a><abbr title="no"></abbr><a href="2"></a><input name="a">""", "a");

        tags.Select(t => t["href"]).Should().Equal("1", "2");
    }

    [Fact]
    public void ATagSplitAcrossLinesIsStillRead()
    {
        var tags = HtmlTags.Find("<input\n  type=\"hidden\"\n  name=\"uuid\"\n  value=\"abc\">", "input");

        tags.Single()["name"].Should().Be("uuid");
        tags.Single()["value"].Should().Be("abc");
    }

    [Fact]
    public void NoMatchGivesAnEmptyList()
    {
        HtmlTags.Find("<p>nothing</p>", "a").Should().BeEmpty();
    }
}
