using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.DataModel.Diagnostics;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
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

    private async Task<Diagnostic[]> CaseVariantCards(Loadout.ReadOnly loadout)
    {
        var tree = await Synchronizer.BuildSyncTree(loadout);
        Synchronizer.ProcessSyncTree(tree);
        var cards = new List<Diagnostic>();
        await foreach (var card in new CaseVariantFolderEmitter().Diagnose(loadout, tree.ToFrozenDictionary(), CancellationToken.None))
            cards.Add(card);
        return cards.ToArray();
    }

    private static string DetailsOf(Diagnostic card) => card.FormatDetails(new PlainDiagnosticWriter());

    [Fact]
    public async Task Synchronize_ProtonGameWithTwoFoldersThatDifferOnlyInCase_AppliesAndTheCardNamesBoth()
    {
        UseProtonPrefix(prefixReady: true);
        var loadout = await CreateLoadout();
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "mods/modX/content/a.ws"), "a");
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "Mods/modY/content/b.ws"), "b");

        loadout = await Synchronizer.Synchronize(loadout);

        var game = GameInstallation.Locations[LocationId.Game].Path;
        game.Combine("mods/modX/content/a.ws").FileExists.Should().BeTrue();
        game.Combine("Mods/modY/content/b.ws").FileExists.Should().BeTrue();
        var card = (await CaseVariantCards(loadout)).Should().ContainSingle().Subject;
        card.Severity.Should().Be(DiagnosticSeverity.Critical);
        DetailsOf(card).Should().Contain("{Game}/mods").And.Contain("{Game}/Mods").And.Contain("one folder of each pair");
    }

    [Fact]
    public async Task Synchronize_ProtonGameWithNestedFoldersThatDifferOnlyInCase_AppliesAndTheCardNamesBoth()
    {
        UseProtonPrefix(prefixReady: true);
        var loadout = await CreateLoadout();
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "Mods/modX/Content/a.ws"), "a");
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "Mods/modX/content/b.ws"), "b");

        loadout = await Synchronizer.Synchronize(loadout);

        GameInstallation.Locations[LocationId.Game].Path.Combine("Mods/modX/content/b.ws").FileExists.Should().BeTrue();
        DetailsOf((await CaseVariantCards(loadout)).Should().ContainSingle().Subject)
            .Should().Contain("{Game}/Mods/modX/Content").And.Contain("{Game}/Mods/modX/content");
    }

    [Fact]
    public async Task Synchronize_ProtonGameWithAModFolderBesideAVanillaFolderOfAnotherCase_AppliesAndTheCardNamesBoth()
    {
        UseProtonPrefix(prefixReady: true);
        var vanilla = GameInstallation.Locations[LocationId.Game].Path.Combine("Data/Interface/vanilla.txt");
        vanilla.Parent.CreateDirectory();
        await vanilla.WriteAllTextAsync("vanilla");
        var loadout = await CreateLoadout();
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "Data/interface/x.txt"), "x");

        loadout = await Synchronizer.Synchronize(loadout);

        GameInstallation.Locations[LocationId.Game].Path.Combine("Data/interface/x.txt").FileExists.Should().BeTrue();
        DetailsOf((await CaseVariantCards(loadout)).Should().ContainSingle().Subject)
            .Should().Contain("{Game}/Data/Interface").And.Contain("{Game}/Data/interface");
    }

    [Fact]
    public async Task Synchronize_ProtonGameWritingAFolderBesideAnUnmanagedFolderThatDiffersInCase_AppliesAndTheCardNamesBoth()
    {
        UseProtonPrefix(prefixReady: true);
        var loadout = await CreateLoadout();
        var unmanaged = GameInstallation.Locations[LocationId.Game].Path.Combine("mods/own/user.ws");
        unmanaged.Parent.CreateDirectory();
        await unmanaged.WriteAllTextAsync("the player's own");
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "Mods/modY/content/b.ws"), "b");

        loadout = await Synchronizer.Synchronize(loadout);

        GameInstallation.Locations[LocationId.Game].Path.Combine("Mods/modY/content/b.ws").FileExists.Should().BeTrue();
        DetailsOf((await CaseVariantCards(loadout)).Should().ContainSingle().Subject)
            .Should().Contain("{Game}/mods").And.Contain("{Game}/Mods");
    }

    [Fact]
    public async Task Synchronize_ProtonGameWithSeveralPairs_ListsEveryPairOnOneCard()
    {
        UseProtonPrefix(prefixReady: true);
        var loadout = await CreateLoadout();
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "mods/modX/a.ws"), "a");
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "Mods/modY/b.ws"), "b");
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "Data/Textures/a.dds"), "a");
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "Data/textures/b.dds"), "b");

        loadout = await Synchronizer.Synchronize(loadout);

        var details = DetailsOf((await CaseVariantCards(loadout)).Should().ContainSingle().Subject);
        details.Should().Contain("{Game}/mods").And.Contain("{Game}/Mods").And.Contain("{Game}/Data/Textures").And.Contain("{Game}/Data/textures");
    }

    [Fact]
    public async Task CaseVariantCard_NativeLinuxGameWithTwoFoldersThatDifferOnlyInCase_IsNotShown()
    {
        var loadout = await CreateLoadout();
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "mods/modX/content/a.ws"), "a");
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "Mods/modY/content/b.ws"), "b");

        loadout = await Synchronizer.Synchronize(loadout);

        (await CaseVariantCards(loadout)).Should().BeEmpty();
    }

    [Fact]
    public void FindAll_FoldersThatDifferOnlyInCaseUnderDifferentParents_AreNotSiblingsOfEachOther()
    {
        var files = new[] { "mods/x/a.ws", "Mods/x/b.ws" }.Select(p => new GamePath(LocationId.Game, p));

        var pairs = CaseVariantFolders.FindAll(files);

        pairs.Select(p => (p.First.ToString(), p.Second.ToString())).Should().Equal(("{Game}/Mods", "{Game}/mods"));
    }

    [Fact]
    public void FindAll_ThreeSpellingsOfOneFolder_GiveThreePairs()
    {
        var files = new[] { "mods/a.ws", "Mods/b.ws", "MODS/c.ws" }.Select(p => new GamePath(LocationId.Game, p));

        CaseVariantFolders.FindAll(files).Should().HaveCount(3);
    }

    [Fact]
    public void FindAll_OneSpellingPerFolder_GivesNoPair()
    {
        var files = new[] { "Mods/x/a.ws", "Mods/y/b.ws", "bin/c.dll" }.Select(p => new GamePath(LocationId.Game, p));

        CaseVariantFolders.FindAll(files).Should().BeEmpty();
    }
    [Fact]
    public async Task Synchronize_ProtonGameWithOneSpellingPerFolder_Passes()
    {
        UseProtonPrefix(prefixReady: true);
        var loadout = await CreateLoadout();
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "Mods/modX/content/a.ws"), "a");
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "Mods/modY/content/b.ws"), "b");

        await Synchronizer.Synchronize(loadout);

        GameInstallation.Locations[LocationId.Game].Path.Combine("Mods/modY/content/b.ws").FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task Synchronize_NativeLinuxGameWithTwoFoldersThatDifferOnlyInCase_IsNotRefused()
    {
        var loadout = await CreateLoadout();
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "mods/modX/content/a.ws"), "a");
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "Mods/modY/content/b.ws"), "b");

        await Synchronizer.Synchronize(loadout);

        GameInstallation.Locations[LocationId.Game].Path.Combine("mods/modX/content/a.ws").FileExists.Should().BeTrue();
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
