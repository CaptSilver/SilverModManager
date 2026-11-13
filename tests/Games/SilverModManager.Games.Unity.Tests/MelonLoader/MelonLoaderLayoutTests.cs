using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Games.Unity.MelonLoader;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Unity.Tests;

public class MelonLoaderLayoutTests
{
    private static RelativePath[] Paths(params string[] paths) => paths.Select(p => RelativePath.FromUnsanitizedInput(p)).ToArray();

    [Theory]
    [InlineData("../Foo.dll")]
    [InlineData("a/../Foo.dll")]
    [InlineData("./Foo.dll")]
    public void ADotOrDotDotSegmentIsRefused(string path)
    {
        var act = () => MelonLoaderLayout.Route(Paths(path), UnityRuntime.Unknown);

        act.Should().Throw<MelonLoaderLayoutException>().WithMessage("*'.' or '..'*");
    }

    [Fact]
    public void AFileNamedModsIsNotAFolder()
    {
        var routes = MelonLoaderLayout.Route(Paths("Mods"), UnityRuntime.Unknown);

        routes[RelativePath.FromUnsanitizedInput("Mods")].Should().BeNull();
    }

    [Fact]
    public void FolderNamesMatchIgnoringCaseAndTakeTheCanonicalSpelling()
    {
        var routes = MelonLoaderLayout.Route(Paths("x/MODS/Foo.dll", "userdata/a.cfg"), UnityRuntime.Unknown);

        routes[RelativePath.FromUnsanitizedInput("x/MODS/Foo.dll")].Should().Be(new GamePath(LocationId.Game, "Mods/Foo.dll"));
        routes[RelativePath.FromUnsanitizedInput("userdata/a.cfg")].Should().Be(new GamePath(LocationId.Game, "UserData/a.cfg"));
    }

    [Fact]
    public void BranchesInDifferentFoldersAreNotTheSameMod()
    {
        var routes = MelonLoaderLayout.Route(Paths("Mods/Foo.Il2cpp.dll", "Plugins/Foo.Mono.dll"), UnityRuntime.Il2Cpp);

        routes.Values.Should().OnlyContain(target => target != null);
    }

    [Fact]
    public void ThePackIsRecognisedAtTheRootOrOneFolderDeepButNotDeeper()
    {
        MelonLoaderLayout.IsLoaderPack(Paths("version.dll", "MelonLoader/a.dll"), out var root).Should().BeTrue();
        root.Should().BeEmpty();
        MelonLoaderLayout.IsLoaderPack(Paths("Pack/version.dll", "Pack/MelonLoader/a.dll"), out root).Should().BeTrue();
        root.Should().Be("Pack");
        MelonLoaderLayout.IsLoaderPack(Paths("a/b/version.dll", "a/b/MelonLoader/a.dll"), out _).Should().BeFalse();
        MelonLoaderLayout.IsLoaderPack(Paths("version.dll", "Mods/a.dll"), out _).Should().BeFalse();
    }
}
