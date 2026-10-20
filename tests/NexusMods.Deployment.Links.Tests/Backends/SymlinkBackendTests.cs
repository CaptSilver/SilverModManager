using NexusMods.Sdk.Loadouts;
using FluentAssertions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Deployment.Links.Backends;
using NexusMods.Deployment.Links.Native;
using NexusMods.Deployment.Links.Tests.Fixtures;
using NexusMods.Hashing.xxHash3;
using NexusMods.Hashing.xxHash3.Paths;
using NexusMods.MnemonicDB.Abstractions;

namespace NexusMods.Deployment.Links.Tests.Backends;

/// <summary>
/// Shared by the same-device and cross-device runs: a symlink works either way, and the repair must too.
/// </summary>
public abstract class SymlinkBackendTestsBase<TTest>(ITestOutputHelper helper, Placement game)
    : LinkedGameTest<TTest>(helper, game, Placement.TempDirectory, DeploymentBackendId.Symlink)
{
    protected const string Original = "bytes of the mod file";
    protected const string Edited = "bytes written by the game, longer than before";

    protected SymlinkDeploymentBackend Symlink => Backend<SymlinkDeploymentBackend>();

    protected async Task<(Loadout.ReadOnly Loadout, EntityId Group, Dictionary<string, Hash> Hashes)> Deploy(params (string Path, string Content)[] files)
    {
        var loadout = await NewLoadout();
        var (group, hashes) = await AddMod(loadout, "Mod", files);
        loadout = await Synchronizer.Synchronize(loadout);
        return (loadout, group, hashes);
    }

    [Fact]
    public async Task Ingest_AfterAWriteThroughTheSymlink_ReplacesTheLinkWithTheFileAndRepairsTheStore()
    {
        var (loadout, _, hashes) = await Deploy(("a/x.txt", Original));
        var hash = hashes["a/x.txt"];
        var path = OnDisk("a/x.txt");

        // A second apply with nothing to change: the edit below must be noticed by a scan that has already
        // seen the link once, not only by the first one after the deploy.
        loadout = await Synchronizer.Synchronize(loadout);
        await File.WriteAllTextAsync(path.ToString(), Edited);
        File.ReadAllText(Store.PathFor(MetadataId, hash).ToString()).Should().Be(Edited, "writing through the link is what damages the staged file");

        await Synchronizer.Synchronize(loadout);

        IsSymlink(path).Should().BeFalse("the written file replaces the link");
        File.ReadAllText(path.ToString()).Should().Be(Edited);
        Store.Contains(MetadataId, hash).Should().BeFalse();
        var restored = await Store.Ensure(MetadataId, hash);
        (await restored.XxHash3Async()).Should().Be(hash);
    }

    [Fact]
    public async Task Reset_AfterAWriteThroughTheSymlink_RepairsTheStore()
    {
        var (loadout, _, hashes) = await Deploy(("a/x.txt", Original));
        var hash = hashes["a/x.txt"];
        await Synchronizer.Synchronize(loadout);
        await File.WriteAllTextAsync(OnDisk("a/x.txt").ToString(), Edited);

        await Synchronizer.ResetToOriginalGameState(GameInstallation, GameInstallation.LocatorResult.LocatorIds.Distinct().ToArray());

        var restored = await Store.Ensure(MetadataId, hash);
        (await restored.XxHash3Async()).Should().Be(hash);
    }
}

public class SymlinkBackendTests(ITestOutputHelper helper) : SymlinkBackendTestsBase<SymlinkBackendTests>(helper, Placement.TempDirectory)
{
    [Fact]
    public async Task Extract_PointsAtTheStoreFileByAbsolutePath()
    {
        var (_, _, hashes) = await Deploy(("a/x.txt", Original));

        IsSymlink(OnDisk("a/x.txt")).Should().BeTrue();
        LibcLinks.TryReadLink(OnDisk("a/x.txt").ToString(), out var target).Should().Be(0);
        target.Should().Be(Store.PathFor(MetadataId, hashes["a/x.txt"]).ToString());
        File.ReadAllText(OnDisk("a/x.txt").ToString()).Should().Be(Original);
    }

    [Fact]
    public async Task Extract_ALaterApplyAddingAPathForASymlinkedHashGetsAPrivateCopy()
    {
        var (loadout, _, _) = await Deploy(("b/x.txt", Original));
        await AddMod(loadout, "Second mod", ("a/x.txt", Original));

        await Synchronizer.Synchronize(loadout);

        IsSymlink(OnDisk("b/x.txt")).Should().BeTrue("the first path keeps the link");
        IsSymlink(OnDisk("a/x.txt")).Should().BeFalse();
        File.ReadAllText(OnDisk("a/x.txt").ToString()).Should().Be(Original);
    }

    [Fact]
    public async Task Extract_LinksOnePathAndCopiesTheSecondForOneHash()
    {
        await Deploy(("a/x.txt", Original), ("b/x.txt", Original));

        new[] { "a/x.txt", "b/x.txt" }.Count(p => IsSymlink(OnDisk(p))).Should().Be(1);
    }

    [Fact]
    public async Task Delete_PurgesByTargetUnderTheStoreAndLeavesAForeignSymlink()
    {
        var (loadout, group, _) = await Deploy(("a/one.txt", "first file"), ("b/two.txt", "second file"));
        await DisableItem(group);
        var tree = await BuildTree(loadout);

        var foreign = Path.Combine(Path.GetDirectoryName(OnDisk("a/one.txt").ToString())!, "..", "foreign-target.txt");
        await File.WriteAllTextAsync(foreign, "a file that is not ours");
        OnDisk("a/one.txt").Delete();
        LibcLinks.TrySymlink(Path.GetFullPath(foreign), OnDisk("a/one.txt").ToString()).Should().Be(0);

        using var tx = Connection.BeginTransaction();
        Symlink.Delete(ContextFor(tree, tx, loadout), tree);

        IsSymlink(OnDisk("a/one.txt")).Should().BeTrue("a symlink to something outside the store is not ours to remove");
        OnDisk("b/two.txt").FileExists.Should().BeFalse();
    }

    [Fact]
    public void AcceptedReason_ForAStoreUnderTheHomeDirectoryNeedsNoRunLine()
    {
        var reason = SymlinkDeploymentBackend.AcceptedReason(
            NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath("/home/me/.local/share/silvermod/store"),
            NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath("/home/me"));

        reason.Should().Contain("symlinks work in the folder").And.Contain("under the home directory");
        reason.Should().NotContain("run line");
    }

    [Fact]
    public void AcceptedReason_ForAStoreOutsideTheHomeDirectoryNamesTheRootAndTheRunLine()
    {
        var reason = SymlinkDeploymentBackend.AcceptedReason(
            NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath("/run/media/me/disk/store"),
            NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath("/home/me"));

        reason.Should().Be("symlinks work in the folder; store root /run/media/me/disk/store is outside the home directory: the game sees symlink targets only when launched through the run line");
    }

    [Fact]
    public void AcceptedReason_TreatsASiblingFolderWithTheSamePrefixAsOutsideTheHomeDirectory()
    {
        var reason = SymlinkDeploymentBackend.AcceptedReason(
            NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath("/home/me2/store"),
            NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath("/home/me"));

        reason.Should().Contain("outside the home directory");
    }

    [Fact]
    public void RequiredFilesystems_IsTheStoreRoot()
    {
        Symlink.RequiredFilesystems(GameInstallation, GamePathOf("")).Should().Equal(Store.Root);
    }
}

public class SymlinkAcrossDevicesTests(ITestOutputHelper helper) : SymlinkBackendTestsBase<SymlinkAcrossDevicesTests>(helper, Placement.SharedMemory)
{
    [Fact]
    public async Task Ingest_RepairNeverOpensTheSymlinkForWrite()
    {
        var (loadout, _, hashes) = await Deploy(("a/x.txt", Original));
        var path = OnDisk("a/x.txt");
        IsSymlink(path).Should().BeTrue();
        await File.WriteAllTextAsync(path.ToString(), Edited);

        // The staged file is on another device than the game folder, so putting it in the link's place
        // is a copy, which must not follow the link it is replacing.
        await Synchronizer.Synchronize(loadout);

        IsSymlink(path).Should().BeFalse();
        File.ReadAllText(path.ToString()).Should().Be(Edited);
        _ = hashes;
    }
}
