using FluentAssertions;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Games.FromSoft.Installers;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.FromSoft.Tests;

public abstract class AEldenRingApplyTests<TTest>(ITestOutputHelper helper, bool dirty) : AEldenRingTest<TTest>(helper, dirty)
{
    [Fact]
    public async Task AnInstalledPackageIsAppliedBesideTheAppsDataAndTheGameFolderIsUntouched()
    {
        var loadout = await CreateLoadout();
        var before = SnapshotOfGameFolder();
        var archivePath = TemporaryFileManager.CreateFolder().Path / "Better Weapons.zip";
        await TestArchives.Write(archivePath, "Better Weapons/parts/wp_a.dcx", "Better Weapons/menu/hud.dds");
        var archive = await RegisterLocalArchive(archivePath);

        await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout.LoadoutId, installer: Game.LibraryItemInstallers.OfType<Me3PackageInstaller>().Single());
        await Synchronizer.Synchronize(Refresh(loadout));

        var package = GameInstallation.Locations[LocationId.Me3Mods].Path / "Better Weapons";
        (await (package / "parts/wp_a.dcx").ReadAllTextAsync()).Should().Be("Better Weapons/parts/wp_a.dcx");
        (package / "menu/hud.dds").FileExists.Should().BeTrue();
        SnapshotOfGameFolder().Should().Equal(before);
    }
}

public class ApplyToAPristineInstallTests(ITestOutputHelper helper) : AEldenRingApplyTests<ApplyToAPristineInstallTests>(helper, dirty: false);

public class ApplyToAnInstallLeftByAnotherModManagerTests(ITestOutputHelper helper) : AEldenRingApplyTests<ApplyToAnInstallLeftByAnotherModManagerTests>(helper, dirty: true);
