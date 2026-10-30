using System.Collections.Frozen;
using System.Reactive.Linq;
using System.Text;
using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.Merge;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.Games.Witcher.Witcher3;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Witcher.Tests.Witcher3;

/// <summary>
/// The next-gen Witcher 3 rebuilds <c>content/metadata.store</c> and its stamp every time the set of mod and DLC
/// bundles changes, and its script compiler writes <c>x64.final.redscripts</c>. Steam lists the store as a vanilla
/// file, so the bytes the game leaves behind must not read as a changed game file.
/// </summary>
public class Witcher3GameRewrittenFilesTests(ITestOutputHelper helper)
    : ASteamInstalledGameTest<Witcher3GameRewrittenFilesTests, Witcher3Game>(helper)
{
    private const uint AppId = 292030;
    private const uint DepotId = 292031;
    private const ulong ManifestId = 9000000000000000001;

    private const string MetadataStore = "content/metadata.store";
    private const string Stamp = "content/metadata.store.stamp";
    private const string CompiledScripts = "content/content0/x64.final.redscripts";

    private static readonly string[] GameRewritten = [MetadataStore, Stamp, CompiledScripts];

    protected override bool StoreVouchesForTheFiles => true;

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services).AddWitcherGames();

    protected override SteamInstall InstallGame(SteamLibraryFixture library)
    {
        var depot = new SteamDepot(DepotId, ManifestId,
        [
            new SteamFile("bin/x64/witcher3.exe", "executable"),
            new SteamFile("bin/x64_DX12/witcher3.exe", "dx12 executable"),
            new SteamFile(MetadataStore, "vanilla metadata store"),
            new SteamFile("content/content0/cookedfinal.redscripts", "steam listed scripts"),
            new SteamFile("content/blob0.bundle", "bundle"),
        ]);
        return library.AddGame(AppId, "The Witcher 3", "The Witcher 3: Wild Hunt", 26238013, depot);
    }

    /// <summary>What the game leaves behind after it starts: a rebuilt store, its stamp and a compiled script cache.</summary>
    private async Task TheGameRunsOnce()
    {
        foreach (var path in GameRewritten)
        {
            var file = GameFolder / path;
            file.Parent.CreateDirectory();
            await file.WriteAllBytesAsync(Encoding.UTF8.GetBytes("written by the game: " + path));
        }
    }

    private static string[] Paths(IEnumerable<GamePath> paths) => paths.Select(p => p.Path.ToString()).Order(StringComparer.Ordinal).ToArray();

    /// <summary>Like the diagnostic manager: the tree is processed before the emitters read it, which is what sets the warnings.</summary>
    private async Task<Diagnostic[]> RunEmittersOnProcessedTree(Loadout.ReadOnly loadout)
    {
        var tree = await Synchronizer.BuildSyncTree(loadout);
        Synchronizer.ProcessSyncTree(tree);
        var frozen = tree.ToFrozenDictionary();
        var emitters = GameInstallation.GetGame().DiagnosticEmitters
            .Concat(ServiceProvider.GetServices<IDiagnosticEmitter>())
            .OfType<ILoadoutDiagnosticEmitter>();

        var found = new List<Diagnostic>();
        foreach (var emitter in emitters)
        {
            await foreach (var diagnostic in emitter.Diagnose(loadout, frozen, CancellationToken.None))
                found.Add(diagnostic);
        }

        return found.ToArray();
    }

    [Fact]
    public async Task TheStoreTheGameRewroteIsNotAChangedGameFile()
    {
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(loadout));
        await TheGameRunsOnce();
        await Synchronizer.Synchronize(Refresh(loadout));

        var tree = await Synchronizer.BuildSyncTree(Refresh(loadout));
        Synchronizer.ProcessSyncTree(tree);

        tree.Where(kv => kv.Value.Actions.HasFlag(Actions.WarnOfUnableToExtract)).Select(kv => kv.Key).Should().BeEmpty();
        Paths(tree.Keys).Should().NotContain(GameRewritten, "the game's own files are not part of the sync");
    }

    [Fact]
    public async Task TheFilesTheGameRewroteRaiseNoDiagnostic()
    {
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(loadout));
        await TheGameRunsOnce();
        await Synchronizer.Synchronize(Refresh(loadout));

        var diagnostics = await RunEmittersOnProcessedTree(Refresh(loadout));

        diagnostics.Select(Describe).Where(text => GameRewritten.Any(text.Contains)).Should().BeEmpty();
        diagnostics.Select(Describe).Should().NotContain(text => text.Contains("Loadout can't be applied"), "no data mismatch card blocks the apply");
    }

    [Fact]
    public async Task AnApplyAfterTheGameRanGoesAheadAndLeavesTheGamesFilesAsTheyAre()
    {
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(loadout));
        await TheGameRunsOnce();

        await Synchronizer.Synchronize(Refresh(loadout));

        foreach (var path in GameRewritten)
            (await (GameFolder / path).ReadAllTextAsync()).Should().Be("written by the game: " + path);
        var overrides = LoadoutOverridesGroup.FindByOverridesFor(Connection.Db, loadout.Id)
            .SelectMany(g => g.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath())
            .Select(item => ((GamePath)item.TargetPath).Path.ToString());
        overrides.Should().NotContain(GameRewritten, "the game's own output is not the player's mod");
    }

    [Fact]
    public async Task ADeletedMetadataStoreIsNotRestoredByAnApply()
    {
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(loadout));

        (GameFolder / MetadataStore).Delete();
        await Synchronizer.Synchronize(Refresh(loadout));

        (GameFolder / MetadataStore).FileExists.Should().BeFalse("the game rebuilds it itself when it starts");
    }

    /// <summary>What the game's synchronizer did before the three paths were filtered: they are indexed like any game file.</summary>
    private sealed class UnfilteredSynchronizer(IServiceProvider provider, IGame game, ALoadoutSynchronizer real) : WitcherSynchronizer(provider, game)
    {
        public override Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout) => real.IntrinsicFiles(loadout);
    }

    private UnfilteredSynchronizer Legacy() => new(ServiceProvider, Game, (ALoadoutSynchronizer)Synchronizer);

    private string[] TrackedRewritten(Loadout.ReadOnly loadout) =>
        Paths(DiskStateEntry.FindByGame(Connection.Db, loadout.Installation.Id).Select(e => (GamePath)e.Path))
            .Where(p => GameRewritten.Contains(p)).ToArray();

    private string[] OverridePaths(Loadout.ReadOnly loadout) =>
        LoadoutOverridesGroup.FindByOverridesFor(Connection.Db, loadout.Id)
            .SelectMany(g => g.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath())
            .Select(item => ((GamePath)item.TargetPath).Path.ToString()).ToArray();

    [Fact]
    public async Task AnInstallThatAlreadyTracksTheStoreKeepsItOnDiskAndStopsTrackingIt()
    {
        var loadout = await CreateLoadout();
        await Legacy().Synchronize(Refresh(loadout));
        TrackedRewritten(loadout).Should().Contain(MetadataStore, "the earlier synchronizer indexed the store");

        await Synchronizer.Synchronize(Refresh(loadout));

        (GameFolder / MetadataStore).FileExists.Should().BeTrue("the apply must not take the vanilla store off disk");
        (await (GameFolder / MetadataStore).ReadAllTextAsync()).Should().Be("vanilla metadata store");
        TrackedRewritten(loadout).Should().BeEmpty();
    }

    [Fact]
    public async Task AStampAndScriptCacheIndexedBeforeTheFilterAreNotIngestedAsOverrides()
    {
        var loadout = await CreateLoadout();
        await Legacy().Synchronize(Refresh(loadout));
        await TheGameRunsOnce();
        // The rescan after a Play, by the synchronizer that did not know the files were the game's.
        await Legacy().ReindexState(GameInstallation);
        TrackedRewritten(loadout).Should().Contain([Stamp, CompiledScripts]);
        // The earlier synchronizer's own record of the vanilla store is already there; only what the apply adds is in question.
        var before = OverridePaths(loadout);

        await Synchronizer.Synchronize(Refresh(loadout));

        foreach (var path in GameRewritten)
            (await (GameFolder / path).ReadAllTextAsync()).Should().Be("written by the game: " + path);
        OverridePaths(loadout).Except(before).Should().BeEmpty("the game's own output is not the player's mod");
        TrackedRewritten(loadout).Should().BeEmpty();
    }

    [Fact]
    public async Task OverridesAnEarlierBuildFiledForTheGamesOwnOutputAreDroppedAndTheFilesStayOnDisk()
    {
        var loadout = await CreateLoadout();
        await Legacy().Synchronize(Refresh(loadout));
        await TheGameRunsOnce();
        await Legacy().Synchronize(Refresh(loadout));
        OverridePaths(loadout).Should().Contain([Stamp, CompiledScripts], "the earlier synchronizer filed the game's output as the player's overrides");

        await Synchronizer.Synchronize(Refresh(loadout));

        OverridePaths(Refresh(loadout)).Should().NotContain(GameRewritten, "the game's own output is not the player's mod");
        foreach (var path in GameRewritten)
            (await (GameFolder / path).ReadAllTextAsync()).Should().Be("written by the game: " + path, "healing the loadout deletes nothing from the game folder");

        // The rows go before the apply builds its tree, so the sync after it has nothing left to delete either.
        await Synchronizer.Synchronize(Refresh(loadout));

        OverridePaths(Refresh(loadout)).Should().NotContain(GameRewritten);
        foreach (var path in GameRewritten)
            (await (GameFolder / path).ReadAllTextAsync()).Should().Be("written by the game: " + path, "the sync after the heal deletes nothing either");
        var diagnostics = await RunEmittersOnProcessedTree(Refresh(loadout));
        diagnostics.Select(Describe).Where(text => GameRewritten.Any(text.Contains)).Should().BeEmpty("no card is left for the game's own output");
    }

    [Fact]
    public async Task AnotherCommitLandingBeforeTheTreeIsBuiltDoesNotMakeTheMergeCheckRefuseTheApply()
    {
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(loadout));
        var held = Refresh(loadout);

        // A background writer (the load order reconciler, say) commits after the apply took its loadout.
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(held.Id, Loadout.Name, "renamed while the apply runs");
            await tx.Commit();
        }

        var tree = await Synchronizer.BuildSyncTree(held);
        var apply = async () => await Synchronizer.RunActions(tree, held);

        await apply.Should().NotThrowAsync<MergeApplyRefusedException>("the tree was built from the loadout the merge check judges");
    }

    [Fact]
    public async Task ANewerRevisionListedMeanwhileDoesNotMakeTheMergeCheckRefuseTheApply()
    {
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(loadout));
        var held = Refresh(loadout);
        var tree = await Synchronizer.BuildSyncTree(held);

        // A commit lands while the apply runs, and a refresh (a diagnostics pass, the page) lists the newer
        // revision, which drops the listing of the revision the apply holds.
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(held.Id, Loadout.Name, "renamed while the apply runs");
            await tx.Commit();
        }
        ((ALoadoutSynchronizer)Synchronizer).WinningFilesForTest(Refresh(loadout));

        var apply = async () => await Synchronizer.RunActions(tree, held);

        await apply.Should().NotThrowAsync<MergeApplyRefusedException>("the check lists the revision it judges again instead of refusing");
    }

    [Fact]
    public async Task OneApplyHealingTheOverridesLeavesNothingElseToApply()
    {
        var loadout = await CreateLoadout();
        await Legacy().Synchronize(Refresh(loadout));
        await TheGameRunsOnce();
        await Legacy().Synchronize(Refresh(loadout));
        OverridePaths(loadout).Should().Contain([Stamp, CompiledScripts], "the earlier synchronizer filed the game's output as the player's overrides");

        await Synchronizer.Synchronize(Refresh(loadout));

        // The state the Apply button shows: it lights up when the loadout differs from what was just applied.
        var status = await (await SynchronizerService.StatusForLoadout(loadout.LoadoutId))
            .Where(state => state != LoadoutSynchronizerState.Pending).Timeout(TimeSpan.FromSeconds(30)).FirstAsync();
        status.Should().Be(LoadoutSynchronizerState.Current, "the heal belongs to the apply that made it");
    }

    // The load order is reconciled in the background whenever a loadout item changes, and an apply that heals the
    // overrides changes items. Under load that pass lands after the apply has recorded itself. When it has
    // nothing to change it must not commit at all: any later commit makes the status diff the loadout against the
    // disk, and mods.settings is rewritten on every sync, so the diff always finds work.
    [Fact]
    public async Task ReconcilingTheLoadOrderAfterAnApplyCommitsNothingAndLeavesNothingToApply()
    {
        var loadout = await CreateLoadout();
        await Legacy().Synchronize(Refresh(loadout));
        await TheGameRunsOnce();
        await Legacy().Synchronize(Refresh(loadout));
        await Synchronizer.Synchronize(Refresh(loadout));
        var basisAfterApply = Connection.Db.BasisTxId;

        await Game.SortOrderManager.UpdateLoadOrders(loadout.LoadoutId);

        Connection.Db.BasisTxId.Should().Be(basisAfterApply, "the load order was already up to date, so there is nothing to record");
        var status = await (await SynchronizerService.StatusForLoadout(loadout.LoadoutId))
            .Where(state => state != LoadoutSynchronizerState.Pending).Timeout(TimeSpan.FromSeconds(30)).FirstAsync();
        status.Should().Be(LoadoutSynchronizerState.Current);
    }

    private async Task<LoadoutSynchronizerState> StatusNow(Loadout.ReadOnly loadout) =>
        await (await SynchronizerService.StatusForLoadout(loadout.LoadoutId))
            .Where(state => state != LoadoutSynchronizerState.Pending).Timeout(TimeSpan.FromSeconds(30)).FirstAsync();

    // A commit to anything else (a diagnostics result, the library, another game) moves the database head but
    // not what an apply would write. The Witcher always has work for mods.settings unless the status can tell
    // the loadout is as it was applied, so it must not read the head.
    [Fact]
    public async Task ACommitOutsideTheLoadoutAfterAnApplyLeavesNothingToApply()
    {
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(loadout));

        await CommitAnUnrelatedLoadout();

        (await StatusNow(loadout)).Should().Be(LoadoutSynchronizerState.Current);
    }

    [Fact]
    public async Task AModAddedAfterAnApplyNeedsAnotherApply()
    {
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(loadout));

        using (var tx = Connection.BeginTransaction())
        {
            var group = AddEmptyGroup(tx, loadout.LoadoutId, "late mod");
            AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, "Mods/modLate/late.ws"), "late");
            await tx.Commit();
        }

        (await StatusNow(loadout)).Should().Be(LoadoutSynchronizerState.NeedsSync);
    }

    // The status is a stream the Apply button stays subscribed to: it has to move when the loadout changes under it,
    // and the commit that does it is not the one the apply recorded.
    [Fact]
    public async Task AModAddedWhileTheStatusIsWatchedTurnsItToNeedsSyncAndAnotherApplyTurnsItBack()
    {
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(loadout));
        var seen = new System.Collections.Concurrent.ConcurrentQueue<LoadoutSynchronizerState>();
        using var subscription = (await SynchronizerService.StatusForLoadout(loadout.LoadoutId)).Subscribe(seen.Enqueue);
        (await StatusNow(loadout)).Should().Be(LoadoutSynchronizerState.Current);

        using (var tx = Connection.BeginTransaction())
        {
            var group = AddEmptyGroup(tx, loadout.LoadoutId, "late mod");
            AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, "Mods/modLate/late.ws"), "late");
            await tx.Commit();
        }
        await WaitFor(() => seen.LastOrDefault() == LoadoutSynchronizerState.NeedsSync);

        await Synchronizer.Synchronize(Refresh(loadout));
        await CommitAnUnrelatedLoadout();
        await WaitFor(() => seen.LastOrDefault() == LoadoutSynchronizerState.Current);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the status never reached the expected state");
            await Task.Delay(25);
        }
    }

    // Removing a mod that is the only provider of its files retracts datoms and asserts none, so the newest
    // datom of the loadout does not move; the status still has to notice.
    [Fact]
    public async Task RemovingTheOnlyModAfterAnApplyNeedsAnotherApply()
    {
        var loadout = await CreateLoadout();
        LoadoutItemGroupId group;
        using (var tx = Connection.BeginTransaction())
        {
            group = AddEmptyGroup(tx, loadout.LoadoutId, "only mod");
            AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, "Mods/modOnly/only.ws"), "only");
            var result = await tx.Commit();
            group = LoadoutItemGroupId.From(result[group.Value]);
        }
        await Synchronizer.Synchronize(Refresh(loadout));
        await CommitAnUnrelatedLoadout();

        using (var tx = Connection.BeginTransaction())
        {
            tx.Delete(group, recursive: true);
            await tx.Commit();
        }

        (await StatusNow(loadout)).Should().Be(LoadoutSynchronizerState.NeedsSync);
    }

    private async Task CommitAnUnrelatedLoadout()
    {
        using var tx = Connection.BeginTransaction();
        var install = new GameInstallMetadata.New(tx)
        {
            Store = GameStore.Unknown,
            Path = "/games/" + Guid.NewGuid(),
            GameId = Witcher3Game.NexusModsGameId.Value,
            Name = "Another game",
            Game = GameId.From("Test.AnotherGame"),
        };
        _ = new Loadout.New(tx)
        {
            Name = Guid.NewGuid().ToString(),
            ShortName = "ZZZ",
            InstallationId = install,
            LoadoutKind = LoadoutKind.Default,
            Revision = 0,
            GameVersion = VanityVersion.From("Unknown"),
        };
        await tx.Commit();
    }

    [Fact]
    public async Task AChangedGameFileSettlesWithinTheApplyThatIngestsIt()
    {
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(loadout));
        var exe = GameFolder / "bin/x64/witcher3.exe";
        await exe.WriteAllBytesAsync(Encoding.UTF8.GetBytes("patched executable"));

        for (var apply = 1; apply <= 2; apply++)
        {
            await Synchronizer.Synchronize(Refresh(loadout));
            var status = await (await SynchronizerService.StatusForLoadout(loadout.LoadoutId))
                .Where(state => state != LoadoutSynchronizerState.Pending).Timeout(TimeSpan.FromSeconds(30)).FirstAsync();
            status.Should().Be(LoadoutSynchronizerState.Current, $"apply {apply} leaves nothing for another");
            (await exe.ReadAllTextAsync()).Should().Be("patched executable");
        }
    }

    [Fact]
    public async Task AModFileOnAPathTheGameRewritesIsExplainedAsTheGamesOwnOutputNotAsASave()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            var group = AddEmptyGroup(tx, loadout.LoadoutId, "mod that ships the stamp");
            AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, Stamp), "a stamp from a mod");
            await tx.Commit();
        }

        var diagnostics = await RunEmittersOnProcessedTree(Refresh(loadout));

        var card = diagnostics.Select(Describe).Should().ContainSingle(text => text.Contains("mod that ships the stamp")).Which;
        card.Should().Contain(Stamp).And.Contain("writes itself");
        card.Should().NotContain("save folder").And.NotContain("Allow save files");
    }

    [Fact]
    public async Task AResetLeavesWhatTheGameRewroteAsItIs()
    {
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(loadout));
        // The vanilla store is in the file store, as it is once an earlier apply backed it up.
        var vanilla = Encoding.UTF8.GetBytes("vanilla metadata store");
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(MetadataStore, new MemoryStream(vanilla)), FileIdentity.XxHash3(vanilla.xxHash3()), Size.FromLong(vanilla.Length))]);
        await TheGameRunsOnce();

        await Synchronizer.ResetToOriginalGameState(GameInstallation, GameInstallation.LocatorResult.LocatorIds.ToArray());

        foreach (var path in GameRewritten)
            (await (GameFolder / path).ReadAllTextAsync()).Should().Be("written by the game: " + path, "a reset does not restore or delete the game's own output");
    }

    private static SyncNodePart Part(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new SyncNodePart { Identity = FileIdentity.XxHash3(bytes.xxHash3()), Size = Size.FromLong(bytes.Length), LastModifiedTicks = 0 };
    }

    [Theory]
    [InlineData(MetadataStore)]
    [InlineData(Stamp)]
    [InlineData(CompiledScripts)]
    public void AResetsVanillaCopyOfAFileTheGameRewritesIsNeitherExtractedNorWarnedAbout(string path)
    {
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [new GamePath(LocationId.Game, path)] = new() { Loadout = Part("vanilla"), SourceItemType = LoadoutSourceItemType.Game },
        };

        Synchronizer.ProcessSyncTree(tree);

        tree.Values.Single().Actions.Should().Be(Actions.DoNothing);
    }

    [Theory]
    [InlineData(MetadataStore)]
    [InlineData(Stamp)]
    [InlineData(CompiledScripts)]
    public void ATrackedCopyOfAFileTheGameRewritesIsForgottenWhateverTheOtherSidesHold(string path)
    {
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [new GamePath(LocationId.Game, path)] = new() { Disk = Part("game"), Loadout = Part("vanilla"), SourceItemType = LoadoutSourceItemType.Game },
        };

        Synchronizer.ProcessSyncTree(tree);

        tree.Values.Single().Actions.Should().Be(Actions.ForgetDiskState);
    }
}
