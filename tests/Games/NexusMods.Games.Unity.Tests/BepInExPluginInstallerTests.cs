using FluentAssertions;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Games.Unity.BepInEx;
using NexusMods.Games.Unity.Tests.Fixtures;

namespace NexusMods.Games.Unity.Tests;

public class BepInExPluginInstallerTests(ITestOutputHelper outputHelper) : AUnityInstallerTests<BepInExPluginInstallerTests>(outputHelper)
{
    [Fact]
    public async Task AThunderstorePackageGetsItsNamespaceAndNameAsFolderAndKeepsItsMetadataBesideThePlugin()
    {
        var archive = await AddFromPathsNamed("Zehs-REPOLib-4.2.0.zip",
            "plugins/REPOLib.dll", "manifest.json", "README.md", "icon.png", "CHANGELOG.md", "LICENSE.txt");

        var (result, targets) = await TryInstall(PluginInstaller, archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(
            new[] { "REPOLib.dll", "manifest.json", "README.md", "icon.png", "CHANGELOG.md", "LICENSE.txt" }
                .Select(f => OnGame($"BepInEx/plugins/Zehs-REPOLib/{f}")));
    }

    [Fact]
    public async Task ABareDllLandsInThePluginFolder()
    {
        var archive = await AddFromPathsNamed("nickklmao-REPOConfig-1.2.6.zip", "REPOConfig.dll");

        var (result, targets) = await TryInstall(PluginInstaller, archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(OnGame("BepInEx/plugins/nickklmao-REPOConfig/REPOConfig.dll"));
    }

    [Fact]
    public async Task ConfigFilesGoStraightToTheConfigFolderKeepingSubfolders()
    {
        var archive = await AddFromPathsNamed("Zehs-REPOLib-4.2.0.zip", "config/com.example.cfg", "config/sub/x.cfg");

        var (result, targets) = await TryInstall(PluginInstaller, archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(OnGame("BepInEx/config/com.example.cfg"), OnGame("BepInEx/config/sub/x.cfg"));
    }

    [Fact]
    public async Task ALoaderPackIsNotSupportedAndTheReasonPointsAtThePackInstaller()
    {
        var archive = await AddFromPathsNamed("Test-LoaderPack-6.0.0.zip",
            "BepInExPack/winhttp.dll", "BepInExPack/doorstop_config.ini", "BepInExPack/BepInEx/core/BepInEx.Preloader.dll");

        var (result, targets) = await TryInstall(PluginInstaller, archive);

        result.IsNotSupported(out var reason).Should().BeTrue();
        reason.Should().Contain("loader pack");
        targets.Should().BeEmpty();
    }

    [Fact]
    public async Task APackIdentifiedOnlyByNameIsNotSupportedEither()
    {
        var archive = await AddFromPathsNamed("Other-LoaderPack-1.0.0.zip", "BepInExPack/BepInEx/core/BepInEx.Unity.IL2CPP.dll");

        var (result, _) = await TryInstall(PluginInstaller, archive);

        result.IsNotSupported(out _).Should().BeTrue();
    }

    [Fact]
    public async Task ThePackInstallerComesBeforeThePluginInstallerSoALoaderPackIsNotTreatedAsAPlugin()
    {
        Game.LibraryItemInstallers.Select(i => i.GetType()).Should().Equal(typeof(BepInExPackInstaller), typeof(BepInExPluginInstaller));

        var archive = await AddFromPathsNamed("Test-LoaderPack-6.0.0.zip",
            "BepInExPack/winhttp.dll", "BepInExPack/doorstop_config.ini", "BepInExPack/BepInEx/core/BepInEx.Preloader.dll");

        foreach (var installer in Game.LibraryItemInstallers.OfType<ALibraryArchiveInstaller>())
        {
            var (result, targets) = await TryInstall(installer, archive);
            if (result.IsNotSupported(out _)) continue;

            installer.Should().BeOfType<BepInExPackInstaller>();
            targets.Should().Contain(OnGame("winhttp.dll"));
            return;
        }

        throw new Exception("No installer took the loader pack.");
    }

    [Fact]
    public async Task AnEmptyArchiveIsNotSupported()
    {
        var archive = await AddFromPathsNamed("Zehs-Empty-1.0.0.zip", "plugins/");

        var (result, targets) = await TryInstall(PluginInstaller, archive);

        result.IsNotSupported(out _).Should().BeTrue();
        targets.Should().BeEmpty();
    }

    [Fact]
    public async Task ALocalArchiveWithoutVersionSuffixUsesTheManifestName()
    {
        var archive = await AddWithManifest("MyMod.zip", """{ "name": "MyMod", "version_number": "1.0.0" }""", "MyMod.dll");

        var (result, targets) = await TryInstall(PluginInstaller, archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().Contain(OnGame("BepInEx/plugins/MyMod/MyMod.dll"));
    }

    [Fact]
    public async Task ALocalArchiveWithAManifestNamespaceUsesNamespaceAndName()
    {
        var archive = await AddWithManifest("MyMod.zip", """{ "namespace": "Me", "name": "MyMod" }""", "MyMod.dll");

        var (result, targets) = await TryInstall(PluginInstaller, archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().Contain(OnGame("BepInEx/plugins/Me-MyMod/MyMod.dll"));
    }

    [Fact]
    public async Task AManifestWithAByteOrderMarkStillGivesTheFolder()
    {
        // Thunderstore's packaging tools often write a UTF-8 byte order mark.
        var archive = await AddWithManifest("MyMod.zip", "\uFEFF{ \"namespace\": \"Me\", \"name\": \"MyMod\" }", "MyMod.dll");

        var (result, targets) = await TryInstall(PluginInstaller, archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().Contain(OnGame("BepInEx/plugins/Me-MyMod/MyMod.dll"));
    }

    [Fact]
    public async Task ALocalArchiveWithoutAManifestFallsBackToTheFileName()
    {
        var archive = await AddFromPathsNamed("MyMod.zip", "MyMod.dll");

        var (result, targets) = await TryInstall(PluginInstaller, archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(OnGame("BepInEx/plugins/MyMod/MyMod.dll"));
    }

    [Fact]
    public async Task TwoFilesThatRouteToTheSamePlaceRefuseTheInstallWithTheLayoutsMessageAndAddNothing()
    {
        var archive = await AddFromPathsNamed("Zehs-REPOLib-4.2.0.zip", "a/x.dll", "b/x.dll");

        var (result, targets) = await TryInstall(PluginInstaller, archive);

        result.IsNotSupported(out var reason).Should().BeTrue();
        reason.Should().Contain("a/x.dll").And.Contain("b/x.dll").And.Contain("BepInEx/plugins/Zehs-REPOLib/x.dll");
        targets.Should().BeEmpty();
    }

    [Fact]
    public async Task AnArchiveNameWithUnusableCharactersGivesTheSanitizedPluginFolder()
    {
        var archive = await AddFromPathsNamed("a<b-1.0.zip", "x.dll");

        var (result, targets) = await TryInstall(PluginInstaller, archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(OnGame("BepInEx/plugins/a_b/x.dll"));
    }

    [Fact]
    public async Task AnArchiveNameWithNothingUsableLeftIsNotSupportedNamingTheArchive()
    {
        var archive = await AddFromPathsNamed("..zip", "x.dll");

        var (result, targets) = await TryInstall(PluginInstaller, archive);

        result.IsNotSupported(out var reason).Should().BeTrue();
        reason.Should().Contain("..zip");
        targets.Should().BeEmpty();
    }
}
