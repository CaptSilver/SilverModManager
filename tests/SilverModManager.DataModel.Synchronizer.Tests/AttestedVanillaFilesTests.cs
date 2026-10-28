using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services)
            .AddOSInterop()
            .AddRuntimeDependencies()
            .RemoveRuntimeDependencyChecker()
            .AddGenericGameSupport()
            .AddFakeToolRunner()
            .AddRedEngineGames();

        // The real service, behind a switch that makes the settle step fail.
        var real = services.Last(d => d.ServiceType == typeof(IFileHashesService));
        services.Remove(real);
        return services.AddSingleton<IFileHashesService>(sp =>
            new SettleFailingHashes((IFileHashesService)ActivatorUtilities.CreateInstance(sp, real.ImplementationType ?? throw new InvalidOperationException()), _settle));
    }

    private sealed class SettleSwitch
    {
        public bool Fail { get; set; }
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
        public Task<LocalBaselineResult> EnsureLocalBaseline(GameInstallation installation, CancellationToken cancellationToken = default) => inner.EnsureLocalBaseline(installation, cancellationToken);
        public Task RemoveLocalBaseline(GameStore store, string gamePath, CancellationToken cancellationToken = default) => inner.RemoveLocalBaseline(store, gamePath, cancellationToken);
        public Task WaitForLocalBaselineBuild(GameInstallation installation, CancellationToken cancellationToken = default) => inner.WaitForLocalBaselineBuild(installation, cancellationToken);
        public Task RecordIndexedFiles(GameInstallation installation, IReadOnlyList<IndexedFileHash> files, CancellationToken cancellationToken = default) => inner.RecordIndexedFiles(installation, files, cancellationToken);
        public bool TryGetFingerprintedHash(GameInstallation installation, GamePath path, Size size, DateTimeOffset modifiedUtc, out Hash xxHash3) => inner.TryGetFingerprintedHash(installation, path, size, modifiedUtc, out xxHash3);
        public System.Collections.Frozen.FrozenDictionary<GamePath, AttestedLookup> AttestedFiles(GameInstallation installation) => inner.AttestedFiles(installation);
        public bool TryFindRelation(Hash xxHash3, out (Sha1Value Sha1, Size Size) relation) => inner.TryFindRelation(xxHash3, out relation);
        public DynamicData.Kernel.Optional<VersionData> SuggestVersionData(GameInstallation gameInstallation, IEnumerable<(GamePath Path, Hash Hash)> files) => inner.SuggestVersionData(gameInstallation, files);
    }
}
