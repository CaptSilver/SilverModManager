using FluentAssertions;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Games.FromSoft.Installers;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Games.FromSoft.Tests;

public class Me3NativeInstallerTests(ITestOutputHelper outputHelper) : AEldenRingInstallerTests<Me3NativeInstallerTests>(outputHelper)
{
    private Task<(InstallerResult Result, string[] Targets)> TryInstall(LibraryArchive.ReadOnly archive)
        => TryInstall<Me3NativeInstaller>(archive);

    [Fact]
    public async Task TheWholeArchiveLandsUnderTheNativesFolderOfTheArchiveName()
    {
        var archive = await AddNamed("Fancy Hook.zip", "FancyHook.dll", "config/hook.ini", "README.txt");

        var (result, targets) = await TryInstall(archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(
            Me3Mods("natives/Fancy Hook/FancyHook.dll"),
            Me3Mods("natives/Fancy Hook/config/hook.ini"),
            Me3Mods("natives/Fancy Hook/README.txt"));
    }

    [Fact]
    public async Task ADllInsideAFolderKeepsItsPlaceInTheArchiveLayout()
    {
        var archive = await AddNamed("Hook.zip", "bin/hook.dll");

        var (_, targets) = await TryInstall(archive);

        targets.Should().BeEquivalentTo(Me3Mods("natives/Hook/bin/hook.dll"));
    }

    [Fact]
    public async Task SeveralDllsWithoutANameMatchAreNotSupportedAndTheReasonListsThem()
    {
        var archive = await AddNamed("Hook.zip", "a.dll", "b.dll");

        var (result, targets) = await TryInstall(archive);

        result.IsNotSupported(out var reason).Should().BeTrue();
        reason.Should().Be("Several DLLs; pick the mod's DLL with the manual installer: a.dll, b.dll");
        targets.Should().BeEmpty();
    }

    [Fact]
    public async Task AnArchiveWithoutADllIsNotSupported()
    {
        var archive = await AddNamed("Docs.zip", "readme.txt");

        var (result, _) = await TryInstall(archive);

        result.IsNotSupported(out _).Should().BeTrue();
    }

    [Fact]
    public async Task TheDllThatMatchesTheNameIsChosenOverARedistributable()
    {
        var archive = await AddNamed("Hook.zip", "vcruntime140.dll", "hook.dll");

        var (result, _) = await TryInstall(archive);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task AMultiPartArchiveExtensionIsDroppedFromTheNativesFolderName()
    {
        var archive = await AddNamed("Hook.tar.gz", "hook.dll");

        var (result, targets) = await TryInstall(archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(Me3Mods("natives/Hook/hook.dll"));
    }

    [Theory]
    [InlineData(".zip")]
    [InlineData(".tar.gz")]
    public async Task AnArchiveWhoseNameIsOnlyAnExtensionIsNotSupportedAndTheReasonSaysWhy(string archiveName)
    {
        var archive = await AddNamed(archiveName, "hook.dll");

        var (result, targets) = await TryInstall(archive);

        result.IsNotSupported(out var reason).Should().BeTrue();
        reason.Should().Contain("no name");
        targets.Should().BeEmpty();
    }

    [Theory]
    [InlineData("...zip")]
    [InlineData("a:b.zip")]
    public async Task AnArchiveWhoseNameIsNotAUsableFolderNameIsNotSupported(string archiveName)
    {
        var archive = await AddNamed(archiveName, "hook.dll");

        var (result, targets) = await TryInstall(archive);

        result.IsNotSupported(out var reason).Should().BeTrue();
        reason.Should().Contain("folder name");
        targets.Should().BeEmpty();
    }
}
