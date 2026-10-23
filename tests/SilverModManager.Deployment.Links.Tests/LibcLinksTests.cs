using NexusMods.Deployment.Links.Native;
using NexusMods.Deployment.Links.Tests.Fixtures;
using NexusMods.Sdk.Native;

namespace NexusMods.Deployment.Links.Tests;

public sealed class LibcLinksTests : IDisposable
{
    private readonly LinkFixtures _fixtures = new();

    public void Dispose() => _fixtures.Dispose();

    [Fact]
    public void Link_SameDevice_SharesTheInode()
    {
        var root = _fixtures.SameDeviceRoot().ToString();
        var original = Path.Combine(root, "original");
        var linked = Path.Combine(root, "linked");
        File.WriteAllText(original, "data");

        Assert.Equal(0, LibcLinks.TryLink(original, linked));

        Assert.Equal(0, LibcStat.TryStat(original, out var a));
        Assert.Equal(0, LibcStat.TryStat(linked, out var b));
        Assert.Equal(2UL, a.NLink);
        Assert.Equal((a.Device, a.Inode), (b.Device, b.Inode));
    }

    [Fact]
    public void Link_CrossDevice_ReturnsEXDEV()
    {
        var original = Path.Combine(_fixtures.SameDeviceRoot().ToString(), "original");
        File.WriteAllText(original, "data");
        var target = Path.Combine(_fixtures.CrossDeviceRoot().ToString(), "linked");

        Assert.Equal(LibcLinks.EXDEV, LibcLinks.TryLink(original, target));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void Link_ToAnExistingPath_ReturnsEEXIST()
    {
        var root = _fixtures.SameDeviceRoot().ToString();
        var original = Path.Combine(root, "original");
        var other = Path.Combine(root, "other");
        File.WriteAllText(original, "data");
        File.WriteAllText(other, "other");

        Assert.Equal(LibcLinks.EEXIST, LibcLinks.TryLink(original, other));
    }

    [Fact]
    public void Clone_OnTmpfs_ReturnsEOPNOTSUPP()
    {
        var root = _fixtures.SameDeviceRoot().ToString();
        Assert.SkipUnless(CannotReflink(root), $"{root} supports reflinks");
        var source = Path.Combine(root, "source");
        File.WriteAllText(source, "data");

        using var from = File.OpenHandle(source);
        using var to = File.OpenHandle(Path.Combine(root, "destination"), FileMode.Create, FileAccess.Write);

        Assert.Equal(LibcLinks.EOPNOTSUPP, LibcLinks.TryClone(from, to));
    }

    [Fact]
    public void Clone_OnReflinkRoot_ProducesAnIndependentInodeWithTheSameBytes()
    {
        var root = _fixtures.ReflinkRoot().ToString();
        var source = Path.Combine(root, "source");
        var clone = Path.Combine(root, "clone");
        File.WriteAllText(source, "original bytes");

        using (var from = File.OpenHandle(source))
        using (var to = File.OpenHandle(clone, FileMode.Create, FileAccess.Write))
            Assert.Equal(0, LibcLinks.TryClone(from, to));

        Assert.Equal("original bytes", File.ReadAllText(clone));
        Assert.Equal(0, LibcStat.TryStat(source, out var a));
        Assert.Equal(0, LibcStat.TryStat(clone, out var b));
        Assert.NotEqual(a.Inode, b.Inode);

        File.WriteAllText(clone, "edited");
        Assert.Equal("original bytes", File.ReadAllText(source));
    }

    [Fact]
    public void ReadLink_RoundTripsAnAbsoluteTarget()
    {
        var root = _fixtures.SameDeviceRoot().ToString();
        var target = Path.Combine(root, "does-not-need-to-exist");
        var link = Path.Combine(root, "link");

        Assert.Equal(0, LibcLinks.TrySymlink(target, link));
        Assert.Equal(0, LibcLinks.TryReadLink(link, out var read));

        Assert.Equal(target, read);
    }

    [Fact]
    public void ReadLink_OnARegularFile_ReturnsEINVAL()
    {
        var file = Path.Combine(_fixtures.SameDeviceRoot().ToString(), "file");
        File.WriteAllText(file, "x");

        Assert.Equal(22, LibcLinks.TryReadLink(file, out _));
    }

    private static bool CannotReflink(string path)
    {
        // A temp directory on btrfs or xfs can reflink; the EOPNOTSUPP check then skips instead of failing.
        var source = Path.Combine(path, "tmpfs-check-source");
        File.WriteAllText(source, "x");
        using var from = File.OpenHandle(source);
        using var to = File.OpenHandle(Path.Combine(path, "tmpfs-check-destination"), FileMode.Create, FileAccess.Write);
        return LibcLinks.TryClone(from, to) != 0;
    }
}
