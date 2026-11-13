using FluentAssertions;
using SilverModManager.Games.Unity.Tests.Fixtures;

namespace SilverModManager.Games.Unity.Tests;

public class MelonLoaderPackInstallerTests(ITestOutputHelper outputHelper) : AMelonLoaderInstallerTests<MelonLoaderPackInstallerTests>(outputHelper)
{
    private static readonly string[] PackFiles = ["version.dll", "dobby.dll", "MelonLoader/net6/MelonLoader.dll"];

    [Fact]
    public async Task ThePackInstallsAtTheGameRootWithoutItsMetadata()
    {
        var archive = await AddFromPathsNamed("whatever.zip", [.. PackFiles, "manifest.json", "README.md", "icon.png"]);

        var (result, targets) = await TryInstallPack(archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(PackFiles.Select(OnGame));
    }

    [Fact]
    public async Task APackUnderOneFolderIsUnwrapped()
    {
        var archive = await AddFromPathsNamed("whatever.zip", [.. PackFiles.Select(f => $"MelonPack/{f}"), "MelonPack/manifest.json"]);

        var (result, targets) = await TryInstallPack(archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(PackFiles.Select(OnGame));
    }

    [Fact]
    public async Task AnArchiveNamedAfterThePackageIsThePackEvenWithoutAMelonLoaderFolder()
    {
        var archive = await AddFromPathsNamed("Test-MelonLoader-0.7.1.zip", "version.dll", "dobby.dll");

        var (result, targets) = await TryInstallPack(archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(OnGame("version.dll"), OnGame("dobby.dll"));
    }

    [Fact]
    public async Task AModArchiveIsNotThePack()
    {
        var archive = await AddFromPathsNamed("Someone-SomeMod-1.0.0.zip", "SomeMod.dll");

        var (result, targets) = await TryInstallPack(archive);

        result.IsNotSupported(out var reason).Should().BeTrue();
        reason.Should().Contain("version.dll").And.Contain("MelonLoader/").And.Contain("Test-MelonLoader");
        targets.Should().BeEmpty();
    }
}
