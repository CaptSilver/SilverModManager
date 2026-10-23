using FluentAssertions;
using SilverModManager.Games.Unity.BepInEx;
using NexusMods.Paths;
using Xunit;

namespace SilverModManager.Games.Unity.Tests;

public class BepInExLayoutTests
{
    private const string Package = "Zehs-REPOLib";

    private static string Route(string path)
    {
        var map = BepInExLayout.Route([RelativePath.FromUnsanitizedInput(path)], Package);
        var target = map.Values.Single();
        return $"{target.LocationId}/{target.Path}";
    }

    private static RelativePath[] Paths(params string[] paths) => paths.Select(p => RelativePath.FromUnsanitizedInput(p)).ToArray();

    [Theory]
    [InlineData("plugins/REPOLib.dll", "Game/BepInEx/plugins/Zehs-REPOLib/REPOLib.dll")]
    [InlineData("REPOConfig.dll", "Game/BepInEx/plugins/Zehs-REPOLib/REPOConfig.dll")]
    [InlineData("BepInEx/plugins/Foo/Foo.dll", "Game/BepInEx/plugins/Zehs-REPOLib/Foo/Foo.dll")]
    [InlineData("patchers/X.dll", "Game/BepInEx/patchers/Zehs-REPOLib/X.dll")]
    [InlineData("core/Y.dll", "Game/BepInEx/core/Zehs-REPOLib/Y.dll")]
    [InlineData("Z.mm.dll", "Game/BepInEx/monomod/Zehs-REPOLib/Z.mm.dll")]
    [InlineData("nested/deeper/plugins/A.dll", "Game/BepInEx/plugins/Zehs-REPOLib/A.dll")]
    [InlineData("Plugins/B.dll", "Game/BepInEx/plugins/Zehs-REPOLib/B.dll")]
    public void FilesGoToTheFolderTheirPathOrExtensionNames(string source, string expected) =>
        Route(source).Should().Be(expected);

    [Theory]
    [InlineData("config/com.example.cfg", "Game/BepInEx/config/com.example.cfg")]
    [InlineData("config/sub/x.cfg", "Game/BepInEx/config/sub/x.cfg")]
    public void ConfigHasNoPackageFolder(string source, string expected) =>
        Route(source).Should().Be(expected);

    [Theory]
    [InlineData("manifest.json")]
    [InlineData("README.md")]
    [InlineData("icon.png")]
    [InlineData("CHANGELOG.md")]
    public void PackageMetadataIsKeptWithThePlugin(string name) =>
        Route(name).Should().Be($"Game/BepInEx/plugins/Zehs-REPOLib/{name}");

    [Fact]
    public void AFileInAnUnmatchedFolderIsFlattened() =>
        Route("assets/sound.wav").Should().Be("Game/BepInEx/plugins/Zehs-REPOLib/sound.wav");

    [Fact]
    public void FlatteningTwoFilesOntoOneNameThrowsNamingBothAndTheTarget()
    {
        var act = () => BepInExLayout.Route(Paths("a/x.dll", "b/x.dll"), Package);

        act.Should().Throw<BepInExLayoutException>()
            .Which.Message.Should().Contain("a/x.dll").And.Contain("b/x.dll")
            .And.Contain("BepInEx/plugins/Zehs-REPOLib/x.dll");
    }

    [Fact]
    public void TargetsThatDifferOnlyInCaseCollide()
    {
        // Proton resolves paths without regard to case, so these would be one file to the game.
        var act = () => BepInExLayout.Route(Paths("a/X.dll", "b/x.dll"), Package);

        act.Should().Throw<BepInExLayoutException>();
    }

    [Theory]
    [InlineData("plugins/config/x.cfg", "Game/BepInEx/plugins/Zehs-REPOLib/config/x.cfg")]
    [InlineData("config/plugins/x.cfg", "Game/BepInEx/config/plugins/x.cfg")]
    public void TheOutermostMatchingFolderWins(string source, string expected) =>
        Route(source).Should().Be(expected);

    [Fact]
    public void AFileNamedLikeAFolderIsRoutedAsAFile() =>
        Route("docs/plugins").Should().Be("Game/BepInEx/plugins/Zehs-REPOLib/plugins");

    [Fact]
    public void TheMappingKeepsEverySourcePath()
    {
        var map = BepInExLayout.Route(Paths("plugins/A.dll", "config/a.cfg"), Package);

        map.Should().HaveCount(2);
        map.Keys.Select(k => k.ToString()).Should().BeEquivalentTo("plugins/A.dll", "config/a.cfg");
    }

    [Theory]
    [InlineData("BepInExPack", "BepInExPack")]
    [InlineData("BepInExPack_PEAK", "BepInExPack_PEAK")]
    [InlineData("", "")]
    public void ALoaderPackIsRecognisedUnderItsRoot(string folder, string expectedRoot)
    {
        var prefix = folder.Length == 0 ? "" : folder + "/";
        var paths = Paths(prefix + "winhttp.dll", prefix + "doorstop_config.ini", prefix + "BepInEx/core/BepInEx.Preloader.dll", "manifest.json");

        BepInExLayout.IsLoaderPack(paths, out var root).Should().BeTrue();
        root.Should().Be(expectedRoot);
    }

    [Fact]
    public void WithoutTheProxyDllItIsNotALoaderPack() =>
        BepInExLayout.IsLoaderPack(Paths("BepInExPack/doorstop_config.ini", "BepInExPack/BepInEx/core/BepInEx.Preloader.dll"), out _).Should().BeFalse();

    [Fact]
    public void AProxyDllTwoFoldersDeepIsNotALoaderPack() =>
        BepInExLayout.IsLoaderPack(Paths("A/B/winhttp.dll", "A/B/doorstop_config.ini", "A/B/BepInEx/core/BepInEx.Preloader.dll"), out _).Should().BeFalse();

    [Fact]
    public void FilesUnderDifferentRootsAreNotALoaderPack() =>
        BepInExLayout.IsLoaderPack(Paths("A/winhttp.dll", "B/doorstop_config.ini", "B/BepInEx/core/BepInEx.Preloader.dll"), out _).Should().BeFalse();

    [Theory]
    [InlineData("plugins/../../evil.dll")]
    [InlineData("../evil.dll")]
    [InlineData("plugins/./x.dll")]
    public void AnArchiveEntryWithADotSegmentIsRefused(string path)
    {
        var act = () => BepInExLayout.Route(Paths(path), Package);

        act.Should().Throw<BepInExLayoutException>().Which.Message.Should().Contain(path);
    }

    [Fact]
    public void BackslashSeparatedDotSegmentsAreRefusedToo()
    {
        var act = () => BepInExLayout.Route(Paths("plugins\\..\\..\\evil.dll"), Package);

        act.Should().Throw<BepInExLayoutException>();
    }
}
