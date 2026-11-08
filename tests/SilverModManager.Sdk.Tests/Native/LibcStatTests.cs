using System.Runtime.InteropServices;
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

    [Fact]
    public void TheX64LayoutIs144BytesWithTheOffsetsGlibcUses()
    {
        Assert.Equal(144, Marshal.SizeOf<LibcStat.Buffer>());
        AssertOffsets<LibcStat.Buffer>(
            ("Device", 0), ("Inode", 8), ("NLink", 16), ("Mode", 24), ("Uid", 28), ("Gid", 32), ("RDevice", 40),
            ("Size", 48), ("BlockSize", 56), ("Blocks", 64), ("AccessSeconds", 72), ("ModifySeconds", 88), ("ChangeSeconds", 104));
    }

    [Fact]
    public void TheArm64LayoutIs128BytesWithTheOffsetsGlibcUses()
    {
        Assert.Equal(128, Marshal.SizeOf<LibcStat.Arm64Layout>());
        AssertOffsets<LibcStat.Arm64Layout>(
            ("Device", 0), ("Inode", 8), ("Mode", 16), ("NLink", 20), ("Uid", 24), ("Gid", 28), ("RDevice", 32),
            ("Size", 48), ("BlockSize", 56), ("Blocks", 64), ("AccessSeconds", 72), ("ModifySeconds", 88), ("ChangeSeconds", 104));
    }

    [Fact]
    public void TwoHardLinksShareDeviceAndInode()
    {
        var first = Path.Combine(_directory, "first");
        var second = Path.Combine(_directory, "second");
        File.WriteAllText(first, "x");
        Assert.Equal(0, LibcLinks.TryLink(first, second));

        Assert.Equal(0, LibcStat.TryStat(first, out var a));
        Assert.Equal(0, LibcStat.TryStat(second, out var b));

        Assert.Equal(a.Device, b.Device);
        Assert.Equal(a.Inode, b.Inode);
        Assert.Equal(2UL, a.NLink);
    }

    [Fact]
    public void SizeAndMTimeMatchTheFileSystemInfo()
    {
        var path = Path.Combine(_directory, "file");
        File.WriteAllText(path, "twelve bytes");

        Assert.Equal(0, LibcStat.TryStat(path, out var stat));

        var info = new FileInfo(path);
        Assert.Equal(info.Length, stat.Size);
        Assert.InRange(Math.Abs((stat.MTime - info.LastWriteTimeUtc).TotalSeconds), 0, 1);
    }

    [Fact]
    public void AnUnsupportedCpuIsRefusedByName()
    {
        var path = Path.Combine(_directory, "file");
        File.WriteAllText(path, "x");

        var exception = Assert.Throws<PlatformNotSupportedException>(() => LibcStat.TryStat(path, Architecture.Wasm, true, out _));

        Assert.Contains("Wasm", exception.Message);
    }

    [Fact]
    public void TheNativeCpuBranchReadsARegularFileAsTheFileSystemDescribesIt()
    {
        var path = Path.Combine(_directory, "file");
        File.WriteAllText(path, "abc");

        Assert.Equal(0, LibcStat.TryStat(path, RuntimeInformation.ProcessArchitecture, followSymlinks: true, out var stat));

        Assert.Equal(LibcStat.RegularFile, stat.Mode & LibcStat.TypeMask);
        Assert.Equal(1UL, (ulong)stat.NLink);
        Assert.Equal(3, stat.Size);
        Assert.Equal(File.GetUnixFileMode(path), (UnixFileMode)(stat.Mode & 0xFFF));
    }

    [Fact]
    public void TheArm64LayoutIsCopiedFieldByFieldIntoTheBuffer()
    {
        var raw = new LibcStat.Arm64Layout
        {
            Device = 1, Inode = 2, Mode = LibcStat.RegularFile | 0x1A4, NLink = 3, Uid = 4, Gid = 5, RDevice = 6,
            Size = 7, BlockSize = 8, Blocks = 9,
            AccessSeconds = 10, AccessNanoseconds = 11, ModifySeconds = 12, ModifyNanoseconds = 13,
            ChangeSeconds = 14, ChangeNanoseconds = 15,
        };

        var buffer = LibcStat.ToBuffer(raw);

        Assert.Equal((1UL, 2UL, 3UL), (buffer.Device, buffer.Inode, buffer.NLink));
        Assert.Equal(LibcStat.RegularFile | 0x1A4, buffer.Mode);
        Assert.Equal((4U, 5U, 6UL), (buffer.Uid, buffer.Gid, buffer.RDevice));
        Assert.Equal((7L, 8L, 9L), (buffer.Size, buffer.BlockSize, buffer.Blocks));
        Assert.Equal((10L, 11L, 12L, 13L, 14L, 15L),
            (buffer.AccessSeconds, buffer.AccessNanoseconds, buffer.ModifySeconds, buffer.ModifyNanoseconds, buffer.ChangeSeconds, buffer.ChangeNanoseconds));
    }

    private static void AssertOffsets<T>(params (string Field, int Offset)[] expected)
    {
        foreach (var (field, offset) in expected)
            Assert.Equal(offset, (int)Marshal.OffsetOf<T>(field));
    }
}
