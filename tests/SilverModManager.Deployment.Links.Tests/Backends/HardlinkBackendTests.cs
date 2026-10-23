using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Deployment.Links.Backends;
using SilverModManager.Deployment.Links.Native;
using SilverModManager.Deployment.Links.Tests.Fixtures;
using NexusMods.Hashing.xxHash3;
using NexusMods.Hashing.xxHash3.Paths;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Native;

namespace SilverModManager.Deployment.Links.Tests.Backends;

public class HardlinkBackendTests(ITestOutputHelper helper)
    : LinkedGameTest<HardlinkBackendTests>(helper, Placement.TempDirectory, Placement.TempDirectory, DeploymentBackendId.Hardlink)
{
    private const string Original = "bytes of the mod file";
    private const string Edited = "bytes written by the game, longer than before";

    private HardlinkDeploymentBackend Hardlink => Backend<HardlinkDeploymentBackend>();

    private async Task<(Loadout.ReadOnly Loadout, EntityId Group, Dictionary<string, Hash> Hashes)> Deploy(params (string Path, string Content)[] files)
    {
        var loadout = await NewLoadout();
        var (group, hashes) = await AddMod(loadout, "Mod", files);
        loadout = await Synchronizer.Synchronize(loadout);
        return (loadout, group, hashes);
    }

    [Fact]
    public async Task Extract_LinksOnePathAndCopiesTheSecondForOneHash()
    {
        var (_, _, hashes) = await Deploy(("a/x.txt", Original), ("b/x.txt", Original));

        var store = Stat(Store.PathFor(MetadataId, hashes["a/x.txt"]));
        var a = Stat(OnDisk("a/x.txt"));
        var b = Stat(OnDisk("b/x.txt"));

        var linked = new[] { a, b }.Where(s => s.Inode == store.Inode).ToArray();
        linked.Should().ContainSingle("only one deployed path may share the staged file's inode");
        linked[0].NLink.Should().Be(2);
        new[] { a, b }.Single(s => s.Inode != store.Inode).NLink.Should().Be(1);
        File.ReadAllText(OnDisk("a/x.txt").ToString()).Should().Be(Original);
        File.ReadAllText(OnDisk("b/x.txt").ToString()).Should().Be(Original);
    }

    [Fact]
    public async Task Extract_ALaterApplyAddingAPathForALinkedHashGetsAPrivateCopy()
    {
        var (loadout, _, hashes) = await Deploy(("b/x.txt", Original));
        await AddMod(loadout, "Second mod", ("a/x.txt", Original));

        await Synchronizer.Synchronize(loadout);

        var store = Stat(Store.PathFor(MetadataId, hashes["b/x.txt"]));
        Stat(OnDisk("b/x.txt")).Inode.Should().Be(store.Inode, "the first path keeps the link");
        var added = Stat(OnDisk("a/x.txt"));
        added.Inode.Should().NotBe(store.Inode);
        added.NLink.Should().Be(1);
        store.NLink.Should().Be(2);
        File.ReadAllText(OnDisk("a/x.txt").ToString()).Should().Be(Original);
    }

    [Fact]
    public async Task Extract_OverAnExistingHardlink_NeverTruncatesTheStoreFile()
    {
        var loadout = await NewLoadout();
        var (_, hashes) = await AddMod(loadout, "Mod", ("x/target.txt", "the replacement"));

        // A staged file for some other hash, linked where the new file is about to go.
        var otherHash = Hash.From(0x1122334455667788UL);
        var staged = Store.PathFor(MetadataId, otherHash);
        staged.Parent.CreateDirectory();
        await File.WriteAllTextAsync(staged.ToString(), "staged bytes that must survive");
        OnDisk("x/target.txt").Parent.CreateDirectory();
        LibcLinks.TryLink(staged.ToString(), OnDisk("x/target.txt").ToString()).Should().Be(0);

        var tree = await BuildTree(loadout);

        // The index sees the pre-linked file as one the player added; this test is about what a plain extract
        // does to whatever is at the target.
        var node = tree[GamePathOf("x/target.txt")];
        node.Actions = Actions.ExtractToDisk;
        tree[GamePathOf("x/target.txt")] = node;
        using var tx = Connection.BeginTransaction();
        await Hardlink.Extract(ContextFor(tree, tx, loadout), tree);

        File.ReadAllText(staged.ToString()).Should().Be("staged bytes that must survive");
        Stat(staged).NLink.Should().Be(1);
        File.ReadAllText(OnDisk("x/target.txt").ToString()).Should().Be("the replacement");
        _ = hashes;
    }

    [Fact]
    public async Task Extract_TheModeIsSetFromTheRuleNotInheritedFromTheStoreInode()
    {
        var loadout = await NewLoadout();
        var (first, _) = await AddMod(loadout, "Launcher mod", ("bin/launcher", Original));
        loadout = await Synchronizer.Synchronize(loadout);
        File.GetUnixFileMode(OnDisk("bin/launcher").ToString()).Should().HaveFlag(UnixFileMode.UserExecute);

        await DisableItem(first);
        await Synchronizer.Synchronize(loadout);
        await AddMod(loadout, "Notes mod", ("notes.txt", Original));
        await Synchronizer.Synchronize(loadout);

        File.GetUnixFileMode(OnDisk("notes.txt").ToString()).Should().NotHaveFlag(UnixFileMode.UserExecute);
    }

    [Fact]
    public async Task Delete_RemovesOnlyInodeMatchedFilesAndLeavesASameNamedForeignFile()
    {
        var (loadout, group, _) = await Deploy(("a/one.txt", "first file"), ("b/two.txt", "second file"));
        await DisableItem(group);
        var tree = await BuildTree(loadout);

        // Replaced after the index the tree was built from.
        OnDisk("a/one.txt").Delete();
        await File.WriteAllTextAsync(OnDisk("a/one.txt").ToString(), "a different file entirely");

        using var tx = Connection.BeginTransaction();
        Hardlink.Delete(ContextFor(tree, tx, loadout), tree);
        await tx.Commit();

        File.ReadAllText(OnDisk("a/one.txt").ToString()).Should().Be("a different file entirely");
        OnDisk("b/two.txt").FileExists.Should().BeFalse();
        HasDiskState("a/one.txt").Should().BeTrue("the row of a file that was left alone stays");
        HasDiskState("b/two.txt").Should().BeFalse();
    }

    [Fact]
    public async Task Delete_FallsBackToTheDiskStateFingerprintWhenTheStorePathIsGone()
    {
        var (loadout, group, hashes) = await Deploy(("a/one.txt", "first file"));
        await DisableItem(group);
        var tree = await BuildTree(loadout);
        Store.Release(MetadataId, hashes["a/one.txt"]).Should().BeTrue();

        using var tx = Connection.BeginTransaction();
        Hardlink.Delete(ContextFor(tree, tx, loadout), tree);

        OnDisk("a/one.txt").FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task Ingest_AfterAnEditThroughTheLink_GivesTheStoreItsOwnInodeBack()
    {
        var (loadout, _, hashes) = await Deploy(("a/x.txt", Original), ("b/x.txt", Original));
        var hash = hashes["a/x.txt"];
        var storeInode = Stat(Store.PathFor(MetadataId, hash)).Inode;
        var linked = new[] { "a/x.txt", "b/x.txt" }.Single(p => Stat(OnDisk(p)).Inode == storeInode);
        var sibling = linked == "a/x.txt" ? "b/x.txt" : "a/x.txt";

        await File.WriteAllTextAsync(OnDisk(linked).ToString(), Edited);
        File.ReadAllText(Store.PathFor(MetadataId, hash).ToString()).Should().Be(Edited, "writing through the link is what damages the staged file");

        await Synchronizer.Synchronize(loadout);

        File.ReadAllText(OnDisk(linked).ToString()).Should().Be(Edited);
        Stat(OnDisk(linked)).Inode.Should().Be(storeInode, "the edited path keeps its inode");
        Store.Contains(MetadataId, hash).Should().BeFalse("the damaged staged file was released");

        var restored = await Store.Ensure(MetadataId, hash);
        (await restored.XxHash3Async()).Should().Be(hash);
        Stat(restored).Inode.Should().NotBe(storeInode);
        File.ReadAllText(OnDisk(sibling).ToString()).Should().Be(Original);
    }

    [Fact]
    public async Task Ingest_ComparesTheInodeForThePreviousHash()
    {
        var (loadout, _, hashes) = await Deploy(("a/x.txt", Original));
        var hash = hashes["a/x.txt"];
        var storeInode = Stat(Store.PathFor(MetadataId, hash)).Inode;
        await File.WriteAllTextAsync(OnDisk("a/x.txt").ToString(), Edited);

        var tree = await BuildTree(loadout);
        var node = tree[GamePathOf("a/x.txt")];
        node.Disk.Hash.Should().NotBe(node.Previous.Hash, "after the reindex the disk side holds the new content");
        Store.Contains(MetadataId, node.Disk.Hash).Should().BeFalse("which has no staged file to compare against");
        node.Actions.Should().HaveFlag(Actions.IngestFromDisk);

        using var tx = Connection.BeginTransaction();
        Hardlink.Ingest(ContextFor(tree, tx, loadout), tree);

        Store.Contains(MetadataId, hash).Should().BeFalse();
        Stat(OnDisk("a/x.txt")).Inode.Should().Be(storeInode);
    }

    [Fact]
    public async Task Reset_AfterAnEditThroughTheLink_ReleasesTheStoreFile()
    {
        var (_, _, hashes) = await Deploy(("a/x.txt", Original));
        var hash = hashes["a/x.txt"];
        await File.WriteAllTextAsync(OnDisk("a/x.txt").ToString(), Edited);

        await Synchronizer.ResetToOriginalGameState(GameInstallation, GameInstallation.LocatorResult.LocatorIds.Distinct().ToArray());

        var restored = await Store.Ensure(MetadataId, hash);
        (await restored.XxHash3Async()).Should().Be(hash);
    }

    [Fact]
    public async Task Extract_AStoreFileWhoseBytesNoLongerMatchItsNameIsReextracted()
    {
        var (loadout, _, hashes) = await Deploy(("a/x.txt", Original));
        var path = OnDisk("a/x.txt");
        var modified = File.GetLastWriteTimeUtc(path.ToString());

        // Same size, same mtime: the next index sees nothing, so only the hash check can notice.
        await File.WriteAllTextAsync(path.ToString(), new string('Z', Original.Length));
        File.SetLastWriteTimeUtc(path.ToString(), modified);

        await AddMod(loadout, "Second mod", ("c/x.txt", Original));
        await Synchronizer.Synchronize(loadout);

        File.ReadAllText(OnDisk("c/x.txt").ToString()).Should().Be(Original);
        Logs.Warnings.Should().Contain(w => w.Contains(hashes["a/x.txt"].ToString()) && w.Contains("wrote through a link"));
    }

    [Fact]
    public async Task WriteIntrinsics_OverALinkedPath_NeverTruncatesTheStoreFile()
    {
        var loadout = await NewLoadout();
        var otherHash = Hash.From(0x0102030405060708UL);
        var staged = Store.PathFor(MetadataId, otherHash);
        staged.Parent.CreateDirectory();
        await File.WriteAllTextAsync(staged.ToString(), "staged bytes that must survive");
        OnDisk("generated.txt").Parent.CreateDirectory();
        LibcLinks.TryLink(staged.ToString(), OnDisk("generated.txt").ToString()).Should().Be(0);

        var path = GamePathOf("generated.txt");
        var synchronizer = new IntrinsicSynchronizer(ServiceProvider, path, "generated bytes");
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [path] = new() { Actions = Actions.WriteIntrinsic, SourceItemType = LoadoutSourceItemType.Intrinsic },
        };

        using var tx = Connection.BeginTransaction();
        await Hardlink.WriteIntrinsics(ContextFor(tree, tx, loadout, synchronizer), tree);

        File.ReadAllText(staged.ToString()).Should().Be("staged bytes that must survive");
        File.ReadAllText(OnDisk("generated.txt").ToString()).Should().Be("generated bytes");
    }

    [Fact]
    public async Task WriteIntrinsics_IntoAFolderThatDoesNotExistYet_CreatesIt()
    {
        var loadout = await NewLoadout();
        var path = GamePathOf("not/yet/generated.txt");
        var synchronizer = new IntrinsicSynchronizer(ServiceProvider, path, "generated bytes");
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [path] = new() { Actions = Actions.WriteIntrinsic, SourceItemType = LoadoutSourceItemType.Intrinsic },
        };

        using var tx = Connection.BeginTransaction();
        await Hardlink.WriteIntrinsics(ContextFor(tree, tx, loadout, synchronizer), tree);

        File.ReadAllText(OnDisk("not/yet/generated.txt").ToString()).Should().Be("generated bytes");
    }

    [Fact]
    public async Task TheBackupLimitCountsTheFilesOfEveryBackendTogether()
    {
        var loadout = await NewLoadout();

        // The game folder is hard linked and the preferences folder copied. Neither share is over the limit on
        // its own, and neither file exists, so anything but the combined check fails on the first read instead.
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [GamePathOf("a.bin")] = BackupNode(0xA1, Size.GB * 3),
            [new GamePath(LocationId.Preferences, "b.bin")] = BackupNode(0xB2, Size.GB * 3),
        };

        var act = () => ((ALoadoutSynchronizer)Synchronizer).RunActions(tree, loadout);

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("larger than the maximum");
    }

    private static SyncNode BackupNode(ulong hash, Size size) => new()
    {
        Disk = new SyncNodePart { Hash = Hash.From(hash), Size = size, LastModifiedTicks = 1 },
        Actions = Actions.BackupFile,
    };

    private sealed class IntrinsicSynchronizer(IServiceProvider provider, GamePath path, string content) : DefaultSynchronizer(provider)
    {
        public override Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout) => new() { [path] = new File(path, content) };

        private sealed class File(GamePath path, string content) : IIntrinsicFile
        {
            public GamePath Path => path;
            public async Task Write(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree) => await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(content));
            public Task Ingest(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree, SilverModManager.MnemonicDB.Abstractions.ITransaction tx) => Task.CompletedTask;
        }
    }
}

public class HardlinkAcrossDevicesTests(ITestOutputHelper helper)
    : LinkedGameTest<HardlinkAcrossDevicesTests>(helper, Placement.SharedMemory, Placement.TempDirectory, DeploymentBackendId.Copy)
{
    private HardlinkDeploymentBackend Hardlink => Backend<HardlinkDeploymentBackend>();

    [Fact]
    public async Task Preflight_RefusesACrossDeviceRoot()
    {
        await NewLoadout();

        var preflight = Hardlink.Preflight(GameInstallation, GamePathOf(""));

        preflight.Accepted.Should().BeFalse();
        preflight.Reason.Should().Contain("EXDEV");
    }

    [Fact]
    public async Task Extract_AcrossDevicesWritesPrivateCopiesAndWarns()
    {
        var loadout = await NewLoadout();
        await AddMod(loadout, "Mod", ("a/x.txt", "mod bytes"));
        var tree = await BuildTree(loadout);

        using var tx = Connection.BeginTransaction();
        await Hardlink.Extract(ContextFor(tree, tx, loadout), tree);

        File.ReadAllText(OnDisk("a/x.txt").ToString()).Should().Be("mod bytes");
        Stat(OnDisk("a/x.txt")).NLink.Should().Be(1);
        Logs.Warnings.Should().Contain(w => w.Contains("EXDEV"));
    }

    [Fact]
    public async Task ARecordedHardlinkRootThatNoLongerAcceptsFailsBeforeWriting()
    {
        var loadout = await NewLoadout();
        await AddMod(loadout, "Mod", ("a/x.txt", "mod bytes"));

        // Hardlink recorded for a root it refuses: the shape of a game that was moved to another drive
        // after it was applied.
        await RecordGameRoot(DeploymentBackendId.Hardlink);
        var act = () => Synchronizer.Synchronize(loadout);

        (await act.Should().ThrowAsync<SynchronizerPreflightException>()).Which.Message.Should().Contain("EXDEV").And.Contain("--recommended");
        OnDisk("a/x.txt").FileExists.Should().BeFalse();

        var moved = await SynchronizerService.ChangeDeploymentBackend(GameInstallation, GamePathOf(""), DeploymentBackendId.Copy, chosen: true);

        moved.Should().BeTrue();
    }

    [Fact]
    public async Task MovingAnAppliedRootWhoseRecordedBackendNoLongerWorksStillCompletes()
    {
        var loadout = await NewLoadout();
        await AddMod(loadout, "Mod", ("a/x.txt", "mod bytes"));
        loadout = await Synchronizer.Synchronize(loadout);

        // Applied, then the root stopped accepting hard links (the game moved to another drive), and a change
        // is waiting: the move starts by synchronizing that change through the backend that no longer works.
        await RecordGameRoot(DeploymentBackendId.Hardlink);
        await AddMod(loadout, "Second mod", ("b/y.txt", "second mod bytes"));

        var moved = await SynchronizerService.ChangeDeploymentBackend(GameInstallation, GamePathOf(""), DeploymentBackendId.Copy, chosen: true);

        moved.Should().BeTrue();
        Logs.Warnings.Should().Contain(w => w.Contains("EXDEV"), "the hard link backend wrote a private copy instead of refusing");
        Plans.Load(Connection.Db, MetadataId).BackendFor(GamePathOf("")).Should().Be(DeploymentBackendId.Copy);
        foreach (var (path, content) in new[] { ("a/x.txt", "mod bytes"), ("b/y.txt", "second mod bytes") })
        {
            File.ReadAllText(OnDisk(path).ToString()).Should().Be(content);
            Stat(OnDisk(path)).NLink.Should().Be(1);
        }
    }
}
