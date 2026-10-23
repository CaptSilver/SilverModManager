using System.Collections.Immutable;
using System.Text;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Files.Diff;
using NexusMods.Abstractions.Loadouts.Extensions;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.Trees;
using NexusMods.StandardGameLocators.TestHelpers;
using Xunit;

namespace NexusMods.DataModel.Synchronizer.Tests;

/// <summary>
/// The protected hooks on the synchronizer base: adjusting mapped actions, refusing a loadout before a
/// switch, and refusing a sync tree before any action runs.
/// </summary>
public class SynchronizerHookTests : AIsolatedGameTest<SynchronizerHookTests, HookStubbedGame>
{
    private static readonly GamePath KeptOffDisk = new(LocationId.Game, "kept-off-disk.txt");
    private static readonly GamePath Plain = new(LocationId.Game, "plain.txt");

    public SynchronizerHookTests(ITestOutputHelper helper) : base(helper) => HookStubbedGame.Hooks.Reset();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddGame<HookStubbedGame>()
            .AddUniversalGameLocator<HookStubbedGame>(Version.Parse("0.0.0"));
    }

    private ALoadoutSynchronizer Base => (ALoadoutSynchronizer)Synchronizer;

    private AbsolutePath GameFolder => GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, ""));

    private async Task<Loadout.ReadOnly> LoadoutWith(params string[] files)
    {
        var loadout = await CreateLoadout();
        using var tx = Connection.BeginTransaction();
        await AddModAsync(tx, files.Select(f => (RelativePath)f), loadout.LoadoutId, "mod");
        await tx.Commit();
        return Refresh(loadout);
    }

    private Dictionary<string, string> FolderSnapshot()
    {
        var result = new Dictionary<string, string>();
        foreach (var file in GameFolder.EnumerateFiles())
            result[file.RelativeTo(GameFolder).ToString()] = File.ReadAllText(file.ToString());
        return result;
    }

    private string[] DiskStateSnapshot(Loadout.ReadOnly loadout)
    {
        return DiskStateEntry.FindByGame(Connection.Db, loadout.Installation)
            .Select(e => $"{(GamePath)e.Path}|{e.Hash}|{e.Size}")
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static Hash HashOf(string content) => Encoding.UTF8.GetBytes(content).xxHash3();

    [Fact]
    public async Task ProcessSyncTreeCallsAdjustActionsOnceAfterEveryNodeIsMapped()
    {
        var loadout = await LoadoutWith("plain.txt");
        var seen = new List<Dictionary<GamePath, Actions>>();
        HookStubbedGame.Hooks.Adjust = tree => seen.Add(tree.ToDictionary(e => e.Key, e => e.Value.Actions));

        var tree = await Synchronizer.BuildSyncTree(loadout);
        HookStubbedGame.Hooks.Calls.Clear();
        Synchronizer.ProcessSyncTree(tree);

        var atHook = seen.Should().ContainSingle("the hook runs once per tree, after the loop").Subject;
        atHook.Values.Should().NotContain((Actions)0, "every node was mapped before the hook ran");
        atHook[Plain].Should().HaveFlag(Actions.ExtractToDisk);
        HookStubbedGame.Hooks.Calls.Count(c => c == "AdjustActions").Should().Be(1);
    }

    [Fact]
    public async Task AdjustedActionsAreWhatShouldSynchronizeSees()
    {
        var loadout = await LoadoutWith("plain.txt");
        Base.ShouldSynchronize(loadout, [], []).Should().BeTrue("the loadout file is not on disk yet");

        HookStubbedGame.Hooks.Adjust = tree =>
        {
            foreach (var path in tree.Keys.ToArray())
            {
                var node = tree[path];
                node.Actions = Actions.DoNothing;
                tree[path] = node;
            }
        };

        Base.ShouldSynchronize(loadout, [], []).Should().BeFalse();
    }

    [Fact]
    public async Task AdjustedActionsAreWhatLoadoutToDiskDiffSees()
    {
        var loadout = await LoadoutWith("plain.txt");
        ChangeOf(Base.LoadoutToDiskDiff(loadout, [], [])).Should().Be(FileChangeType.Added);

        HookStubbedGame.Hooks.Adjust = tree =>
        {
            var node = tree[Plain];
            node.Actions = Actions.DoNothing;
            tree[Plain] = node;
        };

        ChangeOf(Base.LoadoutToDiskDiff(loadout, [], [])).Should().Be(FileChangeType.None);
    }

    private static FileChangeType ChangeOf(FileDiffTree diff) =>
        diff.GetAllDescendentFiles().Single(f => f.GamePath() == Plain).Item.Value.ChangeType;

    [Fact]
    public async Task APreflightActionsRefusalOnTheLoadoutOverloadLeavesDiskAndDiskStateAsTheyWere()
    {
        var applied = await LoadoutWith("plain.txt");
        await Synchronizer.Synchronize(applied);
        var folderBefore = FolderSnapshot();
        var stateBefore = DiskStateSnapshot(applied);
        folderBefore.Should().ContainKey("plain.txt");

        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, [(RelativePath)"extra.txt"], applied.LoadoutId, "second mod");
            await tx.Commit();
        }

        Loadout.ReadOnly? receivedLoadout = null;
        var folderAtHook = new Dictionary<string, string>();
        HookStubbedGame.Hooks.PreflightActions = (_, loadout) =>
        {
            receivedLoadout = loadout;
            folderAtHook = FolderSnapshot();
            throw new InvalidOperationException("refused");
        };

        var tree = await Synchronizer.BuildSyncTree(Refresh(applied));
        Synchronizer.ProcessSyncTree(tree);
        var act = async () => await Synchronizer.RunActions(tree, Refresh(applied));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("refused");
        receivedLoadout.Should().NotBeNull();
        folderAtHook.Should().BeEquivalentTo(folderBefore, "nothing was written before the hook ran");
        FolderSnapshot().Should().BeEquivalentTo(folderBefore);
        DiskStateSnapshot(applied).Should().Equal(stateBefore);
    }

    [Fact]
    public async Task APreflightActionsRefusalOnTheInstallationOverloadLeavesDiskAndDiskStateAsTheyWere()
    {
        var applied = await LoadoutWith("plain.txt");
        await Synchronizer.Synchronize(applied);
        var folderBefore = FolderSnapshot();
        var stateBefore = DiskStateSnapshot(applied);

        var hookRan = false;
        Loadout.ReadOnly? receivedLoadout = default;
        HookStubbedGame.Hooks.PreflightActions = (_, loadout) =>
        {
            hookRan = true;
            receivedLoadout = loadout;
            throw new InvalidOperationException("refused");
        };

        // An empty desired state would delete everything the loadout deployed if the refusal did not come first.
        var act = async () => await Base.RunActions(new Dictionary<GamePath, SyncNode>(), GameInstallation);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("refused");
        hookRan.Should().BeTrue();
        receivedLoadout.Should().BeNull("the installation overload has no loadout");
        FolderSnapshot().Should().BeEquivalentTo(folderBefore);
        DiskStateSnapshot(applied).Should().Equal(stateBefore);
    }

    [Fact]
    public async Task SynchronizeCallsPreflightLoadoutBeforeAnythingIsWritten()
    {
        var loadout = await LoadoutWith("plain.txt");
        var txBefore = Connection.Db.BasisTxId;
        TxId? txAtHook = null;
        HookStubbedGame.Hooks.PreflightLoadout = _ =>
        {
            txAtHook = Connection.Db.BasisTxId;
            return Task.CompletedTask;
        };

        await Synchronizer.Synchronize(loadout);

        HookStubbedGame.Hooks.Calls.Should().Contain("PreflightLoadout");
        txAtHook.Should().Be(txBefore, "locator ids and the re-index write to the database after the hook");
    }

    [Fact]
    public async Task PreflightLoadoutSeesTheLoadoutAsItIsNotTheCallersSnapshot()
    {
        var stale = await LoadoutWith("plain.txt");
        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, [(RelativePath)"extra.txt"], stale.LoadoutId, "mod added after the snapshot");
            await tx.Commit();
        }

        var current = Connection.Db.BasisTxId;
        TxId? seen = null;
        HookStubbedGame.Hooks.PreflightLoadout = loadout =>
        {
            seen = loadout.Db.BasisTxId;
            return Task.CompletedTask;
        };

        await Synchronizer.Synchronize(stale);

        seen.Should().Be(current);
    }

    [Fact]
    public async Task ARefusedSwitchLeavesThePreviousLoadoutApplied()
    {
        var loadoutA = await LoadoutWith("plain.txt");
        await Synchronizer.Synchronize(loadoutA);
        var loadoutB = await LoadoutWith("other.txt");

        var folderBefore = FolderSnapshot();
        folderBefore.Should().ContainKey("plain.txt");
        var txBefore = Connection.Db.BasisTxId;
        HookStubbedGame.Hooks.PreflightLoadout = loadout =>
            loadout.Id == loadoutB.Id ? throw new InvalidOperationException("refused") : Task.CompletedTask;

        var act = async () => await Synchronizer.Synchronize(Refresh(loadoutB));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("refused");
        FolderSnapshot().Should().BeEquivalentTo(folderBefore, "A was not deactivated");
        Sdk.Games.GameInstallMetadata.LastSyncedLoadout.TryGetValue(Refresh(loadoutA).Installation, out var lastSynced).Should().BeTrue();
        lastSynced.Should().Be(loadoutA.Id);
        Connection.Db.BasisTxId.Should().Be(txBefore, "no locator id update, re-index or deactivation ran");
    }

    // Keeps one loadout path off disk: a copy that differs from the loadout is backed up and removed, one that
    // matches is removed, and an absent one needs nothing.
    private static void KeepOffDisk(Dictionary<GamePath, SyncNode> tree)
    {
        if (!tree.TryGetValue(KeptOffDisk, out var node) || !node.HaveLoadout) return;

        node.Actions = node.HaveDisk
            ? node.Disk.Hash != node.Loadout.Hash ? Actions.BackupFile | Actions.DeleteFromDisk : Actions.DeleteFromDisk
            : Actions.DoNothing;
        tree[KeptOffDisk] = node;
    }

    [Fact]
    public async Task AHandEditedCopyOfAKeptOffPathIsBackedUpAndRemovedWithoutIngest()
    {
        HookStubbedGame.Hooks.Adjust = KeepOffDisk;
        var loadout = await LoadoutWith("kept-off-disk.txt");
        var itemId = (await Synchronizer.BuildSyncTree(loadout))[KeptOffDisk].Loadout.EntityId;
        var path = GameInstallation.Locations.ToAbsolutePath(KeptOffDisk);
        path.Parent.CreateDirectory();
        await path.WriteAllTextAsync("hand edited");

        await Synchronizer.Synchronize(loadout);

        path.FileExists.Should().BeFalse();
        (await FileStore.HaveFile(HashOf("hand edited"))).Should().BeTrue("the edit is kept in the file store");

        var tree = await Synchronizer.BuildSyncTree(Refresh(loadout));
        tree[KeptOffDisk].Loadout.Hash.Should().Be(HashOf("kept-off-disk.txt"), "the edit was not ingested");
        tree[KeptOffDisk].Loadout.EntityId.Should().Be(itemId);
    }

    [Fact]
    public async Task AKeptOffPathReachesASteadyStateAndTheLoadoutItemSurvives()
    {
        HookStubbedGame.Hooks.Adjust = KeepOffDisk;
        var loadout = await LoadoutWith("kept-off-disk.txt");
        var path = GameInstallation.Locations.ToAbsolutePath(KeptOffDisk);
        path.Parent.CreateDirectory();
        await path.WriteAllTextAsync("hand edited");
        await Synchronizer.Synchronize(loadout);

        var tree = await Synchronizer.BuildSyncTree(Refresh(loadout));
        Synchronizer.ProcessSyncTree(tree);

        var node = tree[KeptOffDisk];
        node.HaveDisk.Should().BeFalse();
        node.HavePrevious.Should().BeFalse("a leftover disk state entry would read as a user delete next time");
        node.HaveLoadout.Should().BeTrue();
        node.Actions.Should().Be(Actions.DoNothing);

        var item = LoadoutItem.Load(Connection.Db, node.Loadout.EntityId);
        item.IsValid().Should().BeTrue();
        item.IsEnabled().Should().BeTrue();

        // This game has no intrinsic file; one would always leave a WriteIntrinsic node and keep this true.
        var metadata = Refresh(loadout).Installation;
        Base.ShouldSynchronize(Refresh(loadout), Synchronizer.GetLastScannedDiskState(metadata), Synchronizer.GetDiskStateForGame(metadata))
            .Should().BeFalse();
    }
}

/// <summary>
/// A stubbed game whose synchronizer forwards the protected hooks to delegates a test sets.
/// </summary>
public class HookStubbedGame : IGame, IGameData<HookStubbedGame>
{
    public static GameId GameId { get; } = GameId.From("HookStubbedGame");
    public static string DisplayName => "Stubbed Game With Synchronizer Hooks";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(uint.MaxValue - 3);

    /// <summary>What the next synchronizer calls do; reset by each test.</summary>
    public static class Hooks
    {
        public static List<string> Calls { get; private set; } = [];
        public static Action<Dictionary<GamePath, SyncNode>>? Adjust { get; set; }
        public static Func<Dictionary<GamePath, SyncNode>, Loadout.ReadOnly?, Task>? PreflightActions { get; set; }
        public static Func<Loadout.ReadOnly, Task>? PreflightLoadout { get; set; }

        public static void Reset()
        {
            Calls = [];
            Adjust = null;
            PreflightActions = null;
            PreflightLoadout = null;
        }
    }

    public StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        SteamAppIds = [46u],
        GOGProductIds = [46L],
        EGSCatalogItemId = ["epic-game-id-synchronizer-hooks"],
    };

    public IStreamFactory IconImage { get; } = new EmbeddedResourceStreamFactory<NexusMods.StandardGameLocators.TestHelpers.StubbedGames.StubbedGame>("NexusMods.StandardGameLocators.TestHelpers.Resources.question_mark_game.png");
    public IStreamFactory TileImage => throw new NotImplementedException("No game image for stubbed game.");

    private readonly IServiceProvider _serviceProvider;
    public HookStubbedGame(IServiceProvider provider) => _serviceProvider = provider;

    public GamePath GetPrimaryFile(GameInstallation installation) => new(LocationId.Game, "");

    public ILoadoutSynchronizer Synchronizer => new HookSynchronizer(_serviceProvider);

    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
    {
        return new Dictionary<LocationId, AbsolutePath>
        {
            { LocationId.Game, gameLocatorResult.Path.Combine("game") },
        }.ToImmutableDictionary();
    }

    public ILibraryItemInstaller[] LibraryItemInstallers => [];
    public IDiagnosticEmitter[] DiagnosticEmitters => [];
    public ISortOrderManager SortOrderManager => new SortOrderManager(_serviceProvider);

    private sealed class HookSynchronizer(IServiceProvider provider) : DefaultSynchronizer(provider)
    {
        protected override void AdjustActions(Dictionary<GamePath, SyncNode> tree)
        {
            Hooks.Calls.Add("AdjustActions");
            Hooks.Adjust?.Invoke(tree);
        }

        protected override Task PreflightLoadout(Loadout.ReadOnly loadout)
        {
            Hooks.Calls.Add("PreflightLoadout");
            return Hooks.PreflightLoadout?.Invoke(loadout) ?? Task.CompletedTask;
        }

        protected override Task PreflightActions(Dictionary<GamePath, SyncNode> tree, Loadout.ReadOnly? loadout)
        {
            Hooks.Calls.Add("PreflightActions");
            return Hooks.PreflightActions?.Invoke(tree, loadout) ?? Task.CompletedTask;
        }
    }
}
