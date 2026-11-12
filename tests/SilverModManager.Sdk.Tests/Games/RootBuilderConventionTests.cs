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

    [Theory]
    [InlineData("root/x", "x")]
    [InlineData("Mod/root/a/b", "a/b")]
    [InlineData("Skyrim Special Edition/ROOT/x", "x")]
    public void ARootFolderBelowANamedWrapperIsMapped(string modPath, string expected)
    {
        RootBuilderConvention.TryMapBelowWrapper(RelativePath.FromUnsanitizedInput(modPath), ["Mod", "Skyrim Special Edition"], out var underGameFolder).Should().BeTrue();

        underGameFolder.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData("Other/root/x")]
    [InlineData("Mod/Data/root/x")]
    [InlineData("Mod/root")]
    [InlineData("Mod/x")]
    public void ARootFolderBelowAnUnnamedFolderIsNotMatched(string modPath)
    {
        RootBuilderConvention.TryMapBelowWrapper(RelativePath.FromUnsanitizedInput(modPath), ["Mod"], out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(new[] { "Mod/root/x", "Mod/Data/a.esp" }, "Mod")]
    [InlineData(new[] { "Mod/root/x", "Other/a.esp" }, null)]
    [InlineData(new[] { "Mod/root/x", "a.esp" }, null)]
    [InlineData(new[] { "Data/a.esp", "Data/b.esp" }, null)]
    [InlineData(new[] { "meshes/a.nif" }, null)]
    public void TheCommonWrapperFolderIsTheSharedTopFolderThatIsNotAGameFolder(string[] paths, string? expected)
    {
        var wrapper = RootBuilderConvention.CommonWrapperFolder(paths.Select(p => RelativePath.FromUnsanitizedInput(p)), ["Data", "meshes"]);

        wrapper.Should().Be(expected);
    }
}
