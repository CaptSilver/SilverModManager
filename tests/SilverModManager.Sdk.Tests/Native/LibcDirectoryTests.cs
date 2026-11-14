using Microsoft.Win32.SafeHandles;
using NexusMods.Paths;
using SilverModManager.Sdk.Native;

namespace SilverModManager.Sdk.Tests.Native;

public class LibcDirectoryTests : IDisposable
{
    private const int ENOTDIR = 20;
    private const int ELOOP = 40;
    private const int ENOENT = 2;
    private const int FD_CLOEXEC = 1;

    private readonly string _directory = Path.Combine(
        FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory).ToString(),
        $"libc-directory-tests-{Guid.NewGuid():N}");

    public LibcDirectoryTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void TryOpenDirectory_OnAFile_ReturnsENOTDIR()
    {
        var file = Path.Combine(_directory, "a.txt");
        File.WriteAllText(file, "x");

        Assert.Equal(ENOTDIR, LibcDirectory.TryOpenDirectory(file, out var handle));
        Assert.Null(handle);
    }

    [Fact]
    public void TryOpenDirectory_OnAMissingPath_ReturnsENOENT() =>
        Assert.Equal(ENOENT, LibcDirectory.TryOpenDirectory(Path.Combine(_directory, "missing"), out _));

    [Fact]
    public void OpenAt_RefusesASymlinkLeaf()
    {
        File.WriteAllText(Path.Combine(_directory, "target"), "x");
        File.CreateSymbolicLink(Path.Combine(_directory, "link"), "target");
        Assert.Equal(0, LibcDirectory.TryOpenDirectory(_directory, out var dir));
        using (dir)
        {
            Assert.Equal(ELOOP, LibcDirectory.TryOpenAt(dir!, "link", LibcDirectory.ReadOnly, out var file));
            Assert.Null(file);
        }
    }

    [Fact]
    public void OpenAt_ReadsARegularFile()
    {
        File.WriteAllText(Path.Combine(_directory, "a.txt"), "hello");
        Assert.Equal(0, LibcDirectory.TryOpenDirectory(_directory, out var dir));
        using (dir)
        {
            Assert.Equal(0, LibcDirectory.TryOpenAt(dir!, "a.txt", LibcDirectory.ReadOnly, out var file));
            using (file)
            {
                var buffer = new byte[5];
                Assert.Equal(5, RandomAccess.Read(file!, buffer, 0));
                Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(buffer));
            }
        }
    }

    [Fact]
    public void Enumerate_ListsNamesWithTypes()
    {
        File.WriteAllText(Path.Combine(_directory, "a.txt"), "x");
        Directory.CreateDirectory(Path.Combine(_directory, "sub"));
        File.CreateSymbolicLink(Path.Combine(_directory, "link"), "a.txt");
        Assert.Equal(0, LibcDirectory.TryOpenDirectory(_directory, out var dir));
        using (dir)
        {
            var entries = LibcDirectory.Enumerate(dir!, "").ToDictionary(e => e.Name);

            Assert.Equal(["a.txt", "link", "sub"], entries.Keys.Order().ToArray());
            Assert.True(entries["sub"].IsDirectory);
            Assert.False(entries["a.txt"].IsDirectory);
            Assert.False(entries["link"].IsDirectory);
            Assert.True(entries["link"].IsSymlink);
            Assert.False(entries["a.txt"].IsSymlink);
            Assert.False(entries["sub"].IsSymlink);
        }
    }

    [Fact]
    public void Enumerate_ReachesASubdirectoryByRelativePath()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "sub"));
        File.WriteAllText(Path.Combine(_directory, "sub", "inner.txt"), "x");
        Assert.Equal(0, LibcDirectory.TryOpenDirectory(_directory, out var dir));
        using (dir)
            Assert.Equal(["inner.txt"], LibcDirectory.Enumerate(dir!, "sub").Select(e => e.Name).ToArray());
    }

    [Fact]
    public void TryStatAt_FollowsOrReportsTheLink()
    {
        File.WriteAllText(Path.Combine(_directory, "target"), "abcd");
        File.CreateSymbolicLink(Path.Combine(_directory, "link"), "target");
        Assert.Equal(0, LibcDirectory.TryOpenDirectory(_directory, out var dir));
        using (dir)
        {
            Assert.Equal(0, LibcDirectory.TryStatAt(dir!, "link", followSymlinks: true, out var followed));
            Assert.Equal(0, LibcDirectory.TryStatAt(dir!, "link", followSymlinks: false, out var link));

            Assert.True(followed.IsRegularFile);
            Assert.Equal(4, followed.Size);
            Assert.Equal(LibcStat.Symlink, link.Mode & LibcStat.TypeMask);
            Assert.Equal(ENOENT, LibcDirectory.TryStatAt(dir!, "missing", followSymlinks: true, out _));
        }
    }

    [Fact]
    public void OpenDirectory_HandleIsCloseOnExec()
    {
        Assert.Equal(0, LibcDirectory.TryOpenDirectory(_directory, out var dir));
        using (dir)
        {
            Assert.Equal(0, LibcDirectory.TryGetFdFlags(dir!, out var flags));
            Assert.NotEqual(0, flags & FD_CLOEXEC);
        }
    }

    [Fact]
    public void OpenAt_HandleIsCloseOnExec()
    {
        File.WriteAllText(Path.Combine(_directory, "a.txt"), "x");
        Assert.Equal(0, LibcDirectory.TryOpenDirectory(_directory, out var dir));
        using (dir)
        {
            Assert.Equal(0, LibcDirectory.TryOpenAt(dir!, "a.txt", LibcDirectory.ReadOnly, out var file));
            using (file)
            {
                Assert.Equal(0, LibcDirectory.TryGetFdFlags(file!, out var flags));
                Assert.NotEqual(0, flags & FD_CLOEXEC);
            }
        }
    }

    [Fact]
    public void TryRealPath_ResolvesSymlinks()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "real"));
        Directory.CreateSymbolicLink(Path.Combine(_directory, "alias"), "real");

        Assert.Equal(0, LibcDirectory.TryRealPath(Path.Combine(_directory, "alias"), out var resolved));
        Assert.Equal("real", Path.GetFileName(resolved));
        Assert.Null(new FileInfo(resolved).LinkTarget);
    }
}
