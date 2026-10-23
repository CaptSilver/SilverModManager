using SilverModManager.Deployment.Links.Probe;
using SilverModManager.Deployment.Links.Tests.Fixtures;

namespace SilverModManager.Deployment.Links.Tests;

public sealed class LinkProbeTests : IDisposable
{
    private readonly LinkFixtures _fixtures = new();

    public void Dispose() => _fixtures.Dispose();

    [Fact]
    public void SameDevice_TwoDirectoriesOnOneFilesystem_IsTrue()
    {
        Assert.True(LinkProbe.SameDevice(_fixtures.SameDeviceRoot(), _fixtures.SameDeviceRoot()));
    }

    [Fact]
    public void SameDevice_AcrossFilesystems_IsFalse()
    {
        Assert.False(LinkProbe.SameDevice(_fixtures.SameDeviceRoot(), _fixtures.CrossDeviceRoot()));
    }

    [Fact]
    public void SameDevice_UsesTheNearestExistingAncestorOfAMissingPath()
    {
        var root = _fixtures.SameDeviceRoot();
        Assert.True(LinkProbe.SameDevice(root, root.Combine("missing/deeper/still")));
    }

    [Fact]
    public void Hardlink_SameDevice_AcceptedAndLeavesNoProbeFiles()
    {
        var store = _fixtures.SameDeviceRoot();
        var root = _fixtures.SameDeviceRoot();

        Assert.True(LinkProbe.TryHardlink(store, root, out var reason), reason);

        Assert.Empty(Directory.GetFileSystemEntries(store.ToString()));
        Assert.Empty(Directory.GetFileSystemEntries(root.ToString()));
    }

    [Fact]
    public void Hardlink_IntoARootThatDoesNotExistYet_UsesTheNearestExistingAncestor()
    {
        var store = _fixtures.SameDeviceRoot();
        var root = _fixtures.SameDeviceRoot().Combine("My Games/Game");

        Assert.True(LinkProbe.TryHardlink(store, root, out var reason), reason);
        Assert.False(Directory.Exists(root.ToString()));
    }

    [Fact]
    public void Hardlink_CrossDevice_RefusedWithEXDEVInTheReason()
    {
        var store = _fixtures.SameDeviceRoot();
        var root = _fixtures.CrossDeviceRoot();

        Assert.False(LinkProbe.TryHardlink(store, root, out var reason));

        Assert.Contains("EXDEV", reason);
        Assert.Empty(Directory.GetFileSystemEntries(store.ToString()));
        Assert.Empty(Directory.GetFileSystemEntries(root.ToString()));
    }

    [Fact]
    public void Reflink_OnTmpfs_RefusedWithEOPNOTSUPP()
    {
        var store = _fixtures.SameDeviceRoot();
        var root = _fixtures.SameDeviceRoot();

        var accepted = LinkProbe.TryReflink(store, root, out var reason);

        Assert.SkipWhen(accepted, "the temp directory supports reflinks");
        Assert.Contains("EOPNOTSUPP", reason);
        Assert.Empty(Directory.GetFileSystemEntries(store.ToString()));
        Assert.Empty(Directory.GetFileSystemEntries(root.ToString()));
    }

    [Fact]
    public void Reflink_CrossDevice_RefusedWithEXDEVInTheReason()
    {
        var store = _fixtures.ReflinkRoot();
        var root = _fixtures.CrossDeviceRoot();

        Assert.False(LinkProbe.TryReflink(store, root, out var reason));

        Assert.Contains("EXDEV", reason);
    }

    [Fact]
    public void Reflink_OnReflinkRoot_AcceptedAndLeavesNoProbeFiles()
    {
        var store = _fixtures.ReflinkRoot();
        var root = _fixtures.ReflinkRoot();

        Assert.True(LinkProbe.TryReflink(store, root, out var reason), reason);

        Assert.Empty(Directory.GetFileSystemEntries(store.ToString()));
        Assert.Empty(Directory.GetFileSystemEntries(root.ToString()));
    }

    [Fact]
    public void Symlink_CrossDevice_Accepted()
    {
        var store = _fixtures.SameDeviceRoot();
        var root = _fixtures.CrossDeviceRoot();

        Assert.True(LinkProbe.TrySymlink(store, root, out var reason), reason);

        Assert.Empty(Directory.GetFileSystemEntries(store.ToString()));
        Assert.Empty(Directory.GetFileSystemEntries(root.ToString()));
    }

    [Fact]
    public void Symlink_IntoAReadOnlyRoot_RefusedWithTheErrnoInTheReason()
    {
        var store = _fixtures.SameDeviceRoot();
        var root = _fixtures.SameDeviceRoot();
        Assert.SkipWhen(Environment.UserName == "root", "root ignores directory permissions");
        File.SetUnixFileMode(root.ToString(), UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.False(LinkProbe.TrySymlink(store, root, out var reason));
            Assert.Contains("EACCES", reason);
        }
        finally
        {
            File.SetUnixFileMode(root.ToString(), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
