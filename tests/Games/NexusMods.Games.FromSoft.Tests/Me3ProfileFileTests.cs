using FluentAssertions;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Installers;
using NexusMods.Games.FromSoft.Me3;
using NexusMods.Games.FromSoft.Tests.Fixtures;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FromSoft.Tests;

public class Me3ProfileFileTests(ITestOutputHelper helper) : AEldenRingTest<Me3ProfileFileTests>(helper)
{
    private static SyncNode InLoadout() => new()
    {
        Loadout = new SyncNodePart { Hash = Hash.From(1UL), Size = Size.From(1), LastModifiedTicks = 0 },
    };

    private static SyncNode OnDiskOnly() => new()
    {
        Disk = new SyncNodePart { Hash = Hash.From(1UL), Size = Size.From(1), LastModifiedTicks = 0 },
    };

    private static GamePath Mods(string path) => new(LocationId.Me3Mods, path);

    private IIntrinsicFile ProfileFile(Loadout.ReadOnly loadout)
        => ((ALoadoutSynchronizer)Synchronizer).IntrinsicFiles(loadout).Should().ContainKey(Mods("silver.me3")).WhoseValue;

    private async Task<string> Write(Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> tree)
    {
        using var stream = new MemoryStream();
        await ProfileFile(loadout).Write(stream, loadout, tree);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    [Fact]
    public async Task TheSynchronizerGeneratesTheProfileAtTheFixedPathInTheModsFolder()
    {
        var loadout = await CreateLoadout();

        ProfileFile(loadout).Path.Should().Be(Mods("silver.me3"));
    }

    [Fact]
    public async Task OnlyFilesTheLoadoutHasAreListed()
    {
        var loadout = await CreateLoadout();
        var tree = new Dictionary<GamePath, SyncNode>
        {
            { Mods("kept/parts/a.dcx"), InLoadout() },
            { Mods("leftover/parts/b.dcx"), OnDiskOnly() },
            { new GamePath(LocationId.Game, "Game/eldenring.exe"), InLoadout() },
        };

        var text = await Write(loadout, tree);

        var modsFolder = GameInstallation.Locations[LocationId.Me3Mods].Path;
        text.Should().Contain($"id = \"kept\"\npath = '{modsFolder}/kept/'").And.NotContain("leftover").And.NotContain("eldenring.exe");
    }

    [Fact]
    public async Task TheSeamlessCoopNativeIsNamedInsideTheGameFolderOfTheInstallation()
    {
        var loadout = await CreateLoadout();
        var tree = new Dictionary<GamePath, SyncNode> { { Mods("overlay/SeamlessCoop/ersc.dll"), InLoadout() } };

        var text = await Write(loadout, tree);

        text.Should().Contain($"path = '{RealGameFolder}/SeamlessCoop/ersc.dll'");
    }

    [Fact]
    public async Task ApplyWritesTheProfileBesideThePackagesWithoutTouchingTheGameFolder()
    {
        var loadout = await CreateLoadout();
        var before = SnapshotOfGameFolder();
        var archivePath = TemporaryFileManager.CreateFolder().Path / "Better Weapons.zip";
        await TestArchives.Write(archivePath, "parts/wp_a.dcx");
        var archive = await RegisterLocalArchive(archivePath);

        await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout.LoadoutId, installer: Game.LibraryItemInstallers.OfType<Me3PackageInstaller>().Single());
        await Synchronizer.Synchronize(Refresh(loadout));

        var modsFolder = GameInstallation.Locations[LocationId.Me3Mods].Path;
        var text = await (modsFolder / "silver.me3").ReadAllTextAsync();
        text.Should().Contain("id = \"Better Weapons\"").And.Contain(EldenRingPaths.ModdedSave);
        SnapshotOfGameFolder().Should().Equal(before);
    }

    [Fact]
    public async Task AHandEditedProfileIsOverwrittenByTheNextApply()
    {
        var loadout = await CreateLoadout();
        var archivePath = TemporaryFileManager.CreateFolder().Path / "Better Weapons.zip";
        await TestArchives.Write(archivePath, "parts/wp_a.dcx");
        var archive = await RegisterLocalArchive(archivePath);
        await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout.LoadoutId, installer: Game.LibraryItemInstallers.OfType<Me3PackageInstaller>().Single());
        await Synchronizer.Synchronize(Refresh(loadout));
        var profile = GameInstallation.Locations[LocationId.Me3Mods].Path / "silver.me3";
        var generated = await profile.ReadAllTextAsync();

        await profile.WriteAllTextAsync(generated + "\n[[packages]]\nid = \"hand-added\"\npath = '/elsewhere/'\n");
        await Synchronizer.Synchronize(Refresh(loadout));

        (await profile.ReadAllTextAsync()).Should().Be(generated);
    }
}
