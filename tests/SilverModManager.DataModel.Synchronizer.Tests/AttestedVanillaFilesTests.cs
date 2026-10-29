using System.Collections.Frozen;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Games.FileHashes;
using SilverModManager.Games.FileHashes.Emitters;
using SilverModManager.MnemonicDB.Storage;
using SilverModManager.MnemonicDB.Storage.RocksDbBackend;
using SilverModManager.Sdk.Settings;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Backend;
using SilverModManager.CrossPlatform;
using SilverModManager.Games.Generic;
using SilverModManager.Games.RedEngine;
using SilverModManager.Games.RedEngine.Cyberpunk2077;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// Cyberpunk 2077 from a Steam library whose app manifest says Steam finished writing the files after they were laid
/// down. The baseline records every vanilla file from the depot manifests without opening it, the index names each one
/// by the manifest's SHA-1, and a file is read only when the synchronizer has to touch its bytes.
/// </summary>
public class AttestedVanillaFilesTests(ITestOutputHelper helper) : ASteamInstalledGameTest<AttestedVanillaFilesTests, Cyberpunk2077Game>(helper)
{
    private const string Vanilla = "bin/x64/Cyberpunk2077.exe";

    private static readonly string[] PlayerFiles = ["archive/pc/mod/appearance.archive", "r6/scripts/tweak.reds", "bin/x64/plugins/cyber_engine_tweaks.log"];

    protected override bool StoreVouchesForTheFiles => true;

    private readonly SettleSwitch _settle = new();
    private readonly SwitchableBackupOverflowPrompt _prompt = new();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services)
            .AddOSInterop()
            .AddRuntimeDependencies()
            .RemoveRuntimeDependencyChecker()
            .AddGenericGameSupport()
            .AddFakeToolRunner()
            .AddRedEngineGames()
            .AddSingleton(new BackupLimit(Size.From(40)))
            .AddSingleton<IBackupOverflowPrompt>(_prompt);

        // The real service, behind a switch that makes the settle step fail.
        var real = services.Last(d => d.ServiceType == typeof(IFileHashesService));
        services.Remove(real);
        services.AddSingleton<IFileHashesService>(sp =>
            new SettleFailingHashes((IFileHashesService)ActivatorUtilities.CreateInstance(sp, real.ImplementationType ?? throw new InvalidOperationException()), _settle));

        // The real resolver, counting the questions put to it outside a scope.
        var resolver = services.Last(d => d.ServiceType == typeof(IFileIdentityResolver));
        services.Remove(resolver);
        return services.AddSingleton<IFileIdentityResolver>(sp =>
            new UnscopedQuestionCounter((IFileIdentityResolver)ActivatorUtilities.CreateInstance(sp, resolver.ImplementationType ?? throw new InvalidOperationException()), _unscopedQuestions));
    }

    private readonly UnscopedQuestions _unscopedQuestions = new();

    private sealed class UnscopedQuestions
    {
        public int Count { get; set; }
    }

    // A question asked of a scoped resolver goes to its memo and is not counted; one asked here has no memo to answer it.
    private sealed class UnscopedQuestionCounter(IFileIdentityResolver inner, UnscopedQuestions questions) : IFileIdentityResolver
    {
        public bool TryConvert(FileIdentity identity, Size size, HashKind kind, out FileIdentity converted)
        {
            questions.Count++;
            return inner.TryConvert(identity, size, kind, out converted);
        }

        public IdentityMatch Resolve(FileIdentity left, FileIdentity right, Size size)
        {
            questions.Count++;
            return inner.Resolve(left, right, size);
        }

        public IEnumerable<FileIdentity> StoreKeyCandidates(FileIdentity identity, Size size) => inner.StoreKeyCandidates(identity, size);

        public IFileIdentityResolver Scoped() => inner.Scoped();
    }

    private sealed class SettleSwitch
    {
        public bool Fail { get; set; }
        public bool FailBaselineBuild { get; set; }
        public int BaselineBuildCalls { get; set; }
    }

    protected override SteamInstall InstallGame(SteamLibraryFixture library)
    {
        var install = CyberpunkSteamInstall.Install(library);
        CyberpunkSteamInstall.AddPlayerFiles(install);
        return install;
    }

    private IFileHashesService Hashes => ServiceProvider.GetRequiredService<IFileHashesService>();

    private EntityId Metadata => GameRegistry.ForceGetMetadata(GameInstallation).Id;

    private IEnumerable<string> FullyHashed => FullHashes.Paths.Select(p => Path.GetRelativePath(GameFolder.ToString(), p.ToString()));

    private static bool IsVanilla(string path)
        => CyberpunkSteamInstall.GameFiles.Concat(CyberpunkSteamInstall.ExpansionFiles).Any(f => f.Path == path);

    private IReadOnlyList<string> Overrides(Loadout.ReadOnly loadout)
        => LoadoutItem.FindByLoadout(Connection.Db, loadout).OfTypeLoadoutItemGroup().OfTypeLoadoutOverridesGroup()
            .SelectMany(g => g.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath())
            .Select(i => ((GamePath)i.TargetPath).Path.ToString()).ToList();

    private DiskStateEntry.ReadOnly DiskEntry(string path)
        => DiskStateEntry.FindByGame(Connection.Db, Metadata).Single(e => ((GamePath)e.Path).Path.ToString() == path);

    private async Task AddMod(Loadout.ReadOnly loadout, params string[] paths)
    {
        using var tx = Connection.BeginTransaction();
        await AddModAsync(tx, paths.Select(p => (RelativePath)p), loadout.LoadoutId, "Mod over vanilla");
        await tx.Commit();
    }

    [Fact]
    public async Task ManagingTheGame_ReadsNoVanillaFile()
    {
        var loadout = await CreateLoadout();

        FullHashes.BaselineOpens.Should().BeEmpty("the store vouches for every vanilla file");
        FullyHashed.Should().BeEquivalentTo(PlayerFiles);
        var vanilla = DiskStateEntry.FindByGame(Connection.Db, Metadata).Where(e => IsVanilla(((GamePath)e.Path).Path.ToString())).ToList();
        vanilla.Should().NotBeEmpty().And.OnlyContain(e => e.IdentityOf().Kind == HashKind.Sha1);
        Overrides(loadout).Should().BeEquivalentTo(PlayerFiles, "only the player's own files are external changes");
    }

    [Fact]
    public async Task ASecondSyncAfterManaging_DoesNothingAndReadsNothing()
    {
        var loadout = await CreateLoadout();
        FullHashes.Reset();

        await Synchronizer.Synchronize(Refresh(loadout));

        FullHashes.Paths.Should().BeEmpty();
        FullHashes.BaselineOpens.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyingAModOverAVanillaFile_ReadsThatFileOnceBacksItUpAndPromotesIt()
    {
        var loadout = await CreateLoadout();
        FullHashes.Reset();
        await AddMod(loadout, Vanilla);

        await Synchronizer.Synchronize(Refresh(loadout));

        FullyHashed.Should().ContainSingle().Which.Should().Be(Vanilla);
        var vanillaBytes = System.Text.Encoding.UTF8.GetBytes("game executable");
        (await FileStore.HaveFile(vanillaBytes.xxHash3())).Should().BeTrue("the vanilla bytes were archived before the mod replaced them");
        var baseline = LocalBaseline.All(Hashes.Local).Single();
        AttestedFile.FindByBaseline(Hashes.Local, baseline.Id).Select(r => r.Path.ToString()).Should().NotContain(Vanilla);
        baseline.Files.Select(f => f.Path.ToString()).Should().Contain(Vanilla, "the read promoted the row");
        DiskEntry(Vanilla).IdentityOf().Should().Be(FileIdentity.XxHash3(Vanilla.xxHash3AsUtf8()));

        FullHashes.Reset();
        await Synchronizer.Synchronize(Refresh(loadout));
        FullHashes.Paths.Should().BeEmpty();
    }

    private async Task RemoveMod(Loadout.ReadOnly loadout, string name = "Mod over vanilla")
    {
        using var tx = Connection.BeginTransaction();
        foreach (var mod in LoadoutItem.FindByLoadout(Connection.Db, loadout).Where(item => item.Name == name))
            tx.Delete(mod.Id, recursive: true);
        await tx.Commit();
    }

    private static readonly GamePath GameRoot = new(LocationId.Game, RelativePath.Empty);

    private string VanillaText => "game executable";

    private async Task<string> ReadGameFile(string path) => await File.ReadAllTextAsync((GameFolder / path).ToString());

    [Fact]
    public async Task RemovingTheMod_RestoresTheVanillaFileAndTheNextSyncReadsNothing()
    {
        var loadout = await CreateLoadout();
        await AddMod(loadout, Vanilla);
        await Synchronizer.Synchronize(Refresh(loadout));
        (await ReadGameFile(Vanilla)).Should().Be(Vanilla, "the mod's file replaced the vanilla one");
        await RemoveMod(loadout);
        FullHashes.Reset();

        await Synchronizer.Synchronize(Refresh(loadout));

        (await ReadGameFile(Vanilla)).Should().Be(VanillaText);
        FullHashes.Paths.Should().BeEmpty();

        await Synchronizer.Synchronize(Refresh(loadout));
        FullHashes.Paths.Should().BeEmpty();
        (await ReadGameFile(Vanilla)).Should().Be(VanillaText);
    }

    [Fact]
    public async Task ApplyingAModOverASameSizeSameTimeReplacement_BacksTheRealBytesUpFirstAndDeploysOnTheNextSync()
    {
        // The replacement was there before the app first indexed the path, with the vanilla file's size and time, so the
        // index took it for the store's copy. The read the apply makes is the first to see the real bytes.
        var file = GameFolder / Vanilla;
        var stamp = File.GetLastWriteTimeUtc(file.ToString());
        var replacement = "game_executable"; // same length as "game executable"
        await File.WriteAllTextAsync(file.ToString(), replacement);
        File.SetLastWriteTimeUtc(file.ToString(), stamp);
        var loadout = await CreateLoadout();
        DiskEntry(Vanilla).IdentityOf().Kind.Should().Be(HashKind.Sha1, "nothing opened the file");
        await AddMod(loadout, Vanilla);

        await Synchronizer.Synchronize(Refresh(loadout));

        (await FileStore.HaveFile(Encoding.UTF8.GetBytes(replacement).xxHash3())).Should().BeTrue("the real bytes are archived before anything overwrites them");
        AttestedFile.FindByBaseline(Hashes.Local, LocalBaseline.All(Hashes.Local).Single().Id).Select(r => r.Path.ToString())
            .Should().Contain(Vanilla, "the store's description of the path stands");
        Overrides(loadout).Should().NotContain(Vanilla, "the path is a game file, so the ingested override is dropped");

        await Synchronizer.Synchronize(Refresh(loadout));
        (await ReadGameFile(Vanilla)).Should().Be(Vanilla, "the mod deploys on the following sync");
    }

    [Fact]
    public async Task ResettingAfterAModOverrode_RestoresTheVanillaFileFromItsBackup()
    {
        var loadout = await CreateLoadout();
        await AddMod(loadout, Vanilla);
        await Synchronizer.Synchronize(Refresh(loadout));
        FullHashes.Reset();

        await Synchronizer.ResetToOriginalGameState(GameInstallation, Refresh(loadout).LocatorIds.ToArray());

        (await ReadGameFile(Vanilla)).Should().Be(VanillaText);
        FullHashes.Paths.Should().BeEmpty();
    }

    [Fact]
    public async Task UnmanagingWithAClean_DeletesNoVanillaFileAndReadsNone()
    {
        await CreateLoadout();
        FullHashes.Reset();

        await LoadoutManager.UnManage(GameInstallation);

        foreach (var file in CyberpunkSteamInstall.GameFiles) (GameFolder / file.Path).FileExists.Should().BeTrue();
        foreach (var file in PlayerFiles) (GameFolder / file).FileExists.Should().BeFalse("a clean removes what the player added");
        FullHashes.Paths.Should().BeEmpty();
        FullHashes.BaselineOpens.Should().BeEmpty();
    }

    [Fact]
    public async Task AVanillaFileEditedOutsideTheApp_IsBackedUpAndThenWarnedAboutLikeToday()
    {
        var loadout = await CreateLoadout();
        var file = GameFolder / Vanilla;
        await File.WriteAllTextAsync(file.ToString(), "edited by hand");
        File.SetLastWriteTimeUtc(file.ToString(), DateTime.UtcNow.AddMinutes(5));

        await Synchronizer.Synchronize(Refresh(loadout));

        (await ReadGameFile(Vanilla)).Should().Be("edited by hand", "an edit is never deleted");
        (await FileStore.HaveFile(Encoding.UTF8.GetBytes("edited by hand").xxHash3())).Should().BeTrue();
        GameBackedUpFile.All(Connection.Db).Select(f => f.Hash).Should().Contain(Encoding.UTF8.GetBytes("edited by hand").xxHash3());
        Overrides(loadout).Should().NotContain(Vanilla);
        AttestedFile.FindByBaseline(Hashes.Local, LocalBaseline.All(Hashes.Local).Single().Id).Select(r => r.Path.ToString()).Should().Contain(Vanilla);

        FullHashes.Reset();
        await Synchronizer.Synchronize(Refresh(loadout));
        FullHashes.Paths.Should().BeEmpty();
        (await ReadGameFile(Vanilla)).Should().Be("edited by hand");
    }

    [Fact]
    public async Task ResetWithAnEditedVanillaFile_WarnsInsteadOfDeleting()
    {
        var loadout = await CreateLoadout();
        var file = GameFolder / Vanilla;
        await File.WriteAllTextAsync(file.ToString(), "edited by hand");
        File.SetLastWriteTimeUtc(file.ToString(), DateTime.UtcNow.AddMinutes(5));
        await Synchronizer.Synchronize(Refresh(loadout));

        await Synchronizer.ResetToOriginalGameState(GameInstallation, Refresh(loadout).LocatorIds.ToArray());

        file.FileExists.Should().BeTrue("the app cannot put the vanilla bytes back, and does not delete the player's edit");
    }

    [Fact]
    public async Task ResettingAfterARebuildAttestedAPathBackedUpByXxHash3_RestoresItThroughTheRelation()
    {
        var loadout = await CreateLoadout();
        await AddMod(loadout, Vanilla);
        await Synchronizer.Synchronize(Refresh(loadout));

        // A rebuild that found no relation for the path would attest it again; the backup the apply made is keyed by
        // xxHash3 and only the relation recorded when it was made says that is the store's SHA-1.
        var local = Hashes.Local.Connection;
        var baseline = LocalBaseline.All(Hashes.Local).Single();
        var path = baseline.Files.Single(f => f.Path.ToString() == Vanilla);
        var manifest = baseline.Manifests.Single(m => m.Files.Any(f => f.Id == path.Id));
        var relation = path.Hash;
        using (var tx = local.BeginTransaction())
        {
            tx.Retract(baseline.Id, LocalBaseline.Files, path.Id);
            tx.Retract(manifest.Id, SteamManifest.Files, path.Id);
            _ = new AttestedFile.New(tx)
            {
                BaselineId = baseline.Id,
                ManifestId = manifest.Id,
                Path = path.Path,
                Sha1 = relation.Sha1,
                Size = relation.Size,
                ModifiedUtc = DateTimeOffset.UtcNow,
            };
            await tx.Commit();
        }

        await Synchronizer.ResetToOriginalGameState(GameInstallation, Refresh(loadout).LocatorIds.ToArray());

        (await ReadGameFile(Vanilla)).Should().Be(VanillaText);
    }

    [Fact]
    public async Task ResettingWhenTwoRelationsShareTheSha1_RestoresFromTheOneWhoseXxHash3TheStoreHolds()
    {
        var loadout = await CreateLoadout();
        await AddMod(loadout, Vanilla);
        await Synchronizer.Synchronize(Refresh(loadout));

        var local = Hashes.Local.Connection;
        var baseline = LocalBaseline.All(Hashes.Local).Single();
        var path = baseline.Files.Single(f => f.Path.ToString() == Vanilla);
        var manifest = baseline.Manifests.Single(m => m.Files.Any(f => f.Id == path.Id));
        var relation = path.Hash;
        using (var tx = local.BeginTransaction())
        {
            tx.Retract(baseline.Id, LocalBaseline.Files, path.Id);
            tx.Retract(manifest.Id, SteamManifest.Files, path.Id);
            _ = new AttestedFile.New(tx)
            {
                BaselineId = baseline.Id,
                ManifestId = manifest.Id,
                Path = path.Path,
                Sha1 = relation.Sha1,
                Size = relation.Size,
                ModifiedUtc = DateTimeOffset.UtcNow,
            };

            // Rows sharing a SHA-1 come back in entity order. The real relation (the one whose bytes the store
            // archived) is recreated after a relation with the same SHA-1 and size whose xxHash3 was never
            // archived, so the unarchived row is the first candidate and the archived one the second. A rule that
            // takes the first size-matching row restores from the missing key and fails.
            tx.Delete(relation.Id, recursive: false);
            _ = new HashRelation.New(tx)
            {
                XxHash3 = Hash.From(1),
                Sha1 = relation.Sha1,
                Size = relation.Size,
                Md5 = relation.Md5,
                Crc32 = relation.Crc32,
                XxHash64 = Hash.From(2),
                MinimalHash = Hash.From(3),
            };
            _ = new HashRelation.New(tx)
            {
                XxHash3 = relation.XxHash3,
                Sha1 = relation.Sha1,
                Size = relation.Size,
                Md5 = relation.Md5,
                Crc32 = relation.Crc32,
                XxHash64 = relation.XxHash64,
                MinimalHash = relation.MinimalHash,
            };
            await tx.Commit();
        }

        HashRelation.FindBySha1(local.Db, relation.Sha1).Select(r => r.XxHash3).First()
            .Should().Be(Hash.From(1), "the unarchived row is the first candidate for this SHA-1");

        await Synchronizer.ResetToOriginalGameState(GameInstallation, Refresh(loadout).LocatorIds.ToArray());

        (await ReadGameFile(Vanilla)).Should().Be(VanillaText);
    }

    [Theory]
    [InlineData("hardlink")]
    [InlineData("symlink")]
    public async Task ALinkApplyOverAnAttestedPath_ThenReset_NeverTakesTheSha1PreviousForALink(string backend)
    {
        var loadout = await CreateLoadout();
        await SynchronizerService.ChangeDeploymentBackend(GameInstallation, GameRoot, new DeploymentBackendId(backend), chosen: true);
        await AddMod(loadout, Vanilla);

        await Synchronizer.Synchronize(Refresh(loadout));

        (await ReadGameFile(Vanilla)).Should().Be(Vanilla, "the mod's file is linked over the vanilla one");
        (await FileStore.HaveFile(Encoding.UTF8.GetBytes(VanillaText).xxHash3())).Should().BeTrue("the vanilla bytes were archived first");

        await Synchronizer.ResetToOriginalGameState(GameInstallation, Refresh(loadout).LocatorIds.ToArray());

        (await ReadGameFile(Vanilla)).Should().Be(VanillaText, "the reset restores the vanilla file and deletes the link");
        foreach (var file in CyberpunkSteamInstall.GameFiles) (GameFolder / file.Path).FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task AdoptingInPlace_StillGivesAFileNamedByItsSha1ItsXxHash3BeforeIngest()
    {
        // A file the loadout does not know, named by the store's SHA-1: the adopt answer strips its backup, and the
        // ingest that is left still needs the xxHash3 the override is keyed by.
        var loadout = await CreateLoadout();
        var path = new GamePath(LocationId.Game, "unknown.dat");
        var onDisk = GameFolder / "unknown.dat";
        await File.WriteAllTextAsync(onDisk.ToString(), "bytes nobody knew");
        var hashes = await MultiHasher.HashStream(new MemoryStream(Encoding.UTF8.GetBytes("bytes nobody knew")));
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [path] = new SyncNode
            {
                Disk = new SyncNodePart { Identity = FileIdentity.Sha1(hashes.Sha1), Size = hashes.Size, LastModifiedTicks = 1 },
                Actions = Actions.BackupFile | Actions.IngestFromDisk,
            },
        };
        using var tx = Connection.BeginTransaction();

        await Synchronizer.ResolveDiskIdentitiesForWrites(tree, GameInstallation, tx, BackupOverflowChoice.AdoptInPlace);

        tree[path].Disk.Identity.Should().Be(FileIdentity.XxHash3(hashes.XxHash3));
        tree[path].Actions.Should().Be(Actions.IngestFromDisk, "the adopt answer still holds after the actions were worked out again");
        FullyHashed.Should().Contain("unknown.dat");
    }

    [Theory]
    [InlineData(true, "the last sync saw these bytes under their SHA-1, so it follows them to the xxHash3")]
    [InlineData(false, "the last sync saw other bytes under another kind, which nothing relates to these, so it stays as it was")]
    public async Task ReadingASha1File_RewritesThePreviousOnlyWhenItWasTheSameBytes(bool previousIsTheSameBytes, string because)
    {
        var loadout = await CreateLoadout();
        var path = new GamePath(LocationId.Game, "unknown.dat");
        await File.WriteAllTextAsync((GameFolder / "unknown.dat").ToString(), "bytes nobody knew");
        var hashes = await MultiHasher.HashStream(new MemoryStream(Encoding.UTF8.GetBytes("bytes nobody knew")));
        var disk = FileIdentity.Sha1(hashes.Sha1);
        var previous = previousIsTheSameBytes ? disk : FileIdentity.XxHash3(Hash.From(0xBADBEEF));
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [path] = new SyncNode
            {
                Disk = new SyncNodePart { Identity = disk, Size = hashes.Size, LastModifiedTicks = 1 },
                Previous = new SyncNodePart { Identity = previous, Size = hashes.Size, LastModifiedTicks = 1 },
                Actions = Actions.BackupFile | Actions.IngestFromDisk,
            },
        };
        using var tx = Connection.BeginTransaction();

        await Synchronizer.ResolveDiskIdentitiesForWrites(tree, GameInstallation, tx);

        tree[path].Disk.Identity.Should().Be(FileIdentity.XxHash3(hashes.XxHash3));
        tree[path].Previous.Identity.Should().Be(previousIsTheSameBytes ? FileIdentity.XxHash3(hashes.XxHash3) : previous, because);
    }

    [Fact]
    public async Task GameLayerIdentityFromSql_MatchesTheServiceForAnAttestedRow()
    {
        var loadout = await CreateLoadout();

        var rows = ((ALoadoutSynchronizer)Synchronizer).WinningFilesForTest(loadout).Where(r => r.ItemType == "Game").ToDictionary(r => r.Path.Path.ToString());
        var fromService = Hashes.GetGameFiles(GameInstallation).Where(f => f.Identity.Kind == HashKind.Sha1).ToList();

        fromService.Should().NotBeEmpty();
        foreach (var file in fromService)
            FileIdentity.ParseHex(rows[file.Path.Path.ToString()].Identity).Should().Be(file.Identity, "hex() over the stored SHA-1 gives the digits the service builds");
    }

    [Fact]
    public async Task ASettleFailureBeforeTheBackends_AbortsTheApplyWithNothingWritten()
    {
        var loadout = await CreateLoadout();
        await AddMod(loadout, Vanilla);
        _settle.Fail = true;

        var apply = async () => await Synchronizer.Synchronize(Refresh(loadout));

        await apply.Should().ThrowAsync<InvalidOperationException>().WithMessage("*settle*");
        (await ReadGameFile(Vanilla)).Should().Be(VanillaText, "nothing was deleted or extracted");
        (await FileStore.HaveFile(Encoding.UTF8.GetBytes(VanillaText).xxHash3())).Should().BeFalse("nothing was backed up");
        DiskEntry(Vanilla).IdentityOf().Kind.Should().Be(HashKind.Sha1, "the disk state still names the file by the store's SHA-1");
        AttestedFile.FindByBaseline(Hashes.Local, LocalBaseline.All(Hashes.Local).Single().Id).Select(r => r.Path.ToString()).Should().Contain(Vanilla);

        _settle.Fail = false;
        await Synchronizer.Synchronize(Refresh(loadout));
        (await ReadGameFile(Vanilla)).Should().Be(Vanilla, "the apply works once the settle does");
    }

    [Fact]
    public async Task AVanillaFileTouchedWithItsBytesUnchanged_IsReadOnceAndTheNextSyncReadsNothing()
    {
        var loadout = await CreateLoadout();
        var file = (GameFolder / Vanilla).ToString();
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(30));
        FullHashes.Reset();

        await Synchronizer.Synchronize(Refresh(loadout));

        FullyHashed.Should().ContainSingle().Which.Should().Be(Vanilla);
        Overrides(loadout).Should().BeEquivalentTo(PlayerFiles, "the same bytes are not an external change");
        DiskEntry(Vanilla).IdentityOf().Kind.Should().Be(HashKind.XxHash3);
        LocalBaseline.All(Hashes.Local).Single().Files.Select(f => f.Path.ToString()).Should().Contain(Vanilla, "the read promoted the row");

        FullHashes.Reset();
        await Synchronizer.Synchronize(Refresh(loadout));
        FullHashes.Paths.Should().BeEmpty();
    }

    // A full read named these files by xxHash3 while the loadout names the same files by the store's SHA-1. The
    // recorded digests say they are the same bytes, so the sync has nothing to do and nothing to warn about.
    [Fact]
    public async Task VanillaFilesTheIndexNamedByXxHash3WhileTheLoadoutNamesThemBySha1_AreTheSameFile()
    {
        var loadout = await CreateLoadout();
        await TheIndexReadTheVanillaFilesInFull();
        var prompt = new RecordingPrompt(BackupOverflowChoice.AdoptInPlace);
        _prompt.Inner = prompt;

        // The state the player is left with: the disk names every vanilla file by xxHash3, the loadout by SHA-1.
        DiskEntry(Vanilla).IdentityOf().Kind.Should().Be(HashKind.XxHash3);
        ((ALoadoutSynchronizer)Synchronizer).WinningFilesForTest(Refresh(loadout)).Where(r => r.ItemType == "Game")
            .Should().NotBeEmpty().And.OnlyContain(r => FileIdentity.ParseHex(r.Identity).Kind == HashKind.Sha1);

        await Synchronizer.Synchronize(Refresh(loadout));
        prompt.Asked.Should().BeEmpty("the same bytes need no backup, so the cap is never reached");
        FullHashes.Reset();

        // The app is restarted: the index finds every file unchanged by size and time and keeps the disk state's names.
        var tree = await Synchronizer.BuildSyncTree(Refresh(loadout));
        Synchronizer.ProcessSyncTree(tree);
        await Synchronizer.Synchronize(Refresh(loadout));

        tree.Where(kv => kv.Value.Actions.HasFlag(Actions.WarnOfUnableToExtract)).Select(kv => kv.Key.Path.ToString()).Should().BeEmpty();
        Overrides(Refresh(loadout)).Should().BeEquivalentTo(PlayerFiles, "the game's own bytes are not the player's changes");
        FullyHashed.Should().BeEmpty();
        await EveryVanillaFileIsOnDiskAndNoneWasBackedUp();
    }

    // The disk and the previous state name the vanilla file by xxHash3 of these bytes; the loadout names it by the
    // store's SHA-1. Only a recorded relation of the same SHA-1 and size may call the two the same file.
    private async Task<SyncNode> VanillaNodeNamedOnDiskBy(MultiHash diskBytes, bool recordRelation, bool anotherRelationPrecedesIt = false)
        => (await VanillaTreeNamedOnDiskBy(diskBytes, recordRelation, anotherRelationPrecedesIt)).Tree[new GamePath(LocationId.Game, Vanilla)];

    // With removeFromDisk the file is gone from the folder, so only the previous state and the loadout name it.
    private async Task<(Loadout.ReadOnly Loadout, Dictionary<GamePath, SyncNode> Tree)> VanillaTreeNamedOnDiskBy(MultiHash diskBytes, bool recordRelation, bool anotherRelationPrecedesIt = false, bool removeFromDisk = false)
    {
        var loadout = await CreateLoadout();
        var path = new GamePath(LocationId.Game, Vanilla);
        if (anotherRelationPrecedesIt)
        {
            // Same SHA-1 and size, recorded before the disk's own relation, so it is the first row of that SHA-1.
            using var tx = Hashes.Local.Connection.BeginTransaction();
            _ = new HashRelation.New(tx)
            {
                XxHash3 = Hash.From(1),
                Sha1 = diskBytes.Sha1,
                Size = diskBytes.Size,
                Md5 = diskBytes.Md5,
                Crc32 = diskBytes.Crc32,
                XxHash64 = Hash.From(2),
                MinimalHash = Hash.From(3),
            };
            await tx.Commit();
        }

        if (recordRelation)
            await Hashes.RecordIndexedFiles(GameInstallation, [new IndexedFileHash(path, diskBytes.Size, DateTimeOffset.UtcNow, diskBytes.XxHash3, diskBytes)]);

        var synchronizer = (ALoadoutSynchronizer)Synchronizer;
        var metadata = GameInstallMetadata.Load(Connection.Db, Metadata);
        PathPartPair Named(PathPartPair pair) => pair.Path != path ? pair : pair with { Part = new SyncNodePart { Identity = FileIdentity.XxHash3(diskBytes.XxHash3), Size = diskBytes.Size, LastModifiedTicks = pair.Part.LastModifiedTicks } };
        var disk = synchronizer.GetDiskStateForGame(metadata).Where(pair => !removeFromDisk || pair.Path != path).Select(Named).ToList();
        var previous = ((ILoadoutSynchronizer)synchronizer).GetPreviouslyAppliedDiskState(metadata).Select(Named).ToList();

        var tree = synchronizer.BuildSyncTree(disk, previous, Refresh(loadout));
        Synchronizer.ProcessSyncTree(tree);
        var node = tree[path];
        if (!removeFromDisk) node.Disk.Identity.Kind.Should().Be(HashKind.XxHash3);
        if (anotherRelationPrecedesIt)
            node.Loadout.Identity.Should().Be(node.Disk.Identity, "the relation joins the two, so the tree names the file by one kind");
        else
            node.Loadout.Identity.Kind.Should().Be(HashKind.Sha1, "the loadout names the file the way the store does");
        return (loadout, tree);
    }

    private static async Task<MultiHash> HashOf(string text) => await MultiHasher.HashStream(new MemoryStream(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public async Task AnXxHash3WhoseRelationIsTheSecondToShareTheSha1_IsTheStoresFile()
    {
        var vanilla = await HashOf(VanillaText);

        var node = await VanillaNodeNamedOnDiskBy(vanilla, recordRelation: true, anotherRelationPrecedesIt: true);

        node.Actions.Should().NotHaveFlag(Actions.WarnOfUnableToExtract, "a relation of this SHA-1 and size lists the xxHash3 on disk");
    }

    [Fact]
    public async Task AnXxHash3WhoseRelationHasAnotherSha1_IsNotTheStoresFile()
    {
        var edited = await HashOf(VanillaText.ToUpperInvariant()); // same length, so only the SHA-1 tells it apart

        var node = await VanillaNodeNamedOnDiskBy(edited, recordRelation: true);

        node.Actions.Should().HaveFlag(Actions.WarnOfUnableToExtract, "the bytes on disk are not the ones the store lists, and nothing archives the listed ones");
    }

    [Fact]
    public async Task AnXxHash3WhoseRelationHasTheSha1ButAnotherSize_IsNotTheStoresFile()
    {
        var vanilla = await HashOf(VanillaText);

        var node = await VanillaNodeNamedOnDiskBy(vanilla with { Size = vanilla.Size + Size.From(1) }, recordRelation: true);

        node.Actions.Should().HaveFlag(Actions.WarnOfUnableToExtract);
    }

    [Fact]
    public async Task AnXxHash3WithNoRelation_IsNotCalledTheStoresFile()
    {
        var vanilla = await HashOf(VanillaText);

        var node = await VanillaNodeNamedOnDiskBy(vanilla, recordRelation: false);

        node.Actions.Should().HaveFlag(Actions.WarnOfUnableToExtract, "nothing says the bytes behind the xxHash3 are the ones behind the SHA-1");
    }

    // A disk-only file whose path and size the store lists is told apart from a player's file by its hash. When the
    // list names it by SHA-1 and the disk by xxHash3, the recorded relation of the xxHash3 is what compares them.
    private async Task<BackupOverflowKind> HowTheDialogCountsAnUnbackedVanillaFileNamedByXxHash3(MultiHash diskBytes, bool recordRelation)
    {
        await CreateLoadout();
        var path = new GamePath(LocationId.Game, Vanilla);
        if (recordRelation)
            await Hashes.RecordIndexedFiles(GameInstallation, [new IndexedFileHash(path, diskBytes.Size, DateTimeOffset.UtcNow, diskBytes.XxHash3, diskBytes)]);

        var file = new ALoadoutSynchronizer.MeasuredFile(path, diskBytes.Size.Value, BackupOverflowKind.Appeared, FileIdentity.XxHash3(diskBytes.XxHash3));
        return ((ALoadoutSynchronizer)Synchronizer).TellGameFilesApart([file], GameInstallation).Single().Kind;
    }

    [Fact]
    public async Task AnXxHash3OfTheStoresBytes_IsCountedAsTheGamesOwnFile()
        => (await HowTheDialogCountsAnUnbackedVanillaFileNamedByXxHash3(await HashOf(VanillaText), recordRelation: true)).Should().Be(BackupOverflowKind.GameFileChanged);

    [Fact]
    public async Task AnXxHash3OfOtherBytesOfTheListedSize_IsStillAFileThatAppeared()
        => (await HowTheDialogCountsAnUnbackedVanillaFileNamedByXxHash3(await HashOf(VanillaText.ToUpperInvariant()), recordRelation: true)).Should().Be(BackupOverflowKind.Appeared);

    [Fact]
    public async Task AnXxHash3NoRelationTellsApart_IsCountedByPathAndSizeAlone()
        => (await HowTheDialogCountsAnUnbackedVanillaFileNamedByXxHash3(await HashOf(VanillaText.ToUpperInvariant()), recordRelation: false)).Should().Be(BackupOverflowKind.GameFileChanged);

    // The state a full read of every vanilla file leaves: the disk state names each by xxHash3, and the digests of the
    // bytes are recorded as a fingerprint row and a relation.
    private async Task TheIndexReadTheVanillaFilesInFull()
    {
        var remembered = new List<IndexedFileHash>();
        using var tx = Connection.BeginTransaction();
        foreach (var file in CyberpunkSteamInstall.GameFiles.Concat(CyberpunkSteamInstall.ExpansionFiles))
        {
            var hashes = await MultiHasher.HashStream(new MemoryStream(file.Content));
            var entry = DiskEntry(file.Path);
            tx.Add(entry.Id, DiskStateEntry.Identity, FileIdentity.XxHash3(hashes.XxHash3));
            tx.Add(entry.Id, DiskStateEntry.XxHash3, hashes.XxHash3);
            var path = new GamePath(LocationId.Game, file.Path);
            var fingerprint = SilverModManager.Sdk.IO.FileFingerprint.Of(GameFolder / file.Path);
            remembered.Add(new IndexedFileHash(path, fingerprint.Size, fingerprint.LastModified, hashes.XxHash3, hashes));
        }

        await tx.Commit();
        await Hashes.RecordIndexedFiles(GameInstallation, remembered);
    }

    // The index remembers the file's xxHash3 under its new modification time, but no digest joins that xxHash3 to the
    // SHA-1 the store gave the loadout and the last sync: the disk reads as xxHash3, the other parts as SHA-1.
    private async Task TheIndexRememberedTheVanillaFilesXxHash3WithNoRelation()
    {
        var hashes = await HashOf(VanillaText);
        HashRelation.FindBySha1(Hashes.Local, hashes.Sha1).Should().BeEmpty("the fixture starts with no relation for these bytes");
        var file = GameFolder / Vanilla;
        File.SetLastWriteTimeUtc(file.ToString(), DateTime.UtcNow.AddMinutes(30));
        var fingerprint = SilverModManager.Sdk.IO.FileFingerprint.Of(file);
        await Hashes.RecordIndexedFiles(GameInstallation, [new IndexedFileHash(new GamePath(LocationId.Game, Vanilla), fingerprint.Size, fingerprint.LastModified, hashes.XxHash3)]);
    }

    // Thirty bytes the player added: with the vanilla file's fifteen counted they pass the forty-byte cap, without
    // them they do not, so the cap is crossed only when the unresolved file is wrongly counted.
    private async Task AddAnUnmanagedFileThatAloneFitsTheCap()
        => await File.WriteAllTextAsync((GameFolder / "unmanaged.bin").ToString(), new string('x', 30));

    private async Task<SyncNode> VanillaNodeNow(Loadout.ReadOnly loadout)
    {
        var tree = await Synchronizer.BuildSyncTree(Refresh(loadout));
        Synchronizer.ProcessSyncTree(tree);
        return tree[new GamePath(LocationId.Game, Vanilla)];
    }

    [Fact]
    public async Task AVanillaFileWhoseNamesNoRelationJoins_IsReadOnceBeforeTheBackupQuestionAndNotCounted()
    {
        var loadout = await CreateLoadout();
        await TheIndexRememberedTheVanillaFilesXxHash3WithNoRelation();
        await AddAnUnmanagedFileThatAloneFitsTheCap();
        var prompt = new RecordingPrompt(BackupOverflowChoice.AdoptInPlace);
        _prompt.Inner = prompt;
        (await VanillaNodeNow(loadout)).IdentityUnresolved.Should().BeTrue("the two kinds have no recorded digest between them");
        FullHashes.Reset();

        await Synchronizer.Synchronize(Refresh(loadout));

        FullyHashed.Where(p => p == Vanilla).Should().ContainSingle("the file is read once, before anything counts it");
        prompt.Asked.Should().BeEmpty("a file nobody has shown to differ is not counted against the backup cap");
        var node = await VanillaNodeNow(loadout);
        node.Actions.Should().Be(Actions.DoNothing);
        node.IdentityUnresolved.Should().BeFalse();
        var hashes = await HashOf(VanillaText);
        HashRelation.FindBySha1(Hashes.Local, hashes.Sha1).Should().NotBeEmpty("the read recorded the digests of the bytes");

        FullHashes.Reset();
        await Synchronizer.Synchronize(Refresh(loadout));
        FullyHashed.Should().NotContain(Vanilla);
    }

    // ProcessSyncTree runs on every refresh. A node whose names no relation joins is asked about again for each of its
    // three pairs, so the answers have to come from one memo and not from the recorded digests each time.
    [Fact]
    public async Task ProcessingAnUnresolvedNode_AsksTheRecordedDigestsThroughOneScope()
    {
        var (_, tree) = await VanillaTreeNamedOnDiskBy(await HashOf(VanillaText), recordRelation: false);
        tree[new GamePath(LocationId.Game, Vanilla)].IdentityUnresolved.Should().BeTrue();
        _unscopedQuestions.Count = 0;

        Synchronizer.ProcessSyncTree(tree);

        _unscopedQuestions.Count.Should().Be(0, "every pair of the node is put to the same memo");
    }

    // The emitter that reports unusable game files, handed the tree DiagnosticManager would give it.
    private async Task<Diagnostic[]> WhatTheMissingGameFilesEmitterSays(Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> tree)
    {
        var emitter = new UndeployableLoadoutDueToMissingGameFiles(ServiceProvider);
        var found = new List<Diagnostic>();
        await foreach (var diagnostic in emitter.Diagnose(Refresh(loadout), tree.ToFrozenDictionary(), CancellationToken.None))
            found.Add(diagnostic);
        return found.ToArray();
    }

    // The disk and the previous state name the file by xxHash3, the loadout by SHA-1, and no relation joins them: the
    // node warns of an extraction it cannot do, yet it only looks different.
    [Fact]
    public async Task AnUnresolvedVanillaFile_GetsNoMissingGameFilesCard()
    {
        var (loadout, tree) = await VanillaTreeNamedOnDiskBy(await HashOf(VanillaText), recordRelation: false);
        var node = tree[new GamePath(LocationId.Game, Vanilla)];
        node.Actions.Should().HaveFlag(Actions.WarnOfUnableToExtract, "this is the node the emitter has to decide about");
        node.IdentityUnresolved.Should().BeTrue("the two kinds have no recorded digest between them");

        (await WhatTheMissingGameFilesEmitterSays(loadout, tree)).Should().BeEmpty("the next apply reads the file and settles whether it differs");
    }

    // Gone from the folder, the file has no bytes to read, so being unresolved against the loadout changes nothing.
    [Fact]
    public async Task ADeletedVanillaFile_IsStillReportedMissing()
    {
        var (loadout, tree) = await VanillaTreeNamedOnDiskBy(await HashOf(VanillaText), recordRelation: false, removeFromDisk: true);
        var node = tree[new GamePath(LocationId.Game, Vanilla)];
        node.HaveDisk.Should().BeFalse();
        node.IdentityUnresolved.Should().BeTrue("the previous state and the loadout name it by kinds no relation joins");
        node.Actions.Should().HaveFlag(Actions.WarnOfUnableToExtract);

        var diagnostic = (await WhatTheMissingGameFilesEmitterSays(loadout, tree)).Should().ContainSingle().Subject;

        diagnostic.Should().BeOfType<Diagnostic<SilverModManager.Games.FileHashes.Diagnostics.UndeployableLoadoutDueToMissingGameFilesMessageData>>()
            .Which.MessageData.Files.Should().Contain(Vanilla);
    }

    [Fact]
    public async Task AResetWithAnUnresolvedVanillaFile_ReadsItOnceAndAsksNothing()
    {
        var loadout = await CreateLoadout();
        await TheIndexRememberedTheVanillaFilesXxHash3WithNoRelation();
        await AddAnUnmanagedFileThatAloneFitsTheCap();
        var prompt = new RecordingPrompt(BackupOverflowChoice.AdoptInPlace);
        _prompt.Inner = prompt;
        FullHashes.Reset();

        await Synchronizer.ResetToOriginalGameState(GameInstallation, Refresh(loadout).LocatorIds.ToArray());

        FullyHashed.Where(p => p == Vanilla).Should().ContainSingle();
        prompt.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task SwitchingLoadouts_ReadsNoVanillaFile()
    {
        var first = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(first));
        var second = await LoadoutManager.CreateLoadout(GameInstallation, "second");
        FullHashes.Reset();

        await Synchronizer.Synchronize(Refresh(second));
        await Synchronizer.Synchronize(Refresh(first));

        FullHashes.Paths.Should().BeEmpty();
        FullHashes.BaselineOpens.Should().BeEmpty();
    }

    [Fact]
    public async Task AGameUpdatedSinceTheBaselineWasBuilt_BacksUpAndIngestsNoVanillaFileWhenApplyingSwitchingOrResetting()
    {
        var first = await CreateLoadout();
        var second = await LoadoutManager.CreateLoadout(GameInstallation, "second");
        await Synchronizer.Synchronize(Refresh(second));
        await Synchronizer.Synchronize(Refresh(first));
        await TheBaselineIsFromAnOlderBuildAndTheStoreKnowsTheInstalledOne();
        FullHashes.Reset();

        await Synchronizer.Synchronize(Refresh(first));
        await Synchronizer.Synchronize(Refresh(second));
        await Synchronizer.Synchronize(Refresh(first));

        Overrides(Refresh(first)).Should().BeEquivalentTo(PlayerFiles, "an unchanged vanilla file is not an external change");
        await NoVanillaFileWasBackedUp();

        await Synchronizer.ResetToOriginalGameState(GameInstallation, Refresh(first).LocatorIds.ToArray());

        foreach (var file in CyberpunkSteamInstall.GameFiles) (await ReadGameFile(file.Path)).Should().Be(System.Text.Encoding.UTF8.GetString(file.Content));
        await NoVanillaFileWasBackedUp();
    }

    // Steam moved the game to new depot manifests while the app was running. The registry's installation keeps the ids
    // it was located with until a sync sees the new build, and only the app manifest on disk names it.
    private void SteamUpdatesTheGame(bool cachesTheNewManifests)
    {
        var game = CyberpunkSteamInstall.GameDepot with { ManifestId = NewGameManifestId };
        var expansion = CyberpunkSteamInstall.ExpansionDepot with { ManifestId = NewExpansionManifestId };
        if (cachesTheNewManifests)
        {
            Library.RestoreManifest(game);
            Library.RestoreManifest(expansion);
        }

        var appManifest = Install.AppManifest.ToString();
        File.WriteAllText(appManifest, File.ReadAllText(appManifest)
            .Replace(CyberpunkSteamInstall.GameManifestId.ToString(), NewGameManifestId.ToString())
            .Replace(CyberpunkSteamInstall.ExpansionManifestId.ToString(), NewExpansionManifestId.ToString()));
    }

    private const ulong NewGameManifestId = 7000000000000000011;
    private const ulong NewExpansionManifestId = 7000000000000000012;

    private async Task EveryVanillaFileIsOnDiskAndNoneWasBackedUp()
    {
        foreach (var file in CyberpunkSteamInstall.GameFiles.Concat(CyberpunkSteamInstall.ExpansionFiles))
            (await ReadGameFile(file.Path)).Should().Be(System.Text.Encoding.UTF8.GetString(file.Content), $"`{file.Path}` is the game's own");
        await NoVanillaFileWasBackedUp();
    }

    [Fact]
    public async Task AGameUpdatedToABuildNoStoreCovers_RefusesTheApplyAndLeavesEveryVanillaFileAlone()
    {
        var loadout = await CreateLoadout();
        SteamUpdatesTheGame(cachesTheNewManifests: false);

        var act = () => Synchronizer.Synchronize(Refresh(loadout));

        var refusal = await act.Should().ThrowAsync<UserFacingException>();
        refusal.Which.Message.Should().Contain("Let Steam finish updating", "the block lifts once Steam has cached the new build's manifests");
        await EveryVanillaFileIsOnDiskAndNoneWasBackedUp();
        Refresh(loadout).LocatorIds.Select(id => id.Value).Should().NotContain(NewGameManifestId.ToString(), "a refused apply writes nothing");
    }

    [Fact]
    public async Task AGameSteamIsStillUpdating_RefusesTheApplyUntilItFinishes()
    {
        var loadout = await CreateLoadout();
        SteamUpdatesTheGame(cachesTheNewManifests: true);
        SteamLibraryFixture.SetStateFlags(Install, 1026);

        var act = () => Synchronizer.Synchronize(Refresh(loadout));

        var refusal = await act.Should().ThrowAsync<UserFacingException>();
        refusal.Which.Message.Should().StartWith("Steam is updating").And.Contain("try again when it finishes").And.Contain("resume the update in Steam").And.Contain("Nothing was changed");
        await EveryVanillaFileIsOnDiskAndNoneWasBackedUp();
        Refresh(loadout).LocatorIds.Select(id => id.Value).Should().NotContain(NewGameManifestId.ToString(), "a refused apply writes nothing");

        SteamLibraryFixture.SetStateFlags(Install, 4);

        await Synchronizer.Synchronize(Refresh(loadout));
        Refresh(loadout).LocatorIds.Select(id => id.Value).Should().Contain(NewGameManifestId.ToString());
    }

    [Fact]
    public async Task AGameSteamReportsAsMissingFiles_RefusesTheApplyAndSaysToVerifyThem()
    {
        var loadout = await CreateLoadout();
        SteamLibraryFixture.SetStateFlags(Install, 36);

        var act = () => Synchronizer.Synchronize(Refresh(loadout));

        var refusal = await act.Should().ThrowAsync<UserFacingException>();
        refusal.Which.Message.Should().Contain("missing or damaged").And.Contain("Verify the game's files in Steam");
    }

    [Fact]
    public async Task AGameSteamFlagsAsNeedingAnUpdateButIsNotWriting_StillApplies()
    {
        var loadout = await CreateLoadout();
        SteamLibraryFixture.SetStateFlags(Install, 6);

        await Synchronizer.Synchronize(Refresh(loadout));
    }

    [Fact]
    public async Task AGameUpdatedToABuildTheStoreHasManifestsFor_RebuildsTheBaselineAndTouchesNoVanillaFile()
    {
        var loadout = await CreateLoadout();
        SteamUpdatesTheGame(cachesTheNewManifests: true);

        await Synchronizer.Synchronize(Refresh(loadout));

        Refresh(loadout).LocatorIds.Select(id => id.Value).Should().Contain(NewGameManifestId.ToString());
        LocalBaseline.All(Hashes.Local).Single().LocatorIds.Should().Contain(NewGameManifestId.ToString(), "the apply built the baseline of the installed build");
        Overrides(Refresh(loadout)).Should().BeEquivalentTo(PlayerFiles, "an unchanged vanilla file is not an external change");
        await EveryVanillaFileIsOnDiskAndNoneWasBackedUp();
    }

    [Fact]
    public async Task AGameUpdatedAfterItWasRegistered_ASecondSyncWithTheAdoptedIdsReadsAndBacksUpNoVanillaFile()
    {
        var loadout = await CreateLoadout();
        SteamUpdatesTheGame(cachesTheNewManifests: true);
        await Synchronizer.Synchronize(Refresh(loadout));
        Refresh(loadout).LocatorIds.Select(id => id.Value).Should().Contain(NewGameManifestId.ToString());
        FullHashes.Reset();

        // The first sync refreshed the registry; this one must work whether or not that refresh reached the copy the index reads.
        await Synchronizer.Synchronize(Refresh(loadout));

        FullyHashed.Should().BeEmpty("the store vouches for the new build's files, so the index has nothing to read");
        Overrides(Refresh(loadout)).Should().BeEquivalentTo(PlayerFiles, "an unchanged vanilla file is not an external change");
        await EveryVanillaFileIsOnDiskAndNoneWasBackedUp();
    }

    [Fact]
    public async Task AGameUpdatedAfterItWasRegistered_ASecondSyncArchivesNoVanillaFile()
    {
        var loadout = await CreateLoadout();
        SteamUpdatesTheGame(cachesTheNewManifests: true);
        await Synchronizer.Synchronize(Refresh(loadout));

        // Indexed through the startup ids, the vanilla files would look changed and be archived as the player's own.
        await Synchronizer.Synchronize(Refresh(loadout));

        await NoVanillaFileWasBackedUp();
    }

    [Fact]
    public async Task AGameUpdatedAfterItWasRegistered_AModOverAVanillaFileSettlesItsAttestedRowAgainstTheNewBuild()
    {
        var loadout = await CreateLoadout();
        SteamUpdatesTheGame(cachesTheNewManifests: true);
        await Synchronizer.Synchronize(Refresh(loadout));
        FullHashes.Reset();
        await AddMod(loadout, Vanilla);

        await Synchronizer.Synchronize(Refresh(loadout));

        FullyHashed.Should().ContainSingle().Which.Should().Be(Vanilla);
        (await FileStore.HaveFile(System.Text.Encoding.UTF8.GetBytes(VanillaText).xxHash3())).Should().BeTrue("the vanilla bytes were archived before the mod replaced them");
        var baseline = LocalBaseline.All(Hashes.Local).Single(b => b.LocatorIds.Contains(NewGameManifestId.ToString()));
        AttestedFile.FindByBaseline(Hashes.Local, baseline.Id).Select(r => r.Path.ToString()).Should().NotContain(Vanilla, "the read settled the row");
        baseline.Files.Select(f => f.Path.ToString()).Should().Contain(Vanilla, "the read promoted the row on the new build's baseline");
    }

    [Fact]
    public async Task AGameUpdatedAfterItWasRegistered_TheFirstSyncRefreshesTheRegistrysCopyOfTheInstall()
    {
        var loadout = await CreateLoadout();
        SteamUpdatesTheGame(cachesTheNewManifests: true);

        await Synchronizer.Synchronize(Refresh(loadout));

        var registered = Refresh(loadout).InstallationInstance;
        registered.LocatorResult.LocatorIds.Select(id => id.Value).Should().Contain(NewGameManifestId.ToString(), "readers of the registry see the build installed now");
        GameRegistry.LocateGameInstallations().Single(i => i.Game.GameId == registered.Game.GameId).LocatorResult.LocatorIds
            .Should().BeEquivalentTo(registered.LocatorResult.LocatorIds);
    }

    [Fact]
    public async Task ARegistryCopyTakenMidUpdateWhenSteamSettlesOnTheSameBuild_TheFirstSyncClearsTheUpdateFlags()
    {
        var loadout = await CreateLoadout();
        var current = Refresh(loadout).InstallationInstance;
        var settled = current.LocatorResult;
        GameRegistry.RefreshInstallation(new GameInstallation(new GameLocatorResult
        {
            Game = settled.Game,
            Path = settled.Path,
            Store = settled.Store,
            StoreIdentifier = settled.StoreIdentifier,
            LocatorIds = settled.LocatorIds,
            Locator = settled.Locator,
            Platform = settled.Platform,
            StoreRoot = settled.StoreRoot,
            StoreBuildId = settled.StoreBuildId,
            StoreUpdatePending = true,
            StoreUpdateInProgress = true,
        }, current.Locations));
        Refresh(loadout).InstallationInstance.LocatorResult.StoreUpdateInProgress.Should().BeTrue("the registry holds the copy taken mid-update");

        await Synchronizer.Synchronize(Refresh(loadout));

        var registered = Refresh(loadout).InstallationInstance.LocatorResult;
        registered.StoreUpdateInProgress.Should().BeFalse();
        registered.StoreUpdatePending.Should().BeFalse();
    }

    [Fact]
    public async Task AGameUpdatedAfterItWasRegistered_RescanningAndBuildingATreeReadNoVanillaFile()
    {
        var loadout = await CreateLoadout();
        SteamUpdatesTheGame(cachesTheNewManifests: true);
        await Synchronizer.Synchronize(Refresh(loadout));
        FullHashes.Reset();

        await Synchronizer.RescanExternalChanges(Refresh(loadout));
        await Synchronizer.BuildSyncTree(Refresh(loadout));

        FullyHashed.Should().BeEmpty("the index works from the build located now, not the ids the registry kept");
    }

    [Fact]
    public async Task AGameUpdatedWhenTheBaselineCannotBeRebuilt_RefusesTheApplyAndLeavesEveryVanillaFileAlone()
    {
        var loadout = await CreateLoadout();
        SteamUpdatesTheGame(cachesTheNewManifests: true);
        _settle.FailBaselineBuild = true;

        var act = () => Synchronizer.Synchronize(Refresh(loadout));

        await act.Should().ThrowAsync<UserFacingException>();
        await EveryVanillaFileIsOnDiskAndNoneWasBackedUp();
    }

    [Fact]
    public async Task AGameUpdatedToABuildOnlyTheStoreLaterDescribes_NeverTakesTheDeployedModForTheGamesOwnFile()
    {
        var loadout = await CreateLoadout();
        await AddMod(loadout, Vanilla);
        await Synchronizer.Synchronize(Refresh(loadout));
        SteamUpdatesTheGame(cachesTheNewManifests: false);

        var refused = () => Synchronizer.Synchronize(Refresh(loadout));

        await refused.Should().ThrowAsync<UserFacingException>();
        LocalBaseline.All(Hashes.Local).Single().LocatorIds.Should().NotContain(NewGameManifestId.ToString(),
            "a snapshot of a folder with a mod deployed is not the vanilla list of the new build");

        await TheDownloadedStoreLearnsTheNewBuild();
        await RemoveMod(loadout);
        await Synchronizer.Synchronize(Refresh(loadout));

        Refresh(loadout).LocatorIds.Select(id => id.Value).Should().Contain(NewGameManifestId.ToString());
        (await ReadGameFile(Vanilla)).Should().Be(VanillaText, "disabling the mod restores the vanilla bytes");
        Overrides(Refresh(loadout)).Should().NotContain(Vanilla);
    }

    [Fact]
    public async Task AGameUpdatedToABuildNoStoreCovers_WhenNoBaselineExists_RefusesTheApplyAndWritesNoBaseline()
    {
        var loadout = await CreateLoadout();
        await AddMod(loadout, Vanilla);
        await Synchronizer.Synchronize(Refresh(loadout));
        await Hashes.RemoveLocalBaseline(GameInstallation.LocatorResult.Store, GameFolder.ToString());
        SteamUpdatesTheGame(cachesTheNewManifests: false);

        var refused = () => Synchronizer.Synchronize(Refresh(loadout));

        var refusal = await refused.Should().ThrowAsync<UserFacingException>();
        refusal.Which.Message.Should().Contain("Let Steam finish updating");
        LocalBaseline.All(Hashes.Local).Should().BeEmpty("a snapshot of a folder with a mod deployed would call the mod vanilla");
        Refresh(loadout).LocatorIds.Select(id => id.Value).Should().NotContain(NewGameManifestId.ToString());
    }

    [Fact]
    public async Task AKeptBaselineOfAnOlderBuild_IsNotAskedToRebuildOnEveryApplyOnceTheStoreCoversTheBuild()
    {
        var loadout = await CreateLoadout();
        await AddMod(loadout, Vanilla);
        await Synchronizer.Synchronize(Refresh(loadout));
        SteamUpdatesTheGame(cachesTheNewManifests: false);
        var refused = () => Synchronizer.Synchronize(Refresh(loadout));
        await refused.Should().ThrowAsync<UserFacingException>();
        await TheDownloadedStoreLearnsTheNewBuild();
        await Synchronizer.Synchronize(Refresh(loadout));
        LocalBaseline.All(Hashes.Local).Single().LocatorIds.Should().NotContain(NewGameManifestId.ToString(), "the deployed mod keeps the old baseline in place");
        var calls = _settle.BaselineBuildCalls;

        await Synchronizer.Synchronize(Refresh(loadout));

        _settle.BaselineBuildCalls.Should().Be(calls, "the coordinator would refuse again and read the depotcache for nothing");
    }

    // The store's own data: a newer downloaded database on disk, which the service connects when it checks for one.
    private async Task TheDownloadedStoreLearnsTheNewBuild()
    {
        var location = ServiceProvider.GetRequiredService<ISettingsManager>().Get<FileHashesServiceSettings>()
            .HashDatabaseLocation.ToPath(ServiceProvider.GetRequiredService<IFileSystem>());
        var path = location / $"{Guid.NewGuid()}_{DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds()}";
        using (var backend = new SilverModManager.MnemonicDB.Storage.RocksDbBackend.Backend())
        using (var store = new DatomStore(NullLogger<DatomStore>.Instance, new DatomStoreSettings { Path = path }, backend))
        using (var downloaded = new SilverModManager.MnemonicDB.Connection(NullLogger<SilverModManager.MnemonicDB.Connection>.Instance, store, ServiceProvider, []))
        {
            await AddDownloadedManifest(downloaded, CyberpunkSteamInstall.GameDepotId, NewGameManifestId, CyberpunkSteamInstall.GameFiles);
            await AddDownloadedManifest(downloaded, CyberpunkSteamInstall.ExpansionDepotId, NewExpansionManifestId, CyberpunkSteamInstall.ExpansionFiles);
        }

        await Hashes.CheckForUpdate();
        Hashes.Current.Connection.Should().NotBeSameAs(Hashes.Local.Connection, "the downloaded database is connected");
    }

    private static async Task AddDownloadedManifest(IConnection store, uint depotId, ulong manifestId, SteamFile[] files)
    {
        using var tx = store.BeginTransaction();
        var paths = new List<EntityId>();
        foreach (var file in files)
        {
            var hashes = await MultiHasher.HashStream(new MemoryStream(file.Content));
            var relation = new HashRelation.New(tx)
            {
                MinimalHash = hashes.MinimalHash,
                XxHash3 = hashes.XxHash3,
                Sha1 = hashes.Sha1,
                XxHash64 = hashes.XxHash64,
                Md5 = hashes.Md5,
                Size = hashes.Size,
                Crc32 = hashes.Crc32,
            };
            paths.Add(new PathHashRelation.New(tx) { Path = file.Path, HashId = relation }.Id);
        }

        _ = new SteamManifest.New(tx)
        {
            AppId = SilverModManager.Abstractions.Steam.Values.AppId.From(CyberpunkSteamInstall.AppId),
            DepotId = SilverModManager.Abstractions.Steam.Values.DepotId.From(depotId),
            ManifestId = SilverModManager.Abstractions.Steam.Values.ManifestId.From(manifestId),
            Name = "public",
            FilesIds = paths,
        };
        await tx.Commit();
    }

    private async Task NoVanillaFileWasBackedUp()
    {
        foreach (var file in CyberpunkSteamInstall.GameFiles.Concat(CyberpunkSteamInstall.ExpansionFiles))
            (await FileStore.HaveFile(file.Content.xxHash3())).Should().BeFalse($"`{file.Path}` is the game's own and unchanged");
    }

    // The baseline's rows are for the build before an update, and the store now lists the installed build's files with
    // their xxHash3: the new manifest alone must answer for every path.
    private async Task TheBaselineIsFromAnOlderBuildAndTheStoreKnowsTheInstalledOne()
    {
        var baseline = LocalBaseline.All(Hashes.Local).Single();
        var contents = CyberpunkSteamInstall.GameFiles.Concat(CyberpunkSteamInstall.ExpansionFiles).ToDictionary(f => f.Path, f => f.Content);
        using var tx = Hashes.Local.Connection.BeginTransaction();
        foreach (var row in AttestedFile.FindByBaseline(Hashes.Local, baseline.Id))
        {
            var hashes = await MultiHasher.HashStream(new MemoryStream(contents[row.Path.ToString()]));
            var relation = new HashRelation.New(tx)
            {
                MinimalHash = hashes.MinimalHash,
                XxHash3 = hashes.XxHash3,
                Sha1 = hashes.Sha1,
                XxHash64 = hashes.XxHash64,
                Md5 = hashes.Md5,
                Size = hashes.Size,
                Crc32 = hashes.Crc32,
            };
            var path = new PathHashRelation.New(tx) { Path = row.Path, HashId = relation };
            tx.Add(row.ManifestId, SteamManifest.Files, path.Id);
        }

        foreach (var id in baseline.LocatorIds) tx.Retract(baseline.Id, LocalBaseline.LocatorIds, id);
        tx.Add(baseline.Id, LocalBaseline.LocatorIds, "6000000000000000999");
        await tx.Commit();
    }

    private sealed class SettleFailingHashes(IFileHashesService inner, SettleSwitch gate) : IFileHashesService, Microsoft.Extensions.Hosting.IHostedService, IDisposable
    {
        public Task SettleAttested(GameInstallation installation, IReadOnlyList<(AttestedLookup Attested, MultiHash Hashes, SilverModManager.Sdk.IO.FileFingerprint After)> read, CancellationToken cancellationToken = default)
            => gate.Fail && read.Count > 0 ? throw new InvalidOperationException("the settle step failed") : inner.SettleAttested(installation, read, cancellationToken);

        public Task StartAsync(CancellationToken cancellationToken) => ((Microsoft.Extensions.Hosting.IHostedService)inner).StartAsync(cancellationToken);
        public Task StopAsync(CancellationToken cancellationToken) => ((Microsoft.Extensions.Hosting.IHostedService)inner).StopAsync(cancellationToken);
        public void Dispose() => (inner as IDisposable)?.Dispose();

        public IDb Current => inner.Current;
        public IDb Local => inner.Local;
        public IReadOnlyList<IDb> Databases => inner.Databases;
        public ValueTask<IDb> GetFileHashesDb() => inner.GetFileHashesDb();
        public Task CheckForUpdate(bool forceUpdate = false) => inner.CheckForUpdate(forceUpdate);
        public IEnumerable<VanityVersion> GetKnownVanityVersions(SilverModManager.Sdk.NexusModsApi.NexusModsGameId nexusModsGameId) => inner.GetKnownVanityVersions(nexusModsGameId);
        public IEnumerable<GameFileRecord> GetGameFiles(GameInstallation installation) => inner.GetGameFiles(installation);
        public IEnumerable<GameFileRecord> GetGameFiles(LocatorIdsWithGameStore locatorIdsWithGameStore) => inner.GetGameFiles(locatorIdsWithGameStore);
        public bool CoversInstalledBuild(LocatorIdsWithGameStore locatorIdsWithGameStore) => inner.CoversInstalledBuild(locatorIdsWithGameStore);
        public bool TryGetVanityVersion(LocatorIdsWithGameStore locatorIdsWithGameStore, out VanityVersion version) => inner.TryGetVanityVersion(locatorIdsWithGameStore, out version);
        public bool TryGetLocatorIdsForVanityVersion(GameStore gameStore, VanityVersion version, out LocatorId[] locatorIds) => inner.TryGetLocatorIdsForVanityVersion(gameStore, version, out locatorIds);
        public LocatorId[] GetLocatorIdsForVersionDefinition(GameStore gameStore, VersionDefinition.ReadOnly versionDefinition) => inner.GetLocatorIdsForVersionDefinition(gameStore, versionDefinition);
        public Task<LocalBaselineResult> EnsureLocalBaseline(GameInstallation installation, CancellationToken cancellationToken = default)
            => CountedEnsure(installation, cancellationToken);
        private Task<LocalBaselineResult> CountedEnsure(GameInstallation installation, CancellationToken cancellationToken)
        {
            gate.BaselineBuildCalls++;
            return gate.FailBaselineBuild ? throw new InvalidOperationException("the baseline build failed") : inner.EnsureLocalBaseline(installation, cancellationToken);
        }
        public Task MoveLocalBaseline(GameStore store, string oldPath, string newPath, CancellationToken cancellationToken = default) => inner.MoveLocalBaseline(store, oldPath, newPath, cancellationToken);
        public Task RemoveLocalBaseline(GameStore store, string gamePath, CancellationToken cancellationToken = default) => inner.RemoveLocalBaseline(store, gamePath, cancellationToken);
        public Task WaitForLocalBaselineBuild(GameInstallation installation, CancellationToken cancellationToken = default) => inner.WaitForLocalBaselineBuild(installation, cancellationToken);
        public Task RecordIndexedFiles(GameInstallation installation, IReadOnlyList<IndexedFileHash> files, CancellationToken cancellationToken = default) => inner.RecordIndexedFiles(installation, files, cancellationToken);
        public bool TryGetFingerprintedHash(GameInstallation installation, GamePath path, Size size, DateTimeOffset modifiedUtc, out Hash xxHash3) => inner.TryGetFingerprintedHash(installation, path, size, modifiedUtc, out xxHash3);
        public System.Collections.Frozen.FrozenDictionary<GamePath, AttestedLookup> AttestedFiles(GameInstallation installation) => inner.AttestedFiles(installation);
        public DynamicData.Kernel.Optional<VersionData> SuggestVersionData(GameInstallation gameInstallation, IEnumerable<(GamePath Path, Hash Hash)> files) => inner.SuggestVersionData(gameInstallation, files);
    }
}
