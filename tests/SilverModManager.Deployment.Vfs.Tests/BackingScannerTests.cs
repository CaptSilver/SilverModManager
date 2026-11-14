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

    private static string Describe(BackingEntry e) => $"{e.Path}|{e.IsDirectory}|{e.Size.Value}|{e.Inode}";

    [Fact]
    public async Task ScanByDescriptor_ListsTheSameEntriesAsScanByPath()
    {
        var file = _root.Combine("Data/sub/a.bin");
        file.Parent.CreateDirectory();
        await file.WriteAllBytesAsync([1, 2, 3, 4]);
        await _root.Combine("Data/b.bin").WriteAllBytesAsync([5]);
        _root.Combine("Data/empty").CreateDirectory();

        using var handle = DirectoryHandle.Open(_root);
        var byDescriptor = new BackingScanner().Scan(handle).Select(Describe).Order().ToArray();
        var byPath = new BackingScanner().Scan(_root).Select(Describe).Order().ToArray();

        byDescriptor.Should().Equal(byPath);
        byDescriptor.Should().HaveCount(5);
    }

    [Fact]
    public async Task ScanByDescriptor_FollowsASymlinkToAFileOutsideTheRoot()
    {
        var outside = _root.Combine("elsewhere/real.bin");
        outside.Parent.CreateDirectory();
        await outside.WriteAllBytesAsync([1, 2, 3, 4, 5, 6]);
        var game = _root.Combine("game");
        game.CreateDirectory();
        File.CreateSymbolicLink(game.Combine("link.bin").ToString(), outside.ToString());

        using var handle = DirectoryHandle.Open(game);
        var entry = new BackingScanner().Scan(handle).Should().ContainSingle().Which;

        entry.Path.ToString().Should().Be("link.bin");
        entry.IsDirectory.Should().BeFalse();
        entry.Size.Value.Should().Be(6);

        // Served from the target's own folder, so moving that folder does not lose it.
        Directory.Move(outside.Parent.ToString(), _root.Combine("moved").ToString());
        using var file = handle.OpenFile(RelativePath.FromUnsanitizedInput("link.bin"), FileAccess.Read);
        var buffer = new byte[6];
        RandomAccess.Read(file, buffer, 0).Should().Be(6);
        buffer.Should().Equal(1, 2, 3, 4, 5, 6);
    }

    [Fact]
    public async Task ScanByDescriptor_ListsADirectoryReachedTwiceOnce()
    {
        var data = _root.Combine("Data");
        data.Combine("sub").CreateDirectory();
        await data.Combine("sub/a.txt").WriteAllBytesAsync([1]);
        var up = data.Combine("sub/up").ToString();
        File.CreateSymbolicLink(up, data.ToString());

        string[] paths;
        // Cleanup would follow the link round and round, so it goes first.
        try
        {
            using var handle = DirectoryHandle.Open(_root);
            paths = new BackingScanner().Scan(handle).Select(e => e.Path.ToString()).ToArray();
        }
        finally { File.Delete(up); }

        paths.Count(p => p.EndsWith("a.txt")).Should().Be(1);
    }

    [Fact]
    public async Task ScanByDescriptor_ListsAndServesTheContentsOfASymlinkedDirectory()
    {
        var game = _root.Combine("game");
        game.CreateDirectory();
        var outside = _root.Combine("elsewhere");
        outside.CreateDirectory();
        await outside.Combine("inner.txt").WriteAllBytesAsync("abc"u8.ToArray());
        File.CreateSymbolicLink(game.Combine("dir").ToString(), "../elsewhere");

        using var handle = DirectoryHandle.Open(game);
        var paths = new BackingScanner().Scan(handle).Select(e => e.Path.ToString()).Order().ToArray();
        paths.Should().Equal("dir", "dir/inner.txt");

        // The tree reads through the handle after the link's path has gone.
        Directory.Move(outside.ToString(), _root.Combine("moved").ToString());
        using var file = handle.OpenFile(RelativePath.FromUnsanitizedInput("dir/inner.txt"), FileAccess.Read);
        var buffer = new byte[3];
        RandomAccess.Read(file, buffer, 0).Should().Be(3);
        buffer.Should().Equal("abc"u8.ToArray());
    }

    [Fact]
    public async Task StatByDescriptor_AgreesWithStatByPath()
    {
        var file = _root.Combine("Data/a.bin");
        file.Parent.CreateDirectory();
        await file.WriteAllBytesAsync([1, 2, 3]);

        using var handle = DirectoryHandle.Open(_root);
        var byDescriptor = new BackingScanner().Stat(handle, RelativePath.FromUnsanitizedInput("Data/a.bin"));
        var byPath = new BackingScanner().Stat(file);

        byDescriptor!.Value.Size.Should().Be(byPath!.Value.Size);
        byDescriptor.Value.Inode.Should().Be(byPath.Value.Inode);
        byDescriptor.Value.Path.ToString().Should().Be("a.bin");
        new BackingScanner().Stat(handle, RelativePath.FromUnsanitizedInput("Data/missing")).Should().BeNull();
    }

    [Fact]
    public void OpenFile_ThroughASymlinkedIntermediateDirectoryIsRefused()
    {
        var game = _root.Combine("game");
        game.CreateDirectory();
        var outside = _root.Combine("elsewhere");
        outside.CreateDirectory();
        File.WriteAllText(outside.Combine("secret.txt").ToString(), "x");
        File.CreateSymbolicLink(game.Combine("dir").ToString(), outside.ToString());

        // Opened without a scan, so the link was never attached: nothing may resolve it.
        using var handle = DirectoryHandle.Open(game);
        FluentActions.Invoking(() => handle.OpenFile(RelativePath.FromUnsanitizedInput("dir/secret.txt"), FileAccess.Read))
            .Should().Throw<IOException>();
    }
}
