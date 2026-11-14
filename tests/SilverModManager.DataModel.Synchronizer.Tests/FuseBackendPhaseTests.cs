using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.TestFramework;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Deployment.Fuse.Backend;
using SilverModManager.Deployment.Vfs;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// What an apply does for a root on the fuse backend: it records rows and leaves the real folder as it found it.
/// The stubbed game's Data folder holds one vanilla file, <c>vanilla.esp</c>.
/// </summary>
public class FuseBackendPhaseTests(ITestOutputHelper helper) : AStubbedGameTest<FuseBackendPhaseTests>(helper)
{
    private static readonly GamePath DataRoot = new(LocationId.Game, "Data");
    private static readonly GamePath Vanilla = DataRoot / "vanilla.esp";
    private static readonly GamePath ModEsp = DataRoot / "mod.esp";

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services).AddUsableFuseProbe();

    private GameInstallMetadataId MetadataId => GameRegistry.ForceGetMetadata(GameInstallation).Id;

    private AbsolutePath OnDisk(GamePath path) => GameInstallation.Locations.ToAbsolutePath(path);

    private async Task<Loadout.ReadOnly> NewLoadoutOnFuse()
    {
        // The first sync of a loadout is a switch, which treats whatever is on disk as the loadout's own baseline.
        var loadout = await Synchronizer.Synchronize(await CreateLoadout());
        OnDisk(DataRoot).CreateDirectory();
        using var tx = Connection.BeginTransaction();
        ServiceProvider.GetRequiredService<IDeploymentPlanStore>().Write(tx, Connection.Db, MetadataId, DataRoot, DeploymentBackendId.Fuse, chosen: true);
        await tx.Commit();
        return Refresh(loadout);
    }

    // The stub has no baseline of vanilla files, so one is the loadout's own: a file that was on disk, taken into a
    // mod group at the lowest priority.
    private async Task<Loadout.ReadOnly> AddVanillaFile(Loadout.ReadOnly loadout)
    {
        await OnDisk(Vanilla).WriteAllTextAsync("vanilla bytes");
        loadout = await Apply(loadout);
        await MoveIntoAMod(loadout, "vanilla.esp");
        return Refresh(loadout);
    }

    private async Task<LoadoutItemGroupId> AddMod(Loadout.ReadOnly loadout, string name, GamePath path, string content, int priority = 1)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, name);
        AddFile(tx, loadout.LoadoutId, group, path, content, out var identity, out var size);
        _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = group, Priority = ConflictPriority.From((ulong)priority) };
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(path.FileName, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content))), identity, size)]);
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[group.Value]);
    }

    private async Task DisableMod(LoadoutItemGroupId group) => await DisableItem(group.Value);

    private async Task<Loadout.ReadOnly> Apply(Loadout.ReadOnly loadout) => await Synchronizer.Synchronize(Refresh(loadout));

    private async Task<bool> HasPendingWork(Loadout.ReadOnly loadout)
    {
        var metadata = GameRegistry.ForceGetMetadata(GameInstallation);
        return await Synchronizer.ShouldSynchronize(Refresh(loadout), Synchronizer.GetLastScannedDiskState(metadata), Synchronizer.GetDiskStateForGame(metadata));
    }

    private List<MountedFileEntry.ReadOnly> Rows()
    {
        return MountedFileEntry.FindByInstallation(Connection.Db, MetadataId).ToList();
    }

    private static FileIdentity IdentityOf(string content) => FileIdentity.XxHash3(System.Text.Encoding.UTF8.GetBytes(content).xxHash3());

    private FileIdentity RealIdentity(GamePath path) => FileIdentity.XxHash3(File.ReadAllBytes(OnDisk(path).ToString()).xxHash3());

    private IEnumerable<string> RealFolder() => Directory.EnumerateFileSystemEntries(OnDisk(DataRoot).ToString()).Select(Path.GetFileName).Order()!;

    [Fact]
    public async Task ANewModFileRecordsARowAndWritesNothing()
    {
        var loadout = await NewLoadoutOnFuse();
        await AddMod(loadout, "Mod", ModEsp, "mod bytes");
        var before = RealFolder().ToList();

        loadout = await Apply(loadout);

        var row = Rows().Should().ContainSingle().Which;
        ((GamePath)row.Path).Should().Be(ModEsp);
        ((GamePath)row.Root).Should().Be(DataRoot);
        row.Identity.Value.Should().Be(IdentityOf("mod bytes"));
        row.BackingIdentity.HasValue.Should().BeFalse("nothing was under the path");
        RealFolder().Should().Equal(before, "nothing is written under a fuse root");
        (await HasPendingWork(loadout)).Should().BeFalse();
    }

    [Fact]
    public async Task AModShadowingAVanillaFileRecordsWhatItCovers()
    {
        var loadout = await AddVanillaFile(await NewLoadoutOnFuse());
        var vanilla = RealIdentity(Vanilla);
        await AddMod(loadout, "Mod", Vanilla, "shadow bytes", priority: 10);

        loadout = await Apply(loadout);

        var row = Rows().Should().ContainSingle().Which;
        row.Identity.Value.Should().Be(IdentityOf("shadow bytes"));
        row.BackingIdentity.Value.Should().Be(vanilla);
        RealIdentity(Vanilla).Should().Be(vanilla, "the vanilla file stays in the folder");
        (await HasPendingWork(loadout)).Should().BeFalse();
    }

    [Fact]
    public async Task AModReplacedByAnotherModKeepsTheVanillaBacking()
    {
        var loadout = await AddVanillaFile(await NewLoadoutOnFuse());
        var vanilla = RealIdentity(Vanilla);
        await AddMod(loadout, "First", Vanilla, "first bytes", priority: 10);
        loadout = await Apply(loadout);
        await AddMod(loadout, "Second", Vanilla, "second bytes", priority: 20);

        loadout = await Apply(loadout);

        var row = Rows().Should().ContainSingle().Which;
        row.Identity.Value.Should().Be(IdentityOf("second bytes"));
        row.BackingIdentity.Value.Should().Be(vanilla);
        (await HasPendingWork(loadout)).Should().BeFalse();
    }

    [Fact]
    public async Task RemovingAShadowingModRetractsTheRow()
    {
        var loadout = await AddVanillaFile(await NewLoadoutOnFuse());
        var vanilla = RealIdentity(Vanilla);
        var mod = await AddMod(loadout, "Mod", Vanilla, "shadow bytes", priority: 10);
        loadout = await Apply(loadout);

        await DisableMod(mod);
        loadout = await Apply(loadout);

        Rows().Should().BeEmpty("the real file serves the path again");
        RealIdentity(Vanilla).Should().Be(vanilla);
        (await HasPendingWork(loadout)).Should().BeFalse();
    }

    [Fact]
    public async Task RemovingAModWithNothingUnderneathRetractsTheRow()
    {
        var loadout = await NewLoadoutOnFuse();
        var mod = await AddMod(loadout, "Mod", ModEsp, "mod bytes");
        loadout = await Apply(loadout);
        Rows().Should().HaveCount(1);

        await DisableMod(mod);
        loadout = await Apply(loadout);

        Rows().Should().BeEmpty();
        (await HasPendingWork(loadout)).Should().BeFalse();
    }

    [Fact]
    public async Task ALoadoutDeleteOverAVanillaFileRecordsAHiddenRow()
    {
        var loadout = await AddVanillaFile(await NewLoadoutOnFuse());
        var vanilla = RealIdentity(Vanilla);
        using (var tx = Connection.BeginTransaction())
        {
            var group = AddEmptyGroup(tx, loadout.LoadoutId, "Deleter");
            _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = group, Priority = ConflictPriority.From(10) };
            _ = new DeletedFile.New(tx, out var id)
            {
                Reason = "Reified delete",
                LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, id)
                {
                    TargetPath = Vanilla.ToGamePathParentTuple(loadout.LoadoutId),
                    LoadoutItem = new LoadoutItem.New(tx, id) { LoadoutId = loadout.LoadoutId, Name = Vanilla.Path, ParentId = group },
                },
            };
            await tx.Commit();
        }

        loadout = await Apply(loadout);

        var row = Rows().Should().ContainSingle().Which;
        ((GamePath)row.Path).Should().Be(Vanilla);
        row.Hidden.Value.Should().BeTrue();
        row.BackingIdentity.Value.Should().Be(vanilla);
        RealIdentity(Vanilla).Should().Be(vanilla, "the real file is not deleted");
        (await HasPendingWork(loadout)).Should().BeFalse();
    }

    [Fact]
    public async Task AFileAppearingInTheRealFolderIsIngestedAndItsRowRetracted()
    {
        var loadout = await NewLoadoutOnFuse();
        await AddMod(loadout, "Mod", ModEsp, "mod bytes");
        loadout = await Apply(loadout);
        Rows().Should().HaveCount(1);

        // A tool run outside the app wrote the file the row is for.
        await OnDisk(ModEsp).WriteAllTextAsync("tool bytes");
        loadout = await Apply(loadout);

        Rows().Should().BeEmpty("the real file is the loadout's now");
        InOverrides(loadout, ModEsp).Should().BeTrue();
        (await HasPendingWork(loadout)).Should().BeFalse();
    }

    [Fact]
    public async Task AFileAppearingWithNoRowIsIngestedFromTheRealFolder()
    {
        var loadout = await NewLoadoutOnFuse();
        await OnDisk(ModEsp).WriteAllTextAsync("tool bytes");

        loadout = await Apply(loadout);

        InOverrides(loadout, ModEsp).Should().BeTrue();
        Rows().Should().BeEmpty();
        (await HasPendingWork(loadout)).Should().BeFalse();
    }

    [Fact]
    public async Task AChangedRealFileUnderARowIsIngested()
    {
        var loadout = await AddVanillaFile(await NewLoadoutOnFuse());
        await AddMod(loadout, "Mod", Vanilla, "shadow bytes", priority: 10);
        loadout = await Apply(loadout);
        Rows().Should().HaveCount(1);

        await OnDisk(Vanilla).WriteAllTextAsync("changed from outside");
        loadout = await Apply(loadout);

        Rows().Should().BeEmpty();
        InOverrides(loadout, Vanilla).Should().BeTrue();
        (await HasPendingWork(loadout)).Should().BeFalse();
    }

    [Fact]
    public async Task AVanillaWinnerRetractsTheRowBecauseTheRealFileServesIt()
    {
        // The stub has no baseline, so a vanilla winner is handed to the phase directly.
        var loadout = await AddVanillaFile(await NewLoadoutOnFuse());
        await AddMod(loadout, "Mod", Vanilla, "shadow bytes", priority: 10);
        loadout = await Apply(loadout);
        Rows().Should().HaveCount(1);
        var backend = ServiceProvider.GetServices<IDeploymentBackend>().OfType<FuseDeploymentBackend>().Single();
        var vanilla = RealIdentity(Vanilla);

        using var tx = Connection.BeginTransaction();
        var node = new SyncNode
        {
            SourceItemType = LoadoutSourceItemType.Game,
            Actions = Actions.ExtractToDisk,
            Loadout = new SyncNodePart { Identity = vanilla, Size = Size.FromLong(13), LastModifiedTicks = 0 },
            Disk = new SyncNodePart { Identity = IdentityOf("shadow bytes"), Size = Size.FromLong(12), LastModifiedTicks = 0 },
        };
        var tree = new Dictionary<GamePath, SyncNode> { [Vanilla] = node };
        var context = new DeploymentContext
        {
            Synchronizer = (ALoadoutSynchronizer)Synchronizer,
            Installation = GameInstallation,
            MetadataId = MetadataId,
            SyncTree = tree,
            Loadout = loadout,
            Transaction = tx,
        };
        await backend.Extract(context, tree);
        await tx.Commit();

        Rows().Should().BeEmpty();
        RealIdentity(Vanilla).Should().Be(vanilla);
    }

    private bool InOverrides(Loadout.ReadOnly loadout, GamePath path)
    {
        var overrides = OverridesGroupLookup.Find(Refresh(loadout));
        return LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemWithTargetPath()
            .Any(f => f.TargetPath == path && OverridesGroupLookup.IsInGroup(Connection.Db, f.Id, overrides));
    }
}
