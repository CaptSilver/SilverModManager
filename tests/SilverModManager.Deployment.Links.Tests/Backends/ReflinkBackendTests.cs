using FluentAssertions;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Deployment.Links.Backends;
using SilverModManager.Deployment.Links.Tests.Fixtures;

namespace SilverModManager.Deployment.Links.Tests.Backends;

public class ReflinkBackendTests(ITestOutputHelper helper)
    : LinkedGameTest<ReflinkBackendTests>(helper, Placement.EntryDirectory, Placement.EntryDirectory, DeploymentBackendId.Reflink)
{
    private const string Original = "bytes of the mod file";

    [Fact]
    public async Task Extract_ClonesWithAnIndependentInode()
    {
        var loadout = await NewLoadout();
        var (_, hashes) = await AddMod(loadout, "Mod", ("a/x.txt", Original), ("b/x.txt", Original));

        await Synchronizer.Synchronize(loadout);

        var store = Stat(Store.PathFor(MetadataId, hashes["a/x.txt"]));
        foreach (var path in new[] { "a/x.txt", "b/x.txt" })
        {
            var deployed = Stat(OnDisk(path));
            deployed.Inode.Should().NotBe(store.Inode);
            deployed.NLink.Should().Be(1);
            File.ReadAllText(OnDisk(path).ToString()).Should().Be(Original);
        }
        Stat(OnDisk("a/x.txt")).Inode.Should().NotBe(Stat(OnDisk("b/x.txt")).Inode);
        store.NLink.Should().Be(1);
    }

    [Fact]
    public async Task Delete_UsesTheDiskStateFingerprint()
    {
        var loadout = await NewLoadout();
        var (group, _) = await AddMod(loadout, "Mod", ("a/x.txt", Original));
        loadout = await Synchronizer.Synchronize(loadout);
        OnDisk("a/x.txt").FileExists.Should().BeTrue();

        await DisableItem(group);
        await Synchronizer.Synchronize(loadout);

        OnDisk("a/x.txt").FileExists.Should().BeFalse();
        HasDiskState("a/x.txt").Should().BeFalse();
    }

    [Fact]
    public async Task Delete_LeavesAFileReplacedAfterTheIndex()
    {
        var loadout = await NewLoadout();
        var (group, _) = await AddMod(loadout, "Mod", ("a/x.txt", Original));
        loadout = await Synchronizer.Synchronize(loadout);
        await DisableItem(group);
        var tree = await BuildTree(loadout);
        await File.WriteAllTextAsync(OnDisk("a/x.txt").ToString(), "something the player put there afterwards");

        using var tx = Connection.BeginTransaction();
        Backend<ReflinkDeploymentBackend>().Delete(ContextFor(tree, tx, loadout), tree);

        File.ReadAllText(OnDisk("a/x.txt").ToString()).Should().Be("something the player put there afterwards");
    }
}

public class ReflinkOnTmpfsTests(ITestOutputHelper helper)
    : LinkedGameTest<ReflinkOnTmpfsTests>(helper, Placement.SharedMemory, Placement.SharedMemory, DeploymentBackendId.Copy)
{
    [Fact]
    public async Task Preflight_RefusesTmpfsWithEOPNOTSUPP()
    {
        await NewLoadout();

        var preflight = Backend<ReflinkDeploymentBackend>().Preflight(GameInstallation, GamePathOf(""));

        preflight.Accepted.Should().BeFalse();
        preflight.Reason.Should().Contain("EOPNOTSUPP");
    }
}
