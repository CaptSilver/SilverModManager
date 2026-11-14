using FluentAssertions;
using Xunit;

namespace SilverModManager.Networking.GitHub.Tests;

public class ReleaseTagTests
{
    [Theory]
    [InlineData("v2.2.6", "2.2.6")]
    [InlineData("2.2.6", "2.2.6")]
    [InlineData("v0.7.0", "0.7.0")]
    [InlineData("v12", "12.0")]
    [InlineData("3", "3.0")]
    [InlineData("v2.02.06", "2.2.6")]
    public void ATagThatIsAVersionParses(string tag, string expected)
    {
        ReleaseExtensions.TryParseTag(tag, out var version).Should().BeTrue();

        version.Should().Be(Version.Parse(expected));
    }

    [Theory]
    [InlineData("nightly")]
    [InlineData("2026-11-01")]
    [InlineData("v")]
    [InlineData("")]
    [InlineData("release-2.0")]
    public void ATagThatIsAnythingElseDoesNot(string tag)
    {
        ReleaseExtensions.TryParseTag(tag, out _).Should().BeFalse();
    }
}
