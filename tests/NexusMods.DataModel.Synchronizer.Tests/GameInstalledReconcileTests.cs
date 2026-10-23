using System.Reactive.Linq;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Games.FileHashes;
using NexusMods.Abstractions.Games.FileHashes.Models;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.Loadouts.Synchronizers.Deployment;
using NexusMods.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.ElementComparers;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers;
using Xunit;

namespace NexusMods.DataModel.Synchronizer.Tests;

/// <summary>
/// Content the game installed itself is reconciled into the loadout before each sync and shows up in the sync
/// tree as game files, so the synchronizer never ingests, backs up or deletes it.
/// </summary>
public class GameInstalledReconcileTests : AIsolatedGameTest<GameInstalledReconcileTests, GameInstalledStubbedGame>
{
    private const string Source = "StubSource";
    private static readonly GamePath CreationA = Game("Data/alpha.esm");
    private static readonly GamePath CreationB = Game("Data/beta.esm");
    private static readonly GamePath PakOutsideTheGameFolder = new(LocationId.Preferences, "paks/gamma.pak");
    private static readonly GamePath CatalogFile = Game("Catalog/catalog.txt");

    public GameInstalledReconcileTests(ITestOutputHelper helper) : base(helper) => GameInstalledStubbedGame.Reset();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddGame<GameInstalledStubbedGame>()
            .AddUniversalGameLocator<GameInstalledStubbedGame>(Version.Parse("0.0.0"));
    }

    private static GamePath Game(string path) => new(LocationId.Game, path);

    private static FakeGameInstalledRecognizer Recognizer => GameInstalledStubbedGame.Recognizer;

    private GameInstalledStubbedGame.GameInstalledSynchronizer Sync => (GameInstalledStubbedGame.GameInstalledSynchronizer)Synchronizer;

    private AbsolutePath OnDisk(GamePath path) => GameInstallation.Locations.ToAbsolutePath(path);

    private async Task WriteOnDisk(GamePath path, string text)
    {
        var file = OnDisk(path);
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync(text);
    }

    private static Hash HashOf(string text) => Encoding.UTF8.GetBytes(text).xxHash3();

    private static void Recognise(string sourceId, params GamePath[] files)
    {
        Recognizer.Items.RemoveAll(item => item.SourceId == sourceId);
        Recognizer.Items.Add(new GameInstalledContent(Source, sourceId, sourceId, Version: null, GameInstalledConfidence.GameRecord, CanBeClaimed: true, files));
    }

    private async Task<Loadout.ReadOnly> Apply(Loadout.ReadOnly loadout) => await Synchronizer.Synchronize(Refresh(loadout));

    private async Task<Dictionary<GamePath, SyncNode>> TreeOf(Loadout.ReadOnly loadout)
    {
        var tree = await Synchronizer.BuildSyncTree(Refresh(loadout));
        Synchronizer.ProcessSyncTree(tree);
        return tree;
    }

    private IReadOnlyDictionary<GamePath, (Hash Hash, Size Size)> Installed(Loadout.ReadOnly loadout) =>
        GameInstalledPaths.For(Connection.Db, loadout.LoadoutId);

    private GameInstalledItem.ReadOnly[] Items(Loadout.ReadOnly loadout) =>
        GameInstalledGroup.FindByInstalledFor(Connection.Db, loadout.Id)
            .SelectMany(group => group.AsLoadoutItemGroup().Children.OfTypeLoadoutItemGroup().OfTypeGameInstalledItem())
            .ToArray();

    private string[] OverridesPaths(Loadout.ReadOnly loadout) =>
        LoadoutOverridesGroup.FindByOverridesFor(Connection.Db, loadout.Id)
            .SelectMany(group => group.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath())
            .Select(item => ((GamePath)item.TargetPath).Path.ToString())
            .ToArray();

    [Fact]
    public async Task ARecognisedFileIsLeftAloneOnTheFirstSync()
    {
        var loadout = await CreateLoadout();
        await WriteOnDisk(CreationA, "alpha 1");
        Recognise("alpha", CreationA);

        await Apply(loadout);

        var tree = await TreeOf(loadout);
        tree[CreationA].Actions.Should().Be(Actions.DoNothing);
        tree[CreationA].SourceItemType.Should().Be(LoadoutSourceItemType.Game);
        Installed(loadout).Should().ContainKey(CreationA).WhoseValue.Hash.Should().Be(HashOf("alpha 1"));
        OverridesPaths(Refresh(loadout)).Should().BeEmpty("the game owns the file, it is not an external change");
        (await FileStore.HaveFile(HashOf("alpha 1"))).Should().BeFalse("nothing of the game's content is backed up");
        OnDisk(CreationA).FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task AFileTheGameChangesStaysUntouched()
    {
        var loadout = await CreateLoadout();
        await WriteOnDisk(CreationA, "alpha 1");
        Recognise("alpha", CreationA);
        await Apply(loadout);

        await WriteOnDisk(CreationA, "alpha 2, updated by the game");
        await Apply(loadout);

        var tree = await TreeOf(loadout);
        tree[CreationA].Actions.Should().Be(Actions.DoNothing);
        Installed(loadout)[CreationA].Hash.Should().Be(HashOf("alpha 2, updated by the game"), "the row follows the disk");
        OverridesPaths(Refresh(loadout)).Should().BeEmpty();
        (await FileStore.HaveFile(HashOf("alpha 2, updated by the game"))).Should().BeFalse();
        (await OnDisk(CreationA).ReadAllTextAsync()).Should().Be("alpha 2, updated by the game");
    }

    [Fact]
    public async Task AFileTheGameRemovesLeavesNoTrace()
    {
        var loadout = await CreateLoadout();
        await WriteOnDisk(CreationA, "alpha 1");
        Recognise("alpha", CreationA);
        await Apply(loadout);

        OnDisk(CreationA).Delete();
        await Apply(loadout);

        Installed(loadout).Should().BeEmpty();
        Items(Refresh(loadout)).Should().BeEmpty("an item with no file left is dropped");
        OverridesPaths(Refresh(loadout)).Should().BeEmpty();
        var tree = await TreeOf(loadout);
        tree.Should().NotContainKey(CreationA);
    }

    [Fact]
    public async Task AFileThatStopsBeingRecognisedMovesToTheOverridesWithItsBytesBackedUp()
    {
        var loadout = await CreateLoadout();
        await WriteOnDisk(CreationA, "alpha 1");
        Recognise("alpha", CreationA);
        await Apply(loadout);

        Recognizer.Items.Clear();
        await Apply(loadout);

        OnDisk(CreationA).FileExists.Should().BeTrue("leaving the group never deletes a file");
        Installed(loadout).Should().BeEmpty();
        OverridesPaths(Refresh(loadout)).Should().Contain(CreationA.Path.ToString());
        (await FileStore.HaveFile(HashOf("alpha 1"))).Should().BeTrue("the overrides row has an archive like any ingested file");
        (await TreeOf(loadout))[CreationA].Actions.Should().Be(Actions.DoNothing);
    }

    [Fact]
    public async Task AFileThatStopsBeingRecognisedKeepsNoArchiveWhenBackupsAreIgnoredForIt()
    {
        var loadout = await CreateLoadout();
        await WriteOnDisk(CreationA, "alpha 1");
        Recognise("alpha", CreationA);
        GameInstalledStubbedGame.IgnoredBackups.Add(CreationA);
        await Apply(loadout);

        Recognizer.Items.Clear();
        await Apply(loadout);

        OnDisk(CreationA).FileExists.Should().BeTrue();
        OverridesPaths(Refresh(loadout)).Should().Contain(CreationA.Path.ToString());
        (await FileStore.HaveFile(HashOf("alpha 1"))).Should().BeFalse("the game excludes this path from backups");
    }

    [Fact]
    public async Task ARecordFileThatWasIngestedIsKeptOnDiskAndLeavesTheOverrides()
    {
        var loadout = await CreateLoadout();
        await WriteOnDisk(CatalogFile, "catalog 1");
        await Apply(loadout);
        OverridesPaths(Refresh(loadout)).Should().Contain(CatalogFile.Path.ToString(), "before the game's records are known the file is an external change");

        Recognizer.Record.Add(CatalogFile);
        await Apply(loadout);

        OverridesPaths(Refresh(loadout)).Should().NotContain(CatalogFile.Path.ToString());
        OnDisk(CatalogFile).FileExists.Should().BeTrue();
        (await TreeOf(loadout))[CatalogFile].Actions.Should().NotHaveFlag(Actions.DeleteFromDisk);
    }

    [Fact]
    public async Task ARecordFileTheGameRewritesIsForgottenAndThenLeftOutOfTheIndex()
    {
        var loadout = await CreateLoadout();
        await WriteOnDisk(CatalogFile, "catalog 1");
        await Apply(loadout);
        Recognizer.Record.Add(CatalogFile);
        await Apply(loadout);

        await WriteOnDisk(CatalogFile, "catalog 2, rewritten by the game");
        var rewritten = await TreeOf(loadout);
        rewritten[CatalogFile].Actions.Should().Be(Actions.ForgetDiskState);
        await Apply(loadout);

        var after = await TreeOf(loadout);
        after.Should().NotContainKey(CatalogFile, "a filtered path that nothing tracks is not indexed");
        OnDisk(CatalogFile).FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task AnUnreadableRecordChangesNothing()
    {
        var loadout = await CreateLoadout();
        await WriteOnDisk(CreationA, "alpha 1");
        Recognise("alpha", CreationA);
        await Apply(loadout);

        Recognizer.Readable = false;
        await Apply(loadout);

        Installed(loadout).Should().ContainKey(CreationA);
        Items(Refresh(loadout)).Should().ContainSingle();
        OverridesPaths(Refresh(loadout)).Should().BeEmpty();
        OnDisk(CreationA).FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task AReconcileThatFindsNothingNewCommitsNothing()
    {
        var loadout = await CreateLoadout();
        await WriteOnDisk(CreationA, "alpha 1");
        Recognise("alpha", CreationA);
        await Apply(loadout);
        var metadata = GameInstallMetadata.Load(Connection.Db, loadout.InstallationId);
        var before = Connection.Db.BasisTxId;

        var result = await Sync.Reconcile(Refresh(loadout), metadata);

        Connection.Db.BasisTxId.Should().Be(before);
        result.Db.BasisTxId.Should().Be(before);
    }

    [Fact]
    public async Task AFileInstalledAfterThePreviousApplyIsPickedUpByTheNextOne()
    {
        var loadout = await CreateLoadout();
        await Apply(loadout);
        await WriteOnDisk(CreationA, "alpha 1");
        Recognise("alpha", CreationA);

        await Apply(loadout);

        Items(Refresh(loadout)).Should().ContainSingle();
        (await TreeOf(loadout))[CreationA].Actions.Should().Be(Actions.DoNothing);
        OverridesPaths(Refresh(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task TheReadOnlyPathsNeverReconcile()
    {
        var loadout = await CreateLoadout();
        await Apply(loadout);
        await WriteOnDisk(CreationA, "alpha 1");
        Recognise("alpha", CreationA);

        await Synchronizer.BuildSyncTree(Refresh(loadout));
        await DiagnosticManager.GetLoadoutDiagnostics(loadout.LoadoutId).Timeout(TimeSpan.FromSeconds(30)).FirstAsync();

        Installed(loadout).Should().BeEmpty("only an Apply or a loadout switch writes the reconcile");
        Items(Refresh(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task TheGameCanStoreItsOwnDataInsideTheReconcileTransaction()
    {
        var loadout = await CreateLoadout();
        await WriteOnDisk(CreationA, "alpha 1");
        var extra = new object();
        Recognizer.Items.Add(new GameInstalledContent(Source, "alpha", "alpha", null, GameInstalledConfidence.GameRecord, true, [CreationA], extra));

        await Apply(loadout);

        var call = GameInstalledStubbedGame.ExtrasCalls.Should().ContainSingle().Subject;
        call.SourceId.Should().Be("alpha");
        call.Extra.Should().BeSameAs(extra);
        call.ItemCommittedAlready.Should().BeFalse("the hook runs before the transaction commits");
        Items(Refresh(loadout)).Should().ContainSingle();
    }

    [Fact]
    public async Task OverridesRowsAtRecognisedAndRecordPathsAreRemovedWithTheDiskUntouched()
    {
        var loadout = await CreateLoadout();
        await WriteOnDisk(CreationA, "alpha 1");
        await WriteOnDisk(CatalogFile, "catalog 1");
        await Apply(loadout);
        await WriteOnDisk(CreationB, "beta 1");
        using (var tx = Connection.BeginTransaction())
        {
            var fresh = Refresh(loadout);
            var overrides = Sync.GetOrCreateOverridesGroup(tx, fresh);
            OverridesGroupItems.AddReifiedDelete(tx, fresh, overrides, CreationB);
            await tx.Commit();
        }
        OverridesPaths(Refresh(loadout)).Should().Contain([CreationA.Path.ToString(), CatalogFile.Path.ToString(), CreationB.Path.ToString()]);
        Recognise("alpha", CreationA);
        Recognise("beta", CreationB);
        Recognizer.Record.Add(CatalogFile);

        await Apply(loadout);

        OverridesPaths(Refresh(loadout)).Should().BeEmpty("a file, a reified delete and a record file are all the game's now");
        OnDisk(CreationA).FileExists.Should().BeTrue();
        OnDisk(CreationB).FileExists.Should().BeTrue("a reified delete of a recognised file loses");
        OnDisk(CatalogFile).FileExists.Should().BeTrue();
        Installed(loadout).Keys.Should().BeEquivalentTo([CreationA, CreationB]);
    }

    [Fact]
    public async Task AFileAModProvidesIsNotRetyped()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, [(RelativePath)CreationA.Path.ToString()], loadout.LoadoutId, "A mod");
            await tx.Commit();
        }
        await WriteOnDisk(CreationA, CreationA.Path.ToString());
        Recognise("alpha", CreationA);

        await Apply(loadout);

        Installed(loadout).Should().NotContainKey(CreationA, "the mod wins the path");
        var tree = await TreeOf(loadout);
        tree[CreationA].SourceItemType.Should().Be(LoadoutSourceItemType.Loadout);
    }

    [Fact]
    public async Task ADepotListedFileTheGameUpdatedIsStillTheGames()
    {
        var loadout = await CreateLoadout();
        await SeedDepotFile(loadout, CreationA);
        await WriteOnDisk(CreationA, "alpha updated by the game");
        Recognise("alpha", CreationA);

        await Apply(loadout);

        (await TreeOf(loadout))[CreationA].Actions.Should().Be(Actions.DoNothing);
        OverridesPaths(Refresh(loadout)).Should().BeEmpty();
        Items(Refresh(loadout)).Single().DepotShipped.Should().BeTrue();
        (await OnDisk(CreationA).ReadAllTextAsync()).Should().Be("alpha updated by the game");
    }

    [Fact]
    public async Task AFileTheGameInstalledIsNotMarkedAsDepotShipped()
    {
        var loadout = await CreateLoadout();
        await WriteOnDisk(CreationA, "alpha 1");
        Recognise("alpha", CreationA);

        await Apply(loadout);

        Items(Refresh(loadout)).Single().DepotShipped.Should().BeFalse();
    }

    [Fact]
    public async Task EachLoadoutKeepsItsOwnGroupSwitchAcrossASwitch()
    {
        var loadoutA = await CreateLoadout();
        await WriteOnDisk(PakOutsideTheGameFolder, "gamma 1");
        Recognise("gamma", PakOutsideTheGameFolder);
        await Apply(loadoutA);
        using (var tx = Connection.BeginTransaction())
        {
            var group = GameInstalledGroup.FindByInstalledFor(Connection.Db, loadoutA.Id).Single();
            tx.Add(group.Id, LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }

        var loadoutB = await CreateLoadout();
        await Apply(loadoutB);
        await Apply(loadoutA);

        var groupA = GameInstalledGroup.FindByInstalledFor(Connection.Db, loadoutA.Id).Single();
        var groupB = GameInstalledGroup.FindByInstalledFor(Connection.Db, loadoutB.Id).Single();
        groupA.AsLoadoutItemGroup().AsLoadoutItem().IsDisabled.Should().BeTrue();
        groupB.AsLoadoutItemGroup().AsLoadoutItem().IsDisabled.Should().BeFalse();
        Installed(loadoutA).Should().ContainKey(PakOutsideTheGameFolder);
        Installed(loadoutB).Should().ContainKey(PakOutsideTheGameFolder);
        OnDisk(PakOutsideTheGameFolder).FileExists.Should().BeTrue();
    }

    /// <summary>Records <paramref name="path"/> as a vanilla file of the installation, the way a local baseline holds it.</summary>
    private async Task SeedDepotFile(Loadout.ReadOnly loadout, GamePath path)
    {
        var local = ((StubbedFileHasherService)ServiceProvider.GetRequiredService<IFileHashesService>()).Local.Connection;
        var install = loadout.Installation;

        using var tx = local.BeginTransaction();
        var hashes = await MultiHasher.HashStream(new MemoryStream(Encoding.UTF8.GetBytes("vanilla " + path.Path)));
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
        var pathRow = new PathHashRelation.New(tx)
        {
            Path = path.Path,
            HashId = relation,
        };
        _ = new LocalBaseline.New(tx)
        {
            Store = install.Store,
            GamePath = install.Path,
            LocatorIds = [],
            Kind = LocalBaselineKind.Snapshot,
            VersionName = "",
            BuiltAt = DateTimeOffset.UnixEpoch,
            FilesIds = [pathRow.Id],
        };
        await tx.Commit();
    }
}
