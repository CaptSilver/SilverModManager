using System.Runtime.InteropServices;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// A Windows game whose Proton prefix is missing must be refused before anything is written, and every
/// later write must check that the folders it writes into can actually be written.
/// </summary>
public class ProtonPreflightTests(ITestOutputHelper helper) : AStubbedGameTest<ProtonPreflightTests>(helper)
{
    private AbsolutePath _prefix;

    private AbsolutePath Preferences => GameInstallation.Locations[LocationId.Preferences].Path;
    private AbsolutePath Saves => GameInstallation.Locations[LocationId.Saves].Path;

    protected override Task GenerateGameFiles()
    {
        Preferences.CreateDirectory();
        Saves.CreateDirectory();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Turns the located game into a Windows build with a prefix at a fresh path, and re-locates it.
    /// </summary>
    private void UseProtonPrefix(bool prefixReady)
    {
        _prefix = TemporaryFileManager.CreateFolder().Path.Combine("pfx");
        if (prefixReady) CreatePrefix();

        var locator = ServiceProvider.GetServices<IGameLocator>().OfType<UniversalStubbedGameLocator<StubbedGame>>().Single();
        locator.Platform = OSPlatform.Windows;
        locator.WinePrefixPath = _prefix;

        GameRegistry.ClearCache();
        GameInstallation = GameRegistry.LocateGameInstallations().Single(i => i.Game is StubbedGame);
        GameInstallation.LocatorResult.WinePrefixPath.Should().Be(_prefix);
    }

    private void CreatePrefix()
    {
        _prefix.CreateDirectory();
        File.WriteAllText(_prefix.Combine("system.reg").GetFullPath(), "WINE REGISTRY Version 2");
    }

    private void DeletePrefixRegistry() => _prefix.Combine("system.reg").Delete();

    private static string[] Snapshot(params AbsolutePath[] roots)
    {
        return roots
            .Where(r => r.DirectoryExists())
            .SelectMany(r => Directory.GetFileSystemEntries(r.GetFullPath(), "*", SearchOption.AllDirectories).Append(r.GetFullPath()))
            .Select(entry => Directory.Exists(entry)
                ? $"{entry} dir {Directory.GetLastWriteTimeUtc(entry).Ticks}"
                : $"{entry} file {new FileInfo(entry).Length}")
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<Loadout.ReadOnly> AddFileMod(Loadout.ReadOnly loadout, GamePath path, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = bytes.xxHash3();
        if (!await FileStore.HaveFile(hash))
            await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(path.Path, new MemoryStream(bytes)), hash, Size.FromLong(bytes.Length))]);

        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, "mod " + path.Path);
        AddFile(tx, loadout.LoadoutId, group, path, content);
        await tx.Commit();
        return Refresh(loadout);
    }

    private async Task<Loadout.ReadOnly> ManagedAndSyncedWithPrefix()
    {
        UseProtonPrefix(prefixReady: true);
        var loadout = await CreateLoadout();
        return await Synchronizer.Synchronize(loadout);
    }

    [Fact]
    public async Task CreateLoadout_PrefixMissing_ThrowsNamingTheGameAndWritesNothing()
    {
        UseProtonPrefix(prefixReady: false);
        var gameFolder = GameInstallation.Locations[LocationId.Game].Path;
        var before = Snapshot(_prefix.Parent, gameFolder, Preferences, Saves);

        var act = async () => await LoadoutManager.CreateLoadout(GameInstallation, "refused");

        var exception = (await act.Should().ThrowAsync<SynchronizerPreflightException>()).Which;
        exception.Message.Should().Contain(GameInstallation.Game.DisplayName);
        exception.Message.Should().Contain(_prefix.GetFullPath());
        exception.Message.Should().Contain("launch");
        exception.Message.Should().Contain("Proton");

        Loadout.All(Connection.Db).Should().BeEmpty();
        GameRegistry.TryGetMetadata(GameInstallation, out _).Should().BeFalse("nothing is recorded for a game that cannot be managed");
        Snapshot(_prefix.Parent, gameFolder, Preferences, Saves).Should().Equal(before);
    }

    [Fact]
    public async Task CreateLoadoutAndSynchronize_PrefixExistsButProviderIsNull_Pass()
    {
        UseProtonPrefix(prefixReady: true);
        GameInstallation.LocatorResult.LinuxCompatabilityDataProvider.Should().BeNull();

        var loadout = await CreateLoadout();
        var act = async () => await Synchronizer.Synchronize(loadout);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CreateLoadoutAndSynchronize_NativeLinuxGame_Pass()
    {
        GameInstallation.LocatorResult.WinePrefixPath.Should().BeNull();
        GameInstallation.LocatorResult.Platform.Should().Be(OSPlatform.Linux);

        var loadout = await CreateLoadout();
        var act = async () => await Synchronizer.Synchronize(loadout);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Synchronize_PrefixDeletedAfterAdd_Throws()
    {
        var loadout = await ManagedAndSyncedWithPrefix();
        DeletePrefixRegistry();

        var act = async () => await Synchronizer.Synchronize(loadout);

        await act.Should().ThrowAsync<SynchronizerPreflightException>();
    }

    [Fact]
    public async Task ResetToOriginalGameState_PrefixDeletedAfterAdd_Throws()
    {
        await ManagedAndSyncedWithPrefix();
        DeletePrefixRegistry();

        var act = async () => await Synchronizer.ResetToOriginalGameState(GameInstallation, GameInstallation.LocatorResult.LocatorIds.ToArray());

        await act.Should().ThrowAsync<SynchronizerPreflightException>();
    }

    [Fact]
    public async Task UnManageWithClean_PrefixDeletedAfterAdd_ThrowsBeforeDeletingAnyRow()
    {
        await ManagedAndSyncedWithPrefix();
        DeletePrefixRegistry();

        var act = async () => await LoadoutManager.UnManage(GameInstallation);

        await act.Should().ThrowAsync<SynchronizerPreflightException>();
        Loadout.All(Connection.Db).Should().ContainSingle("the loadout is only removed once the game folder was cleaned");
        GameRegistry.ForceGetMetadata(GameInstallation).Contains(GameInstallMetadata.LastSyncedLoadout).Should().BeTrue();
    }

    [Fact]
    public async Task UnManageWithoutClean_PrefixDeletedAfterAdd_Succeeds()
    {
        await ManagedAndSyncedWithPrefix();
        DeletePrefixRegistry();

        await LoadoutManager.UnManage(GameInstallation, cleanGameFolder: false);

        Loadout.All(Connection.Db).Should().BeEmpty();
        GameRegistry.ForceGetMetadata(GameInstallation).Contains(GameInstallMetadata.LastSyncedLoadout).Should().BeFalse();
    }

    [Fact]
    public async Task Synchronize_PendingWriteIntoUnwritableLocation_ThrowsNamingThePath()
    {
        Assert.SkipWhen(Environment.IsPrivilegedProcess, "root ignores directory permissions");

        var loadout = await CreateLoadout();
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Preferences, "new.ini"), "[new]");

        var original = File.GetUnixFileMode(Preferences.GetFullPath());
        File.SetUnixFileMode(Preferences.GetFullPath(), UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var act = async () => await Synchronizer.Synchronize(loadout);

            var exception = (await act.Should().ThrowAsync<SynchronizerPreflightException>()).Which;
            exception.Message.Should().Contain(Preferences.GetFullPath());
        }
        finally
        {
            File.SetUnixFileMode(Preferences.GetFullPath(), original);
        }

        Preferences.Combine("new.ini").FileExists.Should().BeFalse("nothing is deployed when a location cannot be written");
    }

    [Fact]
    public async Task Synchronize_LocationRootMissingButParentWritable_Passes()
    {
        var loadout = await CreateLoadout();
        Preferences.DeleteDirectory(recursive: true);
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Preferences, "new.ini"), "[new]");

        await Synchronizer.Synchronize(loadout);

        Preferences.Combine("new.ini").FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task Synchronize_NothingPending_CreatesNothingAnywhere()
    {
        var loadout = await CreateLoadout();
        loadout = await Synchronizer.Synchronize(loadout);
        var before = Snapshot(GameInstallation.Locations[LocationId.Game].Path, Preferences, Saves);

        await Task.Delay(50);
        await Synchronizer.Synchronize(loadout);

        Snapshot(GameInstallation.Locations[LocationId.Game].Path, Preferences, Saves).Should().Equal(before, "the writability check only runs for locations with pending writes");
    }

    [Fact]
    public async Task Synchronize_WritingOnlyIntoGame_LeavesPreferencesAndSavesRootsAlone()
    {
        var loadout = await CreateLoadout();
        var preferencesBefore = Directory.GetLastWriteTimeUtc(Preferences.GetFullPath());
        var savesBefore = Directory.GetLastWriteTimeUtc(Saves.GetFullPath());
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "mod.dll"), "dll");

        await Task.Delay(50);
        await Synchronizer.Synchronize(loadout);

        Directory.GetLastWriteTimeUtc(Preferences.GetFullPath()).Should().Be(preferencesBefore);
        Directory.GetLastWriteTimeUtc(Saves.GetFullPath()).Should().Be(savesBefore);
    }

    [Fact]
    public async Task Synchronize_LeftoverProbeFile_IsNeverIndexedOrBackedUp()
    {
        Preferences.CreateDirectory();
        var probe = Preferences.Combine(ProtonMessages.ProbeFileName);
        await probe.WriteAllTextAsync("leftover from a crash");

        var loadout = await CreateLoadout();
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Preferences, "new.ini"), "[new]");
        await Synchronizer.Synchronize(loadout);

        var metadata = GameRegistry.ForceGetMetadata(GameInstallation);
        Synchronizer.GetDiskStateForGame(metadata).Should().NotContain(p => p.Path.FileName.ToString() == ProtonMessages.ProbeFileName);
        (await FileStore.HaveFile(Encoding.UTF8.GetBytes("leftover from a crash").xxHash3())).Should().BeFalse();
        probe.FileExists.Should().BeFalse("the next probe of that folder replaces and removes the leftover");
    }
}
