using FluentAssertions;

namespace SilverModManager.Games.Unity.Tests;

public class ThunderstorePackageMetadataTests
{
    [Theory]
    [InlineData("manifest.json", true)]
    [InlineData("README.md", true)]
    [InlineData("icon.png", true)]
    [InlineData("CHANGELOG.md", true)]
    [InlineData("Mods/manifest.json", false)]
    [InlineData("Foo.dll", false)]
    public void OnlyThePackagesOwnFilesAtItsRootAreMetadata(string relativePath, bool expected)
    {
        ThunderstorePackageMetadata.IsAtRoot(relativePath).Should().Be(expected);
    }
}
