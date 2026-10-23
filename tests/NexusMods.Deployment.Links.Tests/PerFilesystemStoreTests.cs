using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Deployment.Links.Tests.Fixtures;
using NexusMods.Deployment.Staging;
using NexusMods.DataModel.Synchronizer;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Native;

namespace NexusMods.Deployment.Links.Tests;

/// <summary>
/// Reads devices from the real filesystems, but puts "the root of the other filesystem" in a directory the test
/// made, so no store ever lands at the top of a real mount.
/// </summary>
public sealed class FixedMountLocator(AbsolutePath mount) : IFilesystemLocator
{
    private readonly StatFilesystemLocator _real = new();

    public ulong? DeviceOf(AbsolutePath path) => _real.DeviceOf(path);

    public AbsolutePath? RootOf(AbsolutePath path) => _real.DeviceOf(path) is null ? null : mount;
}

/// <summary>
/// A game on btrfs and the default store on tmpfs: two filesystems, so the only way to a reflink is a store
/// next to the game.
/// </summary>
public class ReflinkThroughAGameFilesystemStoreTests(ITestOutputHelper helper)
    : LinkedGameTest<ReflinkThroughAGameFilesystemStoreTests>(helper, Placement.EntryDirectory, Placement.TempDirectory, pinned: null)
{
    private readonly AbsolutePath _mount = PlacementDirectory(Placement.EntryDirectory);

    protected override bool PerFilesystemStores => true;

    protected override IFilesystemLocator FilesystemLocator => new FixedMountLocator(_mount);

    private AbsolutePath GameFilesystemStore => _mount.Combine(".silvermodmanager/staging");

    [Fact]
    public async Task Apply_ChoosesReflinkAndStagesOnTheGameFilesystem()
    {
        var loadout = await NewLoadout();
        var (_, hashes) = await AddMod(loadout, "Mod", ("a/x.txt", "bytes of the mod file"));

        await Synchronizer.Synchronize(loadout);

        Plans.Load(Connection.Db, MetadataId).Roots.Single(r => r.Root.LocationId == LocationId.Game).Backend.Should().Be(DeploymentBackendId.Reflink);
        IsSymlink(OnDisk("a/x.txt")).Should().BeFalse();
        File.ReadAllText(OnDisk("a/x.txt").ToString()).Should().Be("bytes of the mod file");
        var perFilesystem = Store.ForRoot(OnDisk(""));
        perFilesystem.Root.Should().Be(GameFilesystemStore);
        perFilesystem.Contains(MetadataId, hashes["a/x.txt"]).Should().BeTrue();
        Store.Contains(MetadataId, hashes["a/x.txt"]).Should().BeFalse();
    }

    [Fact]
    public async Task Sweep_KeepsLiveFilesAndDropsDeadOnesInBothStores()
    {
        var loadout = await NewLoadout();
        var (_, hashes) = await AddMod(loadout, "Mod", ("a/x.txt", "kept"), ("a/y.txt", "dropped"));
        await Synchronizer.Synchronize(loadout);
        var perFilesystem = Store.ForRoot(OnDisk(""));
        var defaultFile = await Store.Ensure(MetadataId, hashes["a/y.txt"], TestContext.Current.CancellationToken);

        var deleted = Store.Sweep(() => new Dictionary<GameInstallMetadataId, IReadOnlySet<Hash>>
        {
            [MetadataId] = new HashSet<Hash> { hashes["a/x.txt"] },
        });

        deleted.Should().Be(2);
        perFilesystem.Contains(MetadataId, hashes["a/x.txt"]).Should().BeTrue();
        perFilesystem.Contains(MetadataId, hashes["a/y.txt"]).Should().BeFalse();
        defaultFile.FileExists.Should().BeFalse();
    }
}

/// <summary>
/// Two tmpfs mounts: reflink is not possible, so the hard link through the second store is the best there is.
/// </summary>
public class HardlinkThroughAGameFilesystemStoreTests(ITestOutputHelper helper)
    : LinkedGameTest<HardlinkThroughAGameFilesystemStoreTests>(helper, Placement.SharedMemory, Placement.TempDirectory, pinned: null)
{
    private readonly AbsolutePath _mount = PlacementDirectory(Placement.SharedMemory);

    protected override bool PerFilesystemStores => true;

    protected override IFilesystemLocator FilesystemLocator => new FixedMountLocator(_mount);

    [Fact]
    public async Task Apply_HardlinksFromTheStoreOnTheGameFilesystem()
    {
        var loadout = await NewLoadout();
        var (_, hashes) = await AddMod(loadout, "Mod", ("a/x.txt", "bytes of the mod file"));

        await Synchronizer.Synchronize(loadout);

        Plans.Load(Connection.Db, MetadataId).Roots.Single(r => r.Root.LocationId == LocationId.Game).Backend.Should().Be(DeploymentBackendId.Hardlink);
        var staged = Store.ForRoot(OnDisk("")).PathFor(MetadataId, hashes["a/x.txt"]);
        staged.InFolder(_mount).Should().BeTrue();
        Stat(OnDisk("a/x.txt")).Inode.Should().Be(Stat(staged).Inode);
    }
}

/// <summary>
/// A game folder already deployed by symlink keeps working, and moves to a reflink through the game filesystem's
/// store the next time its backend is re-chosen.
/// </summary>
public class SymlinkToReflinkMigrationTests(ITestOutputHelper helper)
    : LinkedGameTest<SymlinkToReflinkMigrationTests>(helper, Placement.EntryDirectory, Placement.TempDirectory, pinned: null)
{
    private readonly AbsolutePath _mount = PlacementDirectory(Placement.EntryDirectory);

    protected override bool PerFilesystemStores => true;

    protected override IFilesystemLocator FilesystemLocator => new FixedMountLocator(_mount);

    [Fact]
    public async Task ChangingFromSymlinkToReflink_RedeploysFromTheGameFilesystemStore()
    {
        var loadout = await NewLoadout();
        var (_, hashes) = await AddMod(loadout, "Mod", ("a/x.txt", "bytes of the mod file"));
        await RecordGameRoot(DeploymentBackendId.Symlink);
        await Synchronizer.Synchronize(loadout);
        IsSymlink(OnDisk("a/x.txt")).Should().BeTrue();
        Store.Contains(MetadataId, hashes["a/x.txt"]).Should().BeTrue();

        var service = ServiceProvider.GetRequiredService<SynchronizerService>();
        await service.ChangeDeploymentBackend(GameInstallation, GamePathOf(""), DeploymentBackendId.Reflink, chosen: false);

        IsSymlink(OnDisk("a/x.txt")).Should().BeFalse();
        File.ReadAllText(OnDisk("a/x.txt").ToString()).Should().Be("bytes of the mod file");
        Store.ForRoot(OnDisk("")).Contains(MetadataId, hashes["a/x.txt"]).Should().BeTrue();
    }
}
