using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Games.Larian.BaldursGate3.Models;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using NexusMods.Paths;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Larian.Tests.BaldursGate3.Modsettings;

/// <summary>
/// Handing the game back (Unmanage) and moving an existing loadout's <c>modsettings.lsx</c> from the Overrides
/// group to the generated file.
/// </summary>
public class ModsettingsResetTests(ITestOutputHelper helper) : ModsettingsFixture(helper)
{
    [Fact]
    public async Task UnmanageKeepsBaseAndGameInstalledEntriesAndDropsTheRemovedPaks()
    {
        await WriteGameInstalledPak();
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Alpha mod", "alpha.pak", AlphaPak());
        await Apply(loadout);
        DiskNames().Should().Equal("GustavX", "HonourX", "Game Mod", "Alpha");

        await Synchronizer.ResetToOriginalGameState(GameInstallation, GameInstallation.LocatorResult.LocatorIds.ToArray());

        OnDisk(Pak("alpha.pak")).FileExists.Should().BeFalse("the reset removed the app's pak");
        OnDisk(Pak(GameModPak)).FileExists.Should().BeTrue();
        DiskNames().Should().Equal("GustavX", "HonourX", "Game Mod");
    }

    [Fact]
    public async Task UnmanageListsTheGameInstalledPaksWhoseGroupWasSwitchedOff()
    {
        await WriteGameInstalledPak();
        var loadout = await CreateLoadout();
        await Apply(loadout);

        await DisableItem(GameInstalledGroupOf(loadout).Id);
        await Apply(loadout);
        DiskNames().Should().Equal("GustavX", "HonourX");

        await Synchronizer.ResetToOriginalGameState(GameInstallation, GameInstallation.LocatorResult.LocatorIds.ToArray());

        OnDisk(Pak(GameModPak)).FileExists.Should().BeTrue();
        DiskNames().Should().Equal("GustavX", "HonourX", "Game Mod");
        Disk().Entries.Single(e => e.Uuid == GameModUuid).Md5.Should().Be(GameModMd5);
    }

    [Fact]
    public async Task UnmanageDoesNotListASwitchedOffGameInstalledPakThatIsGoneFromDisk()
    {
        await WriteGameInstalledPak();
        var loadout = await CreateLoadout();
        await Apply(loadout);

        await DisableItem(GameInstalledGroupOf(loadout).Id);
        await Apply(loadout);
        OnDisk(Pak(GameModPak)).Delete();

        await Synchronizer.ResetToOriginalGameState(GameInstallation, GameInstallation.LocatorResult.LocatorIds.ToArray());

        DiskNames().Should().Equal("GustavX", "HonourX");
    }

    [Fact]
    public async Task AnOverridesRowForTheFileIsDroppedByTheFirstSyncWithoutTouchingTheFileFirst()
    {
        var loadout = await CreateLoadout();
        await WriteModsettings(BaseEntries, """<version major="4" minor="9" revision="1" build="77"/>""");

        // What an earlier version left behind: the file ingested as an ordinary overrides row.
        var bytes = await OnDisk(ModsettingsPath).ReadAllBytesAsync();
        var hash = bytes.xxHash3();
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory("modsettings.lsx", new MemoryStream(bytes)), hash, Size.FromLong(bytes.Length))]);
        var overrides = LoadoutOverridesGroup.FindByOverridesFor(Connection.Db, loadout.Id).Single();
        using (var tx = Connection.BeginTransaction())
        {
            OverridesGroupItems.AddFile(tx, Refresh(loadout), LoadoutOverridesGroupId.From(overrides.Id), ModsettingsPath, hash, Size.FromLong(bytes.Length));
            await tx.Commit();
        }
        OverridesPaths(loadout).Should().Contain("modsettings.lsx");

        await Apply(loadout);

        OverridesPaths(loadout).Should().NotContain("modsettings.lsx");
        // The sync did not take the file off disk: the version and base entries it held survive into the write.
        Disk().VersionAttributes["major"].Should().Be("4");
        Disk().VersionAttributes["minor"].Should().Be("9");
        DiskNames().Should().Equal("GustavX", "HonourX");
    }

    private string[] OverridesPaths(Loadout.ReadOnly loadout) =>
        LoadoutOverridesGroup.FindByOverridesFor(Connection.Db, loadout.Id)
            .SelectMany(group => group.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath())
            .Select(item => ((GamePath)item.TargetPath).Path.ToString())
            .ToArray();
}
