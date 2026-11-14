using FluentAssertions;
using SilverModManager.Library.GitHub;
using Xunit;

namespace SilverModManager.Library.Tests.GitHub;

public class ReleaseVersionTests
{
    private static ReleaseVersion Of(string tag)
    {
        ReleaseVersion.TryParse(tag, out var version).Should().BeTrue(tag);
        return version!.Value;
    }

    [Fact]
    public void APrereleaseIsOlderThanItsFinalReleaseAndNewerThanEarlierOnes()
    {
        Of("v3.0.0-rc1").Should().BeLessThan(Of("v3.0.0"));
        Of("v3.0.0-rc1").Should().BeGreaterThan(Of("v2.9.9"));
        Of("v3-rc1").IsPrerelease.Should().BeTrue();
    }

    [Fact]
    public void BuildMetadataDoesNotMakeAPrerelease()
    {
        Of("v1.2.3+build5").IsPrerelease.Should().BeFalse();
        Of("v1.2.3+build5").Should().Be(Of("v1.2.3"));
    }

    [Theory]
    [InlineData("nightly")]
    [InlineData("2026-11-01")]
    [InlineData("-rc1")]
    public void ATagThatIsNotAVersionDoesNotParse(string tag)
    {
        ReleaseVersion.TryParse(tag, out _).Should().BeFalse();
    }
}
