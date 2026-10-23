using FluentAssertions;
using NexusMods.Paths;

namespace SilverModManager.Deployment.Vfs.Tests;

public class BackingScannerTests : IDisposable
{
    private readonly AbsolutePath _root = FileSystem.Shared
        .GetKnownPath(KnownPath.TempDirectory)
        .Combine($"vfs-scanner-tests-{Guid.NewGuid():N}");

    public BackingScannerTests() => _root.CreateDirectory();

    public void Dispose() => _root.DeleteDirectory(recursive: true);

    [Fact]
    public async Task ScanReportsFilesAndDirectoriesWithStatFields()
    {
        var file = _root.Combine("Data/sub/a.bin");
        file.Parent.CreateDirectory();
        await file.WriteAllBytesAsync([1, 2, 3, 4]);
        await _root.Combine("Data/sub/b.bin").WriteAllBytesAsync([5]);
        _root.Combine("Data/empty").CreateDirectory();

        var entries = new BackingScanner().Scan(_root).ToDictionary(e => e.Path.ToString());

        entries.Keys.Should().BeEquivalentTo("Data", "Data/sub", "Data/sub/a.bin", "Data/sub/b.bin", "Data/empty");
        entries["Data"].IsDirectory.Should().BeTrue();
        var scanned = entries["Data/sub/a.bin"];
        scanned.IsDirectory.Should().BeFalse();
        scanned.Size.Value.Should().Be(4);
        scanned.Inode.Should().NotBe(0);
        scanned.Device.Should().Be(entries["Data/sub/b.bin"].Device);
        scanned.Inode.Should().NotBe(entries["Data/sub/b.bin"].Inode);
        scanned.MTime.Should().BeCloseTo(File.GetLastWriteTimeUtc(file.ToString()), TimeSpan.FromSeconds(1));
        scanned.CTime.Should().BeCloseTo(File.GetLastWriteTimeUtc(file.ToString()), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task StatAgreesWithScan()
    {
        var file = _root.Combine("a.bin");
        await file.WriteAllBytesAsync([1, 2, 3]);

        var scanner = new BackingScanner();
        var stat = scanner.Stat(file)!.Value;
        var scanned = scanner.Scan(_root).Single();

        stat.Should().Be(scanned);
    }

    [Fact]
    public void StatOfAMissingFileIsNull() =>
        new BackingScanner().Stat(_root.Combine("missing")).Should().BeNull();

    [Fact]
    public void ScanOfAMissingRootThrows() =>
        FluentActions.Invoking(() => new BackingScanner().Scan(_root.Combine("missing")))
            .Should().Throw<DirectoryNotFoundException>();

    [Fact]
    public async Task ADirectorySymlinkLoopIsVisitedOnce()
    {
        var data = _root.Combine("Data");
        data.CreateDirectory();
        await data.Combine("a.txt").WriteAllBytesAsync([1]);
        var loop = data.Combine("loop").ToString();
        Directory.CreateSymbolicLink(loop, _root.ToString());

        string[] paths;
        // Cleanup would follow the link round and round, so it goes first.
        try { paths = new BackingScanner().Scan(_root).Select(e => e.Path.ToString()).ToArray(); }
        finally { File.Delete(loop); }

        paths.Should().Contain("Data/a.txt");
        paths.Count(p => p.EndsWith("a.txt")).Should().Be(1);
    }
}
