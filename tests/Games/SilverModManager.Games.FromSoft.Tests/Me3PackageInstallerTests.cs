using FluentAssertions;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Games.FromSoft.Installers;
using NexusMods.Games.FromSoft.Tests.Fixtures;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Library;

namespace NexusMods.Games.FromSoft.Tests;

public class Me3PackageInstallerTests(ITestOutputHelper outputHelper) : AEldenRingInstallerTests<Me3PackageInstallerTests>(outputHelper)
{
    private Task<(InstallerResult Result, string[] Targets)> TryInstall(LibraryArchive.ReadOnly archive)
        => TryInstall<Me3PackageInstaller>(archive);

    [Fact]
    public async Task ABareTreeAtTheArchiveRootIsInstalledUnderTheArchiveName()
    {
        var archive = await AddNamed("Weapons.zip", "parts/wp_a.dcx", "menu/hud.dds");

        var (result, targets) = await TryInstall(archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(Me3Mods("Weapons/menu/hud.dds"), Me3Mods("Weapons/parts/wp_a.dcx"));
    }

    [Fact]
    public async Task ASingleWrapperFolderIsDescendedInto()
    {
        var archive = await AddNamed("Weapons.zip", "My Cool Mod/parts/wp_a.dcx");

        var (_, targets) = await TryInstall(archive);

        targets.Should().BeEquivalentTo(Me3Mods("Weapons/parts/wp_a.dcx"));
    }

    [Fact]
    public async Task TheModFolderConventionIsDescendedInto()
    {
        var archive = await AddNamed("Weapons.zip", "mod/chr/c0000.dcx");

        var (_, targets) = await TryInstall(archive);

        targets.Should().BeEquivalentTo(Me3Mods("Weapons/chr/c0000.dcx"));
    }

    [Fact]
    public async Task AReadmeBesideTheTreeIsLeftBehind()
    {
        var archive = await AddNamed("Weapons.zip", "parts/wp_a.dcx", "README.txt", "preview.png");

        var (_, targets) = await TryInstall(archive);

        targets.Should().BeEquivalentTo(Me3Mods("Weapons/parts/wp_a.dcx"), Me3Mods("Weapons/preview.png"), Me3Mods("Weapons/README.txt"));
    }

    [Fact]
    public async Task ModEngineLauncherFilesBesideTheModFolderDoNotBlockDescent()
    {
        var archive = await AddNamed("Weapons.zip", "modengine2_launcher.exe", "config_eldenring.toml", "mod/parts/wp.dcx");

        var (_, targets) = await TryInstall(archive);

        targets.Should().BeEquivalentTo([Me3Mods("Weapons/parts/wp.dcx")], "the launcher files sit outside the package root");
    }

    [Fact]
    public async Task ARegulationFileAloneIsAPackage()
    {
        var archive = await AddNamed("Regulation.zip", "regulation.bin");

        var (_, targets) = await TryInstall(archive);

        targets.Should().BeEquivalentTo(Me3Mods("Regulation/regulation.bin"));
    }

    [Fact]
    public async Task APartsFolderAndARegulationFileAtTheSameLevelAreOneRoot()
    {
        var archive = await AddNamed("Both.zip", "parts/wp_a.dcx", "regulation.bin");

        var (_, targets) = await TryInstall(archive);

        targets.Should().BeEquivalentTo(Me3Mods("Both/parts/wp_a.dcx"), Me3Mods("Both/regulation.bin"));
    }

    [Fact]
    public async Task AnArchiveWithoutAssetFoldersIsNotSupported()
    {
        var archive = await AddNamed("Docs.zip", "random/thing.bin", "notes.docx");

        var (result, _) = await TryInstall(archive);

        result.IsNotSupported(out _).Should().BeTrue();
    }

    [Fact]
    public async Task TwoOptionFoldersAreNotSupportedAndTheReasonNamesBoth()
    {
        var archive = await AddNamed("Minimal HUD.zip", "OPTION 1 - Normal Backgrounds/menu/x.gfx", "OPTION 2 - Translucent Backgrounds/menu/y.gfx");

        var (result, _) = await TryInstall(archive);

        result.IsNotSupported(out var reason).Should().BeTrue();
        reason.Should().Contain("OPTION 1 - Normal Backgrounds").And.Contain("OPTION 2 - Translucent Backgrounds");
    }

    [Fact]
    public async Task APackageNameWithASpaceAndAnApostropheIsAValidFolderName()
    {
        var archive = await AddNamed("Malenia's Blade Pack.zip", "parts/wp_a.dcx");

        var (result, targets) = await TryInstall(archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(Me3Mods("Malenia's Blade Pack/parts/wp_a.dcx"));
    }

    [Fact]
    public async Task NothingIsEverTargetedAtTheGameLocation()
    {
        var archive = await AddFromPaths("parts/wp_a.dcx", "regulation.bin", "mod/menu/x.gfx");

        var (_, targets) = await TryInstall(archive);

        targets.Should().NotBeEmpty().And.OnlyContain(t => t.StartsWith($"{LocationId.Me3Mods}:"));
    }

    [Theory]
    [InlineData("natives.zip")]
    [InlineData("Natives.zip")]
    [InlineData("overlay.zip")]
    [InlineData("OVERLAY.zip")]
    [InlineData("_merged.zip")]
    [InlineData("_MERGED.zip")]
    public async Task AnArchiveWhoseNameIsAFolderTheLoaderGivesAMeaningIsNotSupported(string archiveName)
    {
        var archive = await AddNamed(archiveName, "parts/wp_a.dcx");

        var (result, targets) = await TryInstall(archive);

        result.IsNotSupported(out var reason).Should().BeTrue();
        reason.Should().Contain("reserved");
        targets.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Weapons.tar.gz", "Weapons")]
    [InlineData("Weapons.tar.xz", "Weapons")]
    [InlineData("Weapons.tar.zst", "Weapons")]
    [InlineData("Weapons.tar.bz2", "Weapons")]
    [InlineData("Weapons.TAR.GZ", "Weapons")]
    [InlineData("Weapons.v1.2.zip", "Weapons.v1.2")]
    [InlineData("Weapons.7z", "Weapons")]
    public async Task TheWholeArchiveExtensionIsDroppedFromThePackageId(string archiveName, string expectedId)
    {
        var archive = await AddNamed(archiveName, "parts/wp_a.dcx");

        var (result, targets) = await TryInstall(archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(Me3Mods($"{expectedId}/parts/wp_a.dcx"));
    }

    [Theory]
    [InlineData(".zip")]
    [InlineData(".tar.gz")]
    public async Task AnArchiveWhoseNameIsOnlyAnExtensionIsNotSupportedAndTheReasonSaysWhy(string archiveName)
    {
        var archive = await AddNamed(archiveName, "parts/wp_a.dcx");

        var (result, targets) = await TryInstall(archive);

        result.IsNotSupported(out var reason).Should().BeTrue();
        reason.Should().Contain("no name");
        targets.Should().BeEmpty();
    }

    [Theory]
    [InlineData("natives.tar.gz")]
    [InlineData("overlay.tar.xz")]
    [InlineData("_merged.tar.zst")]
    public async Task AReservedNameBehindAMultiPartExtensionIsStillNotSupported(string archiveName)
    {
        var archive = await AddNamed(archiveName, "parts/wp_a.dcx");

        var (result, _) = await TryInstall(archive);

        result.IsNotSupported(out var reason).Should().BeTrue();
        reason.Should().Contain("reserved");
    }

    [Theory]
    [InlineData("...zip")]
    [InlineData("....tar.gz")]
    [InlineData("Weapons..zip")]
    [InlineData("Weapons .zip")]
    [InlineData("a:b.zip")]
    [InlineData("a|b.zip")]
    [InlineData("a\u0001b.zip")]
    public async Task AnArchiveWhoseNameIsNotAUsableFolderNameIsNotSupported(string archiveName)
    {
        var archive = await AddNamed(archiveName, "parts/wp_a.dcx");

        var (result, targets) = await TryInstall(archive);

        result.IsNotSupported(out var reason).Should().BeTrue();
        reason.Should().Contain("folder name");
        targets.Should().BeEmpty();
    }

    [Theory]
    [InlineData("../../Weapons.tar.gz", "Weapons")]
    [InlineData("/abs/Weapons.zip", "Weapons")]
    [InlineData(@"..\..\Weapons.tar.xz", "Weapons")]
    public void TheIdIsTakenFromTheFileNameAloneWhateverPathTheNameCarries(string archiveName, string expectedId)
        => Me3InstallerFiles.ModId(archiveName).Should().Be(expectedId);

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a*b")]
    [InlineData("...")]
    [InlineData("a.")]
    [InlineData("a ")]
    public void AnIdThatIsNotASingleSafeFolderNameIsRefused(string id)
        => Me3InstallerFiles.IdRefusal(id).Should().NotBeNull();
}
