using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Sdk.Tests.Games;

public class RootBuilderConventionTests
{
    [Theory]
    [InlineData("root/x", "x")]
    [InlineData("Root/a/b", "a/b")]
    [InlineData("ROOT/x", "x")]
    public void AFileInTheRootFolderIsMappedToItsPathBelowIt(string modPath, string expected)
    {
        RootBuilderConvention.TryMap(RelativePath.FromUnsanitizedInput(modPath), out var underGameFolder).Should().BeTrue();

        underGameFolder.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("rootx/y")]
    [InlineData("Data/root/x")]
    [InlineData("")]
    public void ANonRootFolderPathIsNotMatched(string modPath)
    {
        RootBuilderConvention.TryMap(RelativePath.FromUnsanitizedInput(modPath), out var underGameFolder).Should().BeFalse();

        underGameFolder.Should().Be(default(RelativePath));
    }
}
