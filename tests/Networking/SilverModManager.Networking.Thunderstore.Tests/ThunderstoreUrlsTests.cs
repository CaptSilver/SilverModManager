using FluentAssertions;
using SilverModManager.Sdk.Thunderstore;
using Xunit;

namespace SilverModManager.Networking.Thunderstore.Tests;

public class ThunderstoreUrlsTests
{
    private static readonly ThunderstorePackageRef RepoLib = new("Zehs", "REPOLib");

    [Fact]
    public void ThePackagePageIsUnderTheCommunity()
    {
        ThunderstoreUrls.PackagePage("repo", RepoLib).Should().Be(new Uri("https://thunderstore.io/c/repo/p/Zehs/REPOLib/"));
    }

    [Fact]
    public void TheChangelogIsThePackagePageWithChangelogAdded()
    {
        ThunderstoreUrls.Changelog("repo", RepoLib).Should().Be(new Uri("https://thunderstore.io/c/repo/p/Zehs/REPOLib/changelog/"));
    }

    [Fact]
    public void ACommunitySlugIsEscapedAsOneSegment()
    {
        ThunderstoreUrls.PackagePage("a/b c", RepoLib).AbsoluteUri.Should().Be("https://thunderstore.io/c/a%2Fb%20c/p/Zehs/REPOLib/");
    }
}
