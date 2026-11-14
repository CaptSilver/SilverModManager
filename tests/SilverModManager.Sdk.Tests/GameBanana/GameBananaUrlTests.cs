using FluentAssertions;
using SilverModManager.Sdk.GameBanana;

namespace SilverModManager.Sdk.Tests.GameBanana;

public class GameBananaUrlTests
{
    [Fact]
    public void AnInstallLinkNamesTheModAndFile()
    {
        var parsed = GameBananaUrl.TryParse(new Uri("silvermodmanager://mod/install/44334/583960"), out var url, out var problem);

        parsed.Should().BeTrue(problem);
        url.Should().Be(new GameBananaUrl(44334, 583960));
        problem.Should().BeEmpty();
    }

    [Theory]
    [InlineData("silvermodmanager://mod/install/44334")]
    [InlineData("silvermodmanager://mod/install/44334/583960/extra")]
    [InlineData("silvermodmanager://mod/install/abc/583960")]
    [InlineData("silvermodmanager://mod/install/44334/-1")]
    [InlineData("silvermodmanager://mod/install/0/583960")]
    [InlineData("silvermodmanager://mod/install/44334/0")]
    [InlineData("silvermodmanager://mod/install/99999999999999999999/1")]
    [InlineData("silvermodmanager://mod/uninstall/44334/583960")]
    [InlineData("silvermodmanager://sound/install/44334/583960")]
    [InlineData("silvermodmanager://")]
    [InlineData("funkhub://mod/install/44334/583960")]
    public void AMalformedLinkIsRefusedWithAMessageQuotingIt(string link)
    {
        var parsed = GameBananaUrl.TryParse(new Uri(link), out var url, out var problem);

        parsed.Should().BeFalse();
        url.Should().Be(default(GameBananaUrl));
        problem.Should().Contain(link).And.Contain("silvermodmanager://mod/install/{modId}/{fileId}");
    }

    [Fact]
    public void TheSchemeIsTheAppsOwn()
    {
        GameBananaUrl.Scheme.Should().Be("silvermodmanager");
    }
}
