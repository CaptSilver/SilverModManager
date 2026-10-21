using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Installers;
using NexusMods.Games.FromSoft.Tests.Fixtures;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers;

namespace NexusMods.Games.FromSoft.Tests;

public class Me3PackageInstallerTests(ITestOutputHelper outputHelper) : ALibraryArchiveInstallerTests<Me3PackageInstallerTests, EldenRingGame>(outputHelper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddFromSoftGames()
            .AddUniversalGameLocator<EldenRingGame>(new Version("1.0"));
    }

    private async Task<(InstallerResult Result, string[] Targets)> TryInstall(LibraryArchive.ReadOnly archive)
    {
        var loadout = await CreateLoadout();
        var installer = Game.LibraryItemInstallers.OfType<Me3PackageInstaller>().Single();

        using var tx = Connection.BeginTransaction();
        var libraryItem = archive.AsLibraryFile().AsLibraryItem();
        var group = new LoadoutItemGroup.New(tx, out var groupId)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, groupId) { Name = libraryItem.Name, LoadoutId = loadout },
        };

        var result = await installer.ExecuteAsync(libraryItem, group, tx, loadout, CancellationToken.None);
        if (!result.IsSuccess) return (result, []);

        var committed = await tx.Commit();
        var targets = committed.Remap(group).Children
            .Select(child => child.TryGetAsLoadoutItemWithTargetPath(out var item) ? (GamePath)item.TargetPath : throw new InvalidOperationException("The child should have a target path."))
            .Select(path => $"{path.LocationId}:{path.Path}")
            .ToArray();
        return (result, targets);
    }

    /// <summary>An archive with the given name, so the package id the installer derives is known.</summary>
    private async Task<LibraryArchive.ReadOnly> AddNamed(string archiveName, params string[] paths)
    {
        var path = TemporaryFileManager.CreateFolder().Path / archiveName;
        await TestArchives.Write(path, paths);
        return await RegisterLocalArchive(path);
    }

    private static string Me3Mods(string path) => $"{LocationId.Me3Mods}:{path}";

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
}
