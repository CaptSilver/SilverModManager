using FluentAssertions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions.ElementComparers;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers.StubbedGames;
using Xunit;

namespace NexusMods.DataModel.Synchronizer.Tests;

/// <summary>
/// Save files live in a location the synchronizer must never index, back up, deploy over or delete.
/// </summary>
public class SaveProtectionTests(ITestOutputHelper helper) : AStubbedGameTest<SaveProtectionTests>(helper)
{
    private static readonly byte[] Save1Bytes = "save one, never ingest me"u8.ToArray();
    private static readonly byte[] Save2Bytes = "save two, never ingest me"u8.ToArray();
    private static readonly byte[] SettingsBytes = "[settings]\nvolume=3\n"u8.ToArray();

    private AbsolutePath Save1 => GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Saves, "save1.dat"));
    private AbsolutePath Save2 => GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Saves, "save2.dat"));
    private AbsolutePath Settings => GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Preferences, "settings.ini"));

    protected override async Task GenerateGameFiles()
    {
        foreach (var (file, bytes) in new[] { (Save1, Save1Bytes), (Save2, Save2Bytes), (Settings, SettingsBytes) })
        {
            file.Parent.CreateDirectory();
            await file.WriteAllBytesAsync(bytes);
        }
    }

    private List<GamePath> IndexedPaths()
    {
        var metadata = GameRegistry.ForceGetMetadata(GameInstallation);
        return Synchronizer.GetDiskStateForGame(metadata).Select(p => p.Path).ToList();
    }

    private async Task AssertSavesUntouched()
    {
        Save1.FileExists.Should().BeTrue();
        Save2.FileExists.Should().BeTrue();
        (await Save1.ReadAllBytesAsync()).Should().Equal(Save1Bytes);
        (await Save2.ReadAllBytesAsync()).Should().Equal(Save2Bytes);
    }

    [Fact]
    public async Task FirstSync_DoesNotIndexOrBackUpSaves()
    {
        await CreateLoadout();

        IndexedPaths().Should().NotContain(p => p.LocationId == LocationId.Saves, "saves are not indexed");
        (await FileStore.HaveFile(Save1Bytes.xxHash3())).Should().BeFalse("saves are not backed up");
        (await FileStore.HaveFile(Save2Bytes.xxHash3())).Should().BeFalse("saves are not backed up");

        // Guards against the assertions above passing only because nothing was indexed at all.
        IndexedPaths().Should().Contain(new GamePath(LocationId.Preferences, "settings.ini"));
        (await FileStore.HaveFile(SettingsBytes.xxHash3())).Should().BeTrue();
    }

    [Fact]
    public async Task SwitchingLoadouts_LeavesSavesAlone()
    {
        var loadoutA = await CreateLoadout();
        var loadoutB = await CreateLoadout();

        loadoutA = await Synchronizer.Synchronize(loadoutA);
        await AssertSavesUntouched();
        _ = await Synchronizer.Synchronize(loadoutB);
        await AssertSavesUntouched();
    }

    [Fact]
    public async Task ResetToOriginalGameState_LeavesSavesAlone()
    {
        await CreateLoadout();
        await Synchronizer.ResetToOriginalGameState(GameInstallation, [LocatorId.From("StubbedGameState.zip")]);
        await AssertSavesUntouched();
    }

    [Fact]
    public async Task UnManage_LeavesSavesAlone()
    {
        await CreateLoadout();
        await LoadoutManager.UnManage(GameInstallation);
        await AssertSavesUntouched();
    }

    [Fact]
    public async Task ModFileTargetingSaves_IsSkippedButStaysInTheLoadout()
    {
        var loadout = await CreateLoadout();

        await using var archiveFile = await CreateTestArchive(new Dictionary<RelativePath, byte[]>
        {
            { "saves/evil.dat", "overwrite the player's save"u8.ToArray() },
            { "game/ok.txt", "a normal mod file"u8.ToArray() },
        });
        var archive = await RegisterLocalArchive(archiveFile.Path);
        await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout, installer: new StubbedGameInstaller(ServiceProvider));

        loadout = Refresh(loadout);
        var evil = new GamePath(LocationId.Saves, "saves/evil.dat");
        var ok = new GamePath(LocationId.Game, "game/ok.txt");

        loadout = await Synchronizer.Synchronize(loadout);

        GameInstallation.Locations.ToAbsolutePath(evil).FileExists.Should().BeFalse();
        GameInstallation.Locations.ToAbsolutePath(ok).FileExists.Should().BeTrue();
        LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath()
            .Select(i => (GamePath)i.TargetPath)
            .Should().Contain([evil, ok], "the item stays visible in the loadout");
        (await Synchronizer.BuildSyncTree(loadout)).Keys.Should().NotContain(p => p.LocationId == LocationId.Saves);

        // Switching away and back must not trip over the skipped item.
        var other = await CreateLoadout();
        _ = await Synchronizer.Synchronize(other);
        loadout = await Synchronizer.Synchronize(Refresh(loadout));
        GameInstallation.Locations.ToAbsolutePath(evil).FileExists.Should().BeFalse();
        await AssertSavesUntouched();
    }

    [Fact]
    public async Task ProtonShaderCache_IsNeverIndexedOrReportedAsAnExternalChange()
    {
        var loadout = await CreateLoadout();

        // Proton writes the cache while the game runs, after the loadout already exists.
        var cache = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "vkd3d-proton.cache"));
        var cacheBytes = "shader cache written by Proton at runtime"u8.ToArray();
        cache.Parent.CreateDirectory();
        await cache.WriteAllBytesAsync(cacheBytes);

        loadout = await Synchronizer.Synchronize(loadout);

        LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath()
            .Select(i => ((GamePath)i.TargetPath).Path.FileName.ToString())
            .Should().NotContain(n => n.StartsWith("vkd3d-proton.cache"), "a cache is not an external change");
        IndexedPaths().Should().NotContain(p => p.Path.FileName.ToString().StartsWith("vkd3d-proton.cache"));
        (await FileStore.HaveFile(cacheBytes.xxHash3())).Should().BeFalse();
        cache.FileExists.Should().BeTrue();

        // Guards against the assertions passing only because nothing was indexed at all.
        IndexedPaths().Should().Contain(new GamePath(LocationId.Preferences, "settings.ini"));
    }

    private async Task<(Loadout.ReadOnly Loadout, LoadoutItemGroup.ReadOnly Mod)> InstallModWithFile(Loadout.ReadOnly loadout, RelativePath archivePath)
    {
        await using var archiveFile = await CreateTestArchive(new Dictionary<RelativePath, byte[]>
        {
            { archivePath, "a normal mod file"u8.ToArray() },
        });
        var archive = await RegisterLocalArchive(archiveFile.Path);
        var result = await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout, installer: new StubbedGameInstaller(ServiceProvider));
        return (Refresh(loadout), result.LoadoutItemGroup!.Value);
    }

    private async Task DisableAndSync(Loadout.ReadOnly loadout, LoadoutItemGroup.ReadOnly mod)
    {
        using var tx = Connection.BeginTransaction();
        tx.Add(mod.Id, LoadoutItem.Disabled, Null.Instance);
        await tx.Commit();
        await Synchronizer.Synchronize(Refresh(loadout));
    }

    [Fact]
    public async Task RemovingTheOnlyManagedFile_KeepsUntrackedFilesInTheSameFolder()
    {
        var loadout = await CreateLoadout();
        var (installed, mod) = await InstallModWithFile(loadout, "game/sub/mod.txt");
        loadout = await Synchronizer.Synchronize(installed);

        var modFile = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "game/sub/mod.txt"));
        var cache = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "game/sub/x.dxvk-cache"));
        modFile.FileExists.Should().BeTrue();

        // The cache is filtered, so the synchronizer never tracks it and a folder holding it looks empty
        // once the managed file is gone.
        await cache.WriteAllBytesAsync("shader cache"u8.ToArray());
        loadout = await Synchronizer.Synchronize(loadout);

        await DisableAndSync(loadout, mod);

        modFile.FileExists.Should().BeFalse();
        cache.FileExists.Should().BeTrue("a file the synchronizer does not track must survive folder cleanup");
    }

    [Fact]
    public async Task RemovingTheOnlyManagedFile_StillRemovesTheEmptyFolder()
    {
        var loadout = await CreateLoadout();
        var (installed, mod) = await InstallModWithFile(loadout, "game/sub2/mod.txt");
        loadout = await Synchronizer.Synchronize(installed);

        var folder = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "game/sub2"));
        folder.DirectoryExists().Should().BeTrue();

        await DisableAndSync(loadout, mod);

        folder.DirectoryExists().Should().BeFalse("a folder with nothing left in it is still cleaned up");
    }
}
