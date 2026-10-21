using FluentAssertions;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Installers;
using NexusMods.Games.FromSoft.Me3;
using NexusMods.Games.FromSoft.Tests.Fixtures;
using NexusMods.Paths;
using NexusMods.Sdk.Library;

namespace NexusMods.Games.FromSoft.Tests;

public class SeamlessCoopInstallerTests(ITestOutputHelper outputHelper) : AEldenRingInstallerTests<SeamlessCoopInstallerTests>(outputHelper)
{
    private Task<(InstallerResult Result, string[] Targets)> TryInstall(LibraryArchive.ReadOnly archive)
        => TryInstall<SeamlessCoopInstaller>(archive);

    private async Task<LibraryArchive.ReadOnly> AddRelease(string? wrapper = null)
    {
        var path = TemporaryFileManager.CreateFolder().Path / "Seamless Co-op.zip";
        await ErscRelease.Write(path, wrapper);
        return await RegisterLocalArchive(path);
    }

    [Fact]
    public async Task TheDllAndTheSettingsGoToTheConfigLayerBesideEachOtherAndTheLauncherIsDropped()
    {
        var (result, targets) = await TryInstall(await AddRelease());

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(
            Me3Mods("overlay/SeamlessCoop/ersc.dll"),
            Me3Mods("overlay/SeamlessCoop/ersc_settings.ini"));
    }

    [Fact]
    public async Task AReleaseInsideAWrapperFolderIsRecognisedToo()
    {
        var (result, targets) = await TryInstall(await AddRelease("ersc 1.9.9"));

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(
            Me3Mods("overlay/SeamlessCoop/ersc.dll"),
            Me3Mods("overlay/SeamlessCoop/ersc_settings.ini"));
    }

    [Fact]
    public async Task OtherFilesInTheSeamlessCoopFolderTravelWithTheDll()
    {
        var archive = await AddNamed("Seamless Co-op.zip", "ersc_launcher.exe", "SeamlessCoop/ersc.dll", "SeamlessCoop/ersc_settings.ini", "SeamlessCoop/locale/en.txt");

        var (_, targets) = await TryInstall(archive);

        targets.Should().Contain(Me3Mods("overlay/SeamlessCoop/locale/en.txt"));
    }

    [Fact]
    public async Task AnArchiveWithoutTheDllAndSettingsPairIsNotSupported()
    {
        var archive = await AddNamed("Other.zip", "SeamlessCoop/ersc.dll", "parts/wp_a.dcx");

        var (result, targets) = await TryInstall(archive);

        result.IsNotSupported(out _).Should().BeTrue();
        targets.Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnrelatedArchiveIsNotSupported()
    {
        var (result, _) = await TryInstall(await AddNamed("Weapons.zip", "parts/wp_a.dcx"));

        result.IsNotSupported(out _).Should().BeTrue();
    }

    [Fact]
    public async Task TheInstalledSettingsFileIsTheTemplateAsShippedWithNoPassword()
    {
        var loadout = await CreateLoadout();
        var archive = await AddRelease();
        await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout.LoadoutId, installer: Game.LibraryItemInstallers.OfType<SeamlessCoopInstaller>().Single());
        await Synchronizer.Synchronize(Refresh(loadout));

        var settings = GameInstallation.Locations[NexusMods.Sdk.Games.LocationId.Me3Mods].Path / "overlay/SeamlessCoop/ersc_settings.ini";

        (await settings.ReadAllTextAsync()).Should().Be(ErscRelease.SettingsText);
    }

    [Fact]
    public async Task EveryPathTheInstallerAndTheProfileHardcodeIsAMemberOfTheReleaseArchive()
    {
        var path = TemporaryFileManager.CreateFolder().Path / "Seamless Co-op.zip";
        await ErscRelease.Write(path);
        var members = ErscRelease.Members(path);

        members.Should().Contain(ErscRelease.Dll).And.Contain(ErscRelease.Settings).And.Contain(ErscRelease.Launcher);
        ErscRelease.Dll.Should().Be($"{SeamlessCoop.Folder}/{SeamlessCoop.Dll}");
        ErscRelease.Settings.Should().Be($"{SeamlessCoop.Folder}/{SeamlessCoop.Settings}");
        ErscRelease.Launcher.Should().Be(SeamlessCoop.Launcher);
    }

    [Fact]
    public void TheProfileNamesTheNativeAtTheOverlayPathOfTheDllTheInstallerPlaces()
    {
        var gameFolder = new InMemoryFileSystem().FromUnsanitizedFullPath("/steam/ELDEN RING/Game");
        var profile = Me3Profile.Render(new Me3ProfileSpec("eldenring", EldenRingPaths.ModdedSave, [SeamlessCoop.Native]), [Me3ModsLayout.OverlayPath(ErscRelease.Dll)], gameFolder, gameFolder);

        profile.Should().Contain($"path = '{gameFolder / ErscRelease.Dll}'");
    }
}
