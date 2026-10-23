using NexusMods.Paths;
using SilverModManager.Sdk.Native;

namespace SilverModManager.Sdk.Tests.Native;

public class LibcStatTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory).ToString(),
        $"libc-stat-tests-{Guid.NewGuid():N}");

    public LibcStatTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void TryLStat_OnASymlink_ReportsTheLinkNotTheTarget()
    {
        var target = Path.Combine(_directory, "target");
        var link = Path.Combine(_directory, "link");
        File.WriteAllText(target, "x");
        File.CreateSymbolicLink(link, target);

        Assert.Equal(0, LibcStat.TryStat(target, out var targetStat));
        Assert.Equal(0, LibcStat.TryStat(link, out var followed));
        Assert.Equal(0, LibcStat.TryLStat(link, out var linkStat));

        Assert.Equal(targetStat.Inode, followed.Inode);
        Assert.NotEqual(targetStat.Inode, linkStat.Inode);
        Assert.Equal(LibcStat.Symlink, linkStat.Mode & LibcStat.TypeMask);
    }

    [Fact]
    public void TryLStat_OnAMissingPath_ReturnsENOENT()
    {
        Assert.Equal(2, LibcStat.TryLStat(Path.Combine(_directory, "missing"), out _));
    }
}
