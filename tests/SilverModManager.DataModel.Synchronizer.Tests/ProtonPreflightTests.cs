using System.Collections.Frozen;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using FluentAssertions;
using System.Reactive.Linq;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.FolderCase;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
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

    /// <summary>The files the store vouches for, read when the synchronizer asks; null leaves the real (empty) answer.</summary>
    private Func<(string Path, FileInfo Info)[]>? AttestVanilla { get; set; }

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services);
        var real = services.Last(d => d.ServiceType == typeof(IFileHashesService));
        services.Remove(real);
        return services.AddSingleton<IFileHashesService>(sp =>
        {
            var proxy = DispatchProxy.Create<IFileHashesService, AttestingHashes>();
            var attesting = (AttestingHashes)(object)proxy;
            attesting.Inner = (IFileHashesService)ActivatorUtilities.CreateInstance(sp, real.ImplementationType ?? throw new InvalidOperationException());
            attesting.Test = this;
            return proxy;
        });
    }

    /// <summary>Forwards everything to the real service except which files are attested, which a test can set.</summary>
    public class AttestingHashes : DispatchProxy
    {
        internal IFileHashesService Inner = null!;
        internal ProtonPreflightTests Test = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IFileHashesService.AttestedFiles) && Test.AttestVanilla is { } attest)
            {
                return attest().ToFrozenDictionary(
                    a => new GamePath(LocationId.Game, RelativePath.FromUnsanitizedInput(a.Path)),
                    a => new AttestedLookup(EntityId.From(1), RelativePath.FromUnsanitizedInput(a.Path), default, Size.FromLong(a.Info.Length), new DateTimeOffset(a.Info.LastWriteTimeUtc)));
            }

            try
            {
                return targetMethod?.Invoke(Inner, args);
            }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            {
                throw e.InnerException;
            }
        }
    }

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
        if (!await FileStore.HaveFile(FileIdentity.XxHash3(hash)))
            await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(path.Path, new MemoryStream(bytes)), FileIdentity.XxHash3(hash), Size.FromLong(bytes.Length))]);

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

    private AbsolutePath GameDir => GameInstallation.Locations[LocationId.Game].Path;

    private string[] GameFolders()
        => Directory.EnumerateDirectories(GameDir.GetFullPath(), "*", SearchOption.AllDirectories).Select(d => Path.GetRelativePath(GameDir.GetFullPath(), d)).Order(StringComparer.Ordinal).ToArray();

    private async Task<AbsolutePath> WriteOnDisk(string relative, string content)
    {
        var file = GameDir.Combine(relative);
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync(content);
        return file;
    }

    private IEnumerable<string> DiskRowPaths()
        => DiskStateEntry.FindByGame(Connection.Db, GameRegistry.ForceGetMetadata(GameInstallation).Id)
            .Select(e => ((GamePath)e.Path).Path.ToString());

    [Fact]
    public async Task Synchronize_ProtonGameWithTwoFoldersThatDifferOnlyInCase_DeploysOneFolder_NoCard()
    {
        UseProtonPrefix(prefixReady: true);
        var loadout = await CreateLoadout();
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "mods/modX/content/a.ws"), "a");
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "Mods/modY/content/b.ws"), "b");

        loadout = await Synchronizer.Synchronize(loadout);

        GameDir.Combine("Mods/modX/content/a.ws").FileExists.Should().BeTrue();
        GameDir.Combine("Mods/modY/content/b.ws").FileExists.Should().BeTrue();
        GameFolders().Should().NotContain("mods");
        (await CaseVariantCards(loadout)).Should().BeEmpty();
        DiskRowPaths().Where(p => p.StartsWith("mods", StringComparison.OrdinalIgnoreCase)).Should().OnlyContain(p => p.StartsWith("Mods/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Synchronize_ProtonGameWithNestedFoldersThatDifferOnlyInCase_DeploysOneFolder_NoCard()
    {
        UseProtonPrefix(prefixReady: true);
        var loadout = await CreateLoadout();
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "Mods/modX/Content/a.ws"), "a");
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "Mods/modX/content/b.ws"), "b");

        loadout = await Synchronize(loadout);

        GameFolders().Where(f => f.StartsWith("Mods/modX/", StringComparison.Ordinal)).Should().HaveCount(1);
        Directory.EnumerateFiles(GameDir.Combine("Mods/modX").GetFullPath(), "*", SearchOption.AllDirectories).Should().HaveCount(2);
        (await CaseVariantCards(loadout)).Should().BeEmpty();
    }

    private async Task<Loadout.ReadOnly> Synchronize(Loadout.ReadOnly loadout) => await Synchronizer.Synchronize(loadout);

    [Fact]
    public async Task Synchronize_ProtonGameWithAModFolderBesideAnUnattestedFolderOfAnotherCase_DeploysIntoTheOnDiskSpelling()
    {
        UseProtonPrefix(prefixReady: true);
        await WriteOnDisk("Data/Interface/vanilla.txt", "vanilla");
        var loadout = await CreateLoadout();
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "Data/interface/x.txt"), "x");

        loadout = await Synchronize(loadout);

        GameDir.Combine("Data/Interface/x.txt").FileExists.Should().BeTrue();
        GameFolders().Should().NotContain("Data/interface");
        (await CaseVariantCards(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task Synchronize_ProtonGameWithAModFolderBesideAnAttestedVanillaFolder_DeploysIntoVanillaSpelling()
    {
        UseProtonPrefix(prefixReady: true);
        var vanilla = await WriteOnDisk("Data/Interface/vanilla.txt", "vanilla");
        // A second spelling holding more files on disk: only the store's attestation can make Interface the winner.
        await WriteOnDisk("Data/interface/own1.txt", "1");
        await WriteOnDisk("Data/interface/own2.txt", "2");
        AttestVanilla = () => [("Data/Interface/vanilla.txt", new FileInfo(vanilla.GetFullPath()))];
        var loadout = await CreateLoadout();
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "Data/interface/x.txt"), "x");
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "Data/interface/own1.txt"), "1");

        loadout = await Synchronize(loadout);

        GameDir.Combine("Data/Interface/x.txt").FileExists.Should().BeTrue();
        GameDir.Combine("Data/Interface/vanilla.txt").FileExists.Should().BeTrue();
        GameFolders().Should().NotContain("Data/interface", "the store's spelling wins and the other folder was merged into it");
    }

    [Fact]
    public async Task Synchronize_ProtonGameWritingAFolderBesideAnUnmanagedFolderThatDiffersInCase_DeploysIntoTheOnDiskSpelling()
    {
        UseProtonPrefix(prefixReady: true);
        var loadout = await CreateLoadout();
        await WriteOnDisk("mods/own/user.ws", "the player's own");
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "Mods/modY/content/b.ws"), "b");

        loadout = await Synchronize(loadout);

        GameDir.Combine("mods/modY/content/b.ws").FileExists.Should().BeTrue();
        GameDir.Combine("mods/own/user.ws").FileExists.Should().BeTrue();
        GameFolders().Should().NotContain("Mods");
        (await CaseVariantCards(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task Synchronize_ProtonGameWithTwinsLeftOnDiskByAnEarlierApply_MergesAndRecordsCanonical()
    {
        // The same loadout applied by a build that did not merge: both spellings are on disk and in the rows.
        var loadout = await CreateLoadout();
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "mods/modX/content/a.ws"), "a");
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "Mods/modX/content/b.ws"), "b");
        loadout = await Synchronizer.Synchronize(loadout);
        var time = new DateTime(2019, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(GameDir.Combine("mods/modX/content/a.ws").GetFullPath(), time);
        File.SetLastWriteTimeUtc(GameDir.Combine("Mods/modX/content/b.ws").GetFullPath(), time);
        GameFolders().Should().Contain("mods").And.Contain("Mods");
        UseProtonPrefix(prefixReady: true);

        loadout = await Synchronize(Refresh(loadout));

        GameFolders().Should().NotContain("mods");
        var moved = new FileInfo(GameDir.Combine("Mods/modX/content/a.ws").GetFullPath());
        moved.Exists.Should().BeTrue();
        (await GameDir.Combine("Mods/modX/content/a.ws").ReadAllTextAsync()).Should().Be("a");
        (await GameDir.Combine("Mods/modX/content/b.ws").ReadAllTextAsync()).Should().Be("b");
        new FileInfo(GameDir.Combine("Mods/modX/content/b.ws").GetFullPath()).LastWriteTimeUtc.Should().Be(time);
        DiskRowPaths().Where(p => p.StartsWith("mods", StringComparison.OrdinalIgnoreCase)).Should().OnlyContain(p => p.StartsWith("Mods/", StringComparison.Ordinal));
        GamePathRows.TargetPaths(Connection.Db, loadout.Id).Select(r => r.Path).Where(p => p.StartsWith("mods", StringComparison.OrdinalIgnoreCase))
            .Should().OnlyContain(p => p.StartsWith("Mods/", StringComparison.Ordinal));
        DeletedFile.All(Connection.Db).Should().BeEmpty();
        (await CaseVariantCards(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task Synchronize_ProtonGameWithADifferentBytesTwin_LeavesIt_NoDeletedFileRow_CardNamesBoth()
    {
        // Both spellings hold the same relative file name, so the loadout's path comparison sees one file.
        UseProtonPrefix(prefixReady: true);
        var loadout = await CreateLoadout();
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "Mods/modX/content/a.ws"), "from the mod");
        loadout = await Synchronizer.Synchronize(loadout);
        var twin = await WriteOnDisk("mods/modX/content/a.ws", "left behind");

        loadout = await Synchronizer.Synchronize(loadout);

        twin.FileExists.Should().BeTrue();
        (await GameDir.Combine("Mods/modX/content/a.ws").ReadAllTextAsync()).Should().Be("from the mod");
        DeletedFile.All(Connection.Db).Should().BeEmpty("a twin the merge refuses is not a file the user deleted");
        var card = (await CaseVariantCards(loadout)).Should().ContainSingle().Subject;
        DetailsOf(card).Should().Contain("{Game}/mods").And.Contain("{Game}/Mods");
    }

    [Fact]
    public async Task Synchronize_ProtonGameWithASameBytesTwin_DropsIt_NoCard()
    {
        UseProtonPrefix(prefixReady: true);
        var loadout = await CreateLoadout();
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "Mods/modX/content/a.ws"), "same");
        loadout = await Synchronizer.Synchronize(loadout);
        await WriteOnDisk("mods/modX/content/a.ws", "same");

        loadout = await Synchronizer.Synchronize(loadout);

        GameFolders().Should().NotContain("mods");
        GameDir.Combine("Mods/modX/content/a.ws").FileExists.Should().BeTrue();
        (await CaseVariantCards(loadout)).Should().BeEmpty();
    }

    private string[] SwfWinners()
        => Directory.EnumerateFiles(GameDir.Combine("Data").GetFullPath(), "a.swf", SearchOption.AllDirectories).ToArray();

    private async Task<(Loadout.ReadOnly Loadout, LoadoutItemGroupId Group)> AddPrioritisedMod(Loadout.ReadOnly loadout, string name, int priority, string path, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var identity = FileIdentity.XxHash3(bytes.xxHash3());
        if (!await FileStore.HaveFile(identity))
            await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(path, new MemoryStream(bytes)), identity, Size.FromLong(bytes.Length))]);

        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, name);
        _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = group, Priority = ConflictPriority.From((ulong)priority) };
        AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, path), content);
        var result = await tx.Commit();
        return (Refresh(loadout), result[group]);
    }

    [Fact]
    public async Task Synchronize_ProtonGameWithTwoModsOnePathInTwoFolderSpellings_HigherPriorityWins()
    {
        UseProtonPrefix(prefixReady: true);
        var loadout = await CreateLoadout();
        (loadout, _) = await AddPrioritisedMod(loadout, "low", 1, "Data/Interface/a.swf", "low");
        (loadout, _) = await AddPrioritisedMod(loadout, "high", 2, "Data/interface/a.swf", "high");

        loadout = await Synchronize(loadout);

        var winner = SwfWinners().Should().ContainSingle().Subject;
        (await File.ReadAllTextAsync(winner)).Should().Be("high");
    }

    [Fact]
    public async Task Synchronize_ProtonGameWithTwoModsOneFileInTwoSpellingsOfItsName_HigherPriorityWins()
    {
        UseProtonPrefix(prefixReady: true);
        var loadout = await CreateLoadout();
        (loadout, _) = await AddPrioritisedMod(loadout, "low", 1, "Data/Textures/Foo.dds", "low");
        (loadout, _) = await AddPrioritisedMod(loadout, "high", 2, "Data/Textures/foo.dds", "high");

        loadout = await Synchronize(loadout);

        var deployed = Directory.EnumerateFiles(GameDir.Combine("Data/Textures").GetFullPath()).ToArray();
        deployed.Should().ContainSingle();
        (await File.ReadAllTextAsync(deployed[0])).Should().Be("high");
    }

    [Fact]
    public async Task Synchronize_ProtonGameWithAPinnedWinnerInAnotherSpelling_KeepsThePin()
    {
        UseProtonPrefix(prefixReady: true);
        var loadout = await CreateLoadout();
        (loadout, var low) = await AddPrioritisedMod(loadout, "low", 1, "Data/Interface/a.swf", "low");
        (loadout, _) = await AddPrioritisedMod(loadout, "high", 2, "Data/interface/a.swf", "high");
        await LoadoutManager.PinFileWinner(loadout.LoadoutId, new GamePath(LocationId.Game, "Data/interface/a.swf"), low);

        loadout = await Synchronize(loadout);

        var winner = SwfWinners().Should().ContainSingle().Subject;
        (await File.ReadAllTextAsync(winner)).Should().Be("low", "the pin names the path in the spelling the rows were folded to");
        GamePathRows.PinPaths(Connection.Db, loadout.Id).Select(r => r.Path).Should().Equal("Data/Interface/a.swf");
    }

    [Fact]
    public async Task Synchronize_ProtonGameWithADisabledModUnderTheMergedSpelling_DeletesItAtItsRealPath()
    {
        UseProtonPrefix(prefixReady: true);
        var loadout = await CreateLoadout();
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "Mods/keep/a.ws"), "a");
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "mods/gone/b.ws"), "b");
        loadout = await Synchronize(loadout);
        GameDir.Combine("Mods/gone/b.ws").FileExists.Should().BeTrue();

        using (var tx = Connection.BeginTransaction())
        {
            foreach (var group in LoadoutItemGroup.All(Connection.Db).Where(g => g.AsLoadoutItem().Name.StartsWith("mod mods/gone", StringComparison.Ordinal)))
                tx.Add(group.Id, LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }
        loadout = await Synchronize(Refresh(loadout));

        GameDir.Combine("Mods/gone/b.ws").FileExists.Should().BeFalse();
        GameFolders().Should().NotContain("mods");
    }

    [Fact]
    public async Task ResetToOriginalGameState_ProtonGameAfterTheTwinsWereMerged_RemovesTheModFilesAndLeavesNoTwinFolder()
    {
        var loadout = await CreateLoadout();
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "mods/modX/content/a.ws"), "a");
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "Mods/modY/content/b.ws"), "b");
        loadout = await Synchronizer.Synchronize(loadout);
        UseProtonPrefix(prefixReady: true);
        loadout = await Synchronize(Refresh(loadout));
        GameFolders().Should().NotContain("mods");

        await Synchronizer.ResetToOriginalGameState(GameInstallation, GameInstallation.LocatorResult.LocatorIds.ToArray());

        GameFolders().Where(f => f.StartsWith("mods", StringComparison.OrdinalIgnoreCase)).Should().BeEmpty();
    }

    [Fact]
    public async Task BuildProcessRun_ProtonSwitch_FoldsBeforeTheTree()
    {
        UseProtonPrefix(prefixReady: true);
        var loadout = await CreateLoadout();
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "mods/modX/content/a.ws"), "a");
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, "Mods/modY/content/b.ws"), "b");
        var state = GameRegistry.ForceGetMetadata(GameInstallation);

        await ((ALoadoutSynchronizer)Synchronizer).BuildProcessRun(loadout, state, CancellationToken.None);

        GameDir.Combine("Mods/modX/content/a.ws").FileExists.Should().BeTrue();
        GameDir.Combine("Mods/modY/content/b.ws").FileExists.Should().BeTrue();
        GameFolders().Should().NotContain("mods");
    }

    /// <summary>A refused twin: the same relative file in two spellings of a folder, with different bytes.</summary>
    private async Task<Loadout.ReadOnly> ApplyWithRefusedTwin(Loadout.ReadOnly loadout, string folder, string twinFolder)
    {
        loadout = await AddFileMod(loadout, new GamePath(LocationId.Game, folder + "/a.ws"), "from the mod");
        loadout = await Synchronizer.Synchronize(loadout);
        await WriteOnDisk(twinFolder + "/a.ws", "left behind");
        return await Synchronizer.Synchronize(loadout);
    }

    [Fact]
    public async Task HealthCheck_ProtonGameWithARefusedTwin_ShowsTheCaseVariantCard()
    {
        UseProtonPrefix(prefixReady: true);
        var loadout = await CreateLoadout();
        loadout = await ApplyWithRefusedTwin(loadout, "Mods/modX", "mods/modX");

        await DiagnosticManager.RefreshAsync(loadout.LoadoutId).WaitAsync(TimeSpan.FromSeconds(30));
        var cards = await DiagnosticManager.GetLoadoutDiagnostics(loadout.LoadoutId).Timeout(TimeSpan.FromSeconds(30)).FirstAsync();

        cards.Should().Contain(c => c.Id == new DiagnosticId("SilverModManager.DataModel.CaseVariantFolder", number: 1));
    }

    [Fact]
    public async Task Synchronize_ProtonGameWithSeveralRefusedTwins_ListsEveryPairOnOneCard()
    {
        UseProtonPrefix(prefixReady: true);
        var loadout = await CreateLoadout();
        loadout = await ApplyWithRefusedTwin(loadout, "Mods/modX", "mods/modX");
        loadout = await ApplyWithRefusedTwin(loadout, "Data/Textures", "Data/textures");

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

        loadout = await Synchronizer.Synchronize(loadout);

        GameInstallation.Locations[LocationId.Game].Path.Combine("Mods/modY/content/b.ws").FileExists.Should().BeTrue();
        (await CaseVariantCards(loadout)).Should().BeEmpty();
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
        (await FileStore.HaveFile(FileIdentity.XxHash3(Encoding.UTF8.GetBytes("leftover from a crash").xxHash3()))).Should().BeFalse();
        probe.FileExists.Should().BeFalse("the next probe of that folder replaces and removes the leftover");
    }
}
