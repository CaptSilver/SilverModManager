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
    [InlineData(new[] { "Mod/root/x", "a.esp" }, "Mod")]
    [InlineData(new[] { "Mod/root/x", "readme.txt", "shot.png" }, "Mod")]
    [InlineData(new[] { "Mod/root/x", "fomod/info.xml" }, "Mod")]
    [InlineData(new[] { "Mod/root/x", "Docs/readme.txt" }, "Mod")]
    [InlineData(new[] { "readme.txt", "a.esp" }, null)]
    [InlineData(new[] { "Data/a.esp", "Data/b.esp" }, null)]
    [InlineData(new[] { "meshes/a.nif" }, null)]
    public void TheCommonWrapperFolderIsTheSharedTopFolderThatIsNotAGameFolder(string[] paths, string? expected)
    {
        var wrapper = RootBuilderConvention.CommonWrapperFolder(paths.Select(p => RelativePath.FromUnsanitizedInput(p)), ["Data", "meshes"]);

        wrapper.Should().Be(expected);
    }

    [Theory]
    [InlineData("Mod/Skyrim Special Edition/root/a/b", "a/b")]
    [InlineData("Mod/skyrim special edition/ROOT/x", "x")]
    public void ARootFolderInAGameFolderBelowAWrapperIsMapped(string modPath, string expected)
    {
        RootBuilderConvention.TryMapBelowWrapper(RelativePath.FromUnsanitizedInput(modPath), ["Mod"], out var underGameFolder, ["Skyrim Special Edition"]).Should().BeTrue();

        underGameFolder.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData("Mod/Other/root/x")]
    [InlineData("Mod/Skyrim Special Edition/Data/root/x")]
    [InlineData("Mod/Skyrim Special Edition/root")]
    [InlineData("Other/Skyrim Special Edition/root/x")]
    public void AGameFolderBelowAWrapperOnlyCountsWhenItHoldsTheRootFolder(string modPath)
    {
        RootBuilderConvention.TryMapBelowWrapper(RelativePath.FromUnsanitizedInput(modPath), ["Mod"], out _, ["Skyrim Special Edition"]).Should().BeFalse();
    }

    [Theory]
    [InlineData(new[] { "Mod/Skyrim Special Edition/Data/a.esp", "Mod/Skyrim Special Edition/root/x" }, "Mod/Skyrim Special Edition")]
    [InlineData(new[] { "Mod/SKYRIM SPECIAL EDITION/a.esp" }, "Mod/SKYRIM SPECIAL EDITION")]
    [InlineData(new[] { "Mod/Skyrim Special Edition/a.esp", "Mod/b.esp" }, "Mod")]
    [InlineData(new[] { "Mod/Skyrim Special Edition/a.esp", "Mod/Other/b.esp" }, "Mod")]
    [InlineData(new[] { "Mod/Other/a.esp" }, "Mod")]
    public void AWrapperStepsIntoASingleGameFolderBelowIt(string[] paths, string expected)
    {
        var root = RootBuilderConvention.StepIntoGameFolder(
            RelativePath.FromUnsanitizedInput("Mod"), paths.Select(p => RelativePath.FromUnsanitizedInput(p)), ["Skyrim Special Edition"]);

        root.ToString().Should().Be(expected);
    }
}
