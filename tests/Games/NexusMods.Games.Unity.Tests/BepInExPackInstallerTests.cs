using FluentAssertions;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Games.Unity.Tests.Fixtures;

namespace NexusMods.Games.Unity.Tests;

public class BepInExPackInstallerTests(ITestOutputHelper outputHelper) : AUnityInstallerTests<BepInExPackInstallerTests>(outputHelper)
{
    private static readonly string[] PackFiles =
    [
        "winhttp.dll",
        "doorstop_config.ini",
        ".doorstop_version",
        "BepInEx/core/BepInEx.Preloader.dll",
        "BepInEx/config/BepInEx.cfg",
    ];

    private static string[] Under(string root, params string[] files) => files.Select(f => $"{root}/{f}").ToArray();

    [Fact]
    public async Task APackIdentifiedByItsFilesLosesTheRootFolderAndTheMetadata()
    {
        var archive = await AddFromPathsNamed("whatever.zip",
            [.. Under("BepInExPack", PackFiles), "manifest.json", "README.md", "icon.png"]);

        var (result, targets) = await TryInstallPack(archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(PackFiles.Select(OnGame));
    }

    [Fact]
    public async Task MetadataInsideTheRootFolderIsDropped()
    {
        var archive = await AddFromPathsNamed("whatever.zip",
            [.. Under("BepInExPack", PackFiles), "BepInExPack/README.md", "BepInExPack/manifest.json", "BepInExPack/ICON.PNG"]);

        var (result, targets) = await TryInstallPack(archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(PackFiles.Select(OnGame));
    }

    [Theory]
    [InlineData("Test-LoaderPack-6.0.0.zip")]
    [InlineData("Other-LoaderPack-1.0.0.zip")]
    public async Task APackIdentifiedByItsNameIsInstalledEvenWithoutAPreloader(string archiveName)
    {
        var archive = await AddFromPathsNamed(archiveName,
            "BepInExPack/winhttp.dll", "BepInExPack/doorstop_config.ini", "BepInExPack/BepInEx/core/BepInEx.Unity.IL2CPP.dll", "manifest.json");

        var (result, targets) = await TryInstallPack(archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(OnGame("winhttp.dll"), OnGame("doorstop_config.ini"), OnGame("BepInEx/core/BepInEx.Unity.IL2CPP.dll"));
    }

    [Fact]
    public async Task APackWithoutAnIdentifiableRootAndWithoutAMatchingNameIsNotSupported()
    {
        var archive = await AddFromPathsNamed("Zehs-Other-1.0.0.zip", "BepInExPack/doorstop_config.ini", "BepInExPack/BepInEx/core/BepInEx.Unity.IL2CPP.dll", "manifest.json");

        var (result, targets) = await TryInstallPack(archive);

        result.IsNotSupported(out var reason).Should().BeTrue();
        reason.Should().Contain("winhttp.dll").And.Contain("doorstop_config.ini").And.Contain("BepInEx.Preloader.dll").And.Contain("Test-LoaderPack");
        targets.Should().BeEmpty();
    }

    [Fact]
    public async Task APackWithoutAPreloaderIsNotSupportedUnderAnotherPackagesName()
    {
        var archive = await AddFromPathsNamed("Zehs-Other-1.0.0.zip",
            "BepInExPack/winhttp.dll", "BepInExPack/doorstop_config.ini", "BepInExPack/BepInEx/core/BepInEx.Unity.IL2CPP.dll", "manifest.json");

        var (result, targets) = await TryInstallPack(archive);

        result.IsNotSupported(out _).Should().BeTrue();
        targets.Should().BeEmpty();
    }

    [Fact]
    public async Task ARootFolderWithAnEmptyPluginsFolderInstallsTheSameAndTheFolderYieldsNoFile()
    {
        var archive = await AddFromPathsNamed("BepInEx-BepInExPack_PEAK-5.4.75301.zip",
            [.. Under("BepInExPack_PEAK", PackFiles), "BepInExPack_PEAK/BepInEx/plugins/", "manifest.json", "README.md", "icon.png"]);

        var (result, targets) = await TryInstallPack(archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(PackFiles.Select(OnGame));
    }

    [Fact]
    public async Task AModThatIsNotALoaderPackIsNotSupported()
    {
        var archive = await AddFromPathsNamed("Zehs-REPOLib-4.2.0.zip", "plugins/REPOLib.dll");

        var (result, targets) = await TryInstallPack(archive);

        result.IsNotSupported(out _).Should().BeTrue();
        targets.Should().BeEmpty();
    }
}
