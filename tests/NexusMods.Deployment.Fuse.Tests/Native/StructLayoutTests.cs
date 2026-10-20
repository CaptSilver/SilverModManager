using System.Reflection;
using System.Runtime.InteropServices;
using FluentAssertions;
using NexusMods.Deployment.Fuse.Native;

namespace NexusMods.Deployment.Fuse.Tests.Native;

/// <summary>
/// Pins every transcribed libfuse3 struct to the sizes and offsets a C compiler prints for the same headers.
/// The numbers below came from libfuse 3.18.3 on x86_64 glibc, i.e. <see cref="FuseAbi.VerifiedVersion"/>.
/// To regenerate them, build a program that includes <c>fuse_lowlevel.h</c> (with <c>FUSE_USE_VERSION 318</c>)
/// and <c>sys/statvfs.h</c> with <c>gcc x.c $(pkg-config fuse3 --cflags --libs)</c> and print, for each struct,
/// <code>
/// printf("%zu\n", sizeof(struct fuse_lowlevel_ops));
/// printf("%zu\n", offsetof(struct fuse_file_info, fh));
/// </code>
/// For the <c>fuse_file_info</c> bitfield, set one member on a zeroed struct and print the second 32-bit word.
/// A number that differs after a libfuse upgrade is what these tests are for: raise
/// <see cref="FuseAbi.VerifiedVersion"/> only after the new numbers have been compared with the structs.
/// </summary>
public class StructLayoutTests
{
    [Fact]
    public void ConstantsCameFromTheVerifiedVersion()
    {
        FuseAbi.VerifiedVersion.Should().Be(318);
    }

    [Fact]
    public void FuseArgs_Layout()
    {
        Marshal.SizeOf<FuseArgs>().Should().Be(24);
        Offset<FuseArgs>("Argc").Should().Be(0);
        Offset<FuseArgs>("Argv").Should().Be(8);
        Offset<FuseArgs>("Allocated").Should().Be(16);
    }

    [Fact]
    public void LibfuseVersion_Layout()
    {
        Marshal.SizeOf<LibfuseVersion>().Should().Be(16);
        Offset<LibfuseVersion>("Minor").Should().Be(4);
        Offset<LibfuseVersion>("Hotfix").Should().Be(8);
    }

    [Fact]
    public void LibfuseVersion_Current_NamesTheHeadersTheStructsWereTranscribedFrom()
    {
        var current = LibfuseVersion.Current;
        (current.Major, current.Minor, current.Hotfix, current.Padding).Should().Be((3u, 18u, 3u, 0u));
    }

    [Fact]
    public void FuseEntryParam_Layout()
    {
        Marshal.SizeOf<FuseEntryParam>().Should().Be(176);
        Offset<FuseEntryParam>("Ino").Should().Be(0);
        Offset<FuseEntryParam>("Generation").Should().Be(8);
        Offset<FuseEntryParam>("Attr").Should().Be(16);
        Offset<FuseEntryParam>("AttrTimeout").Should().Be(160);
        Offset<FuseEntryParam>("EntryTimeout").Should().Be(168);
    }

    [Fact]
    public void FuseFileInfo_Layout()
    {
        Marshal.SizeOf<FuseFileInfo>().Should().Be(64);
        Offset<FuseFileInfo>("Flags").Should().Be(0);
        Offset<FuseFileInfo>("Fh").Should().Be(16);
        Offset<FuseFileInfo>("LockOwner").Should().Be(24);
        Offset<FuseFileInfo>("PollEvents").Should().Be(32);
        Offset<FuseFileInfo>("BackingId").Should().Be(36);
        Offset<FuseFileInfo>("CompatFlags").Should().Be(40);
        Offset<FuseFileInfo>("_reserved0").Should().Be(48);
    }

    [Fact]
    public void FuseFileInfo_BitfieldWordStartsAtOffsetFour()
    {
        // A wrong word offset or bit order would silently drop keep_cache or direct_io.
        Offset<FuseFileInfo>("_bits").Should().Be(4);

        var info = new FuseFileInfo { KeepCache = true };
        BitsOf(info).Should().Be(0x4u);
    }

    [Theory]
    [InlineData(nameof(FuseFileInfo.WritePage), 0x1u)]
    [InlineData(nameof(FuseFileInfo.DirectIo), 0x2u)]
    [InlineData(nameof(FuseFileInfo.KeepCache), 0x4u)]
    [InlineData(nameof(FuseFileInfo.Flush), 0x8u)]
    [InlineData(nameof(FuseFileInfo.NonSeekable), 0x10u)]
    [InlineData(nameof(FuseFileInfo.FlockRelease), 0x20u)]
    [InlineData(nameof(FuseFileInfo.CacheReaddir), 0x40u)]
    [InlineData(nameof(FuseFileInfo.NoFlush), 0x80u)]
    [InlineData(nameof(FuseFileInfo.ParallelDirectWrites), 0x100u)]
    [InlineData(nameof(FuseFileInfo.KillSuidgid), 0x200u)]
    public void FuseFileInfo_BitfieldMembersMatchTheCompilersBitOrder(string member, uint expected)
    {
        var property = typeof(FuseFileInfo).GetProperty(member)!;
        object boxed = new FuseFileInfo();
        property.SetValue(boxed, true);

        BitsOf((FuseFileInfo)boxed).Should().Be(expected);
        ((bool)property.GetValue(boxed)!).Should().BeTrue();
    }

    [Fact]
    public void FuseConnInfo_Layout()
    {
        Marshal.SizeOf<FuseConnInfo>().Should().Be(128);
        Offset<FuseConnInfo>("ProtoMajor").Should().Be(0);
        Offset<FuseConnInfo>("MaxWrite").Should().Be(8);
        Offset<FuseConnInfo>("Capable").Should().Be(20);
        Offset<FuseConnInfo>("Want").Should().Be(24);
        Offset<FuseConnInfo>("MaxBackground").Should().Be(28);
        Offset<FuseConnInfo>("TimeGran").Should().Be(36);
        Offset<FuseConnInfo>("MaxBackingStackDepth").Should().Be(40);
        Offset<FuseConnInfo>("CapableExt").Should().Be(48);
        Offset<FuseConnInfo>("WantExt").Should().Be(56);
        Offset<FuseConnInfo>("RequestTimeout").Should().Be(64);
    }

    [Fact]
    public void FuseBuf_Layout()
    {
        Marshal.SizeOf<FuseBuf>().Should().Be(48);
        Offset<FuseBuf>("Flags").Should().Be(8);
        Offset<FuseBuf>("Mem").Should().Be(16);
        Offset<FuseBuf>("Fd").Should().Be(24);
        Offset<FuseBuf>("Pos").Should().Be(32);
        Offset<FuseBuf>("MemSize").Should().Be(40);
    }

    [Fact]
    public void FuseBufvec_Layout()
    {
        Marshal.SizeOf<FuseBufvec>().Should().Be(72);
        Offset<FuseBufvec>("Idx").Should().Be(8);
        Offset<FuseBufvec>("Off").Should().Be(16);
        Offset<FuseBufvec>("Buf").Should().Be(24);
    }

    [Fact]
    public void FuseBufFlags_MatchTheHeader()
    {
        ((int)FuseBufFlags.IsFd).Should().Be(2);
        ((int)FuseBufFlags.FdSeek).Should().Be(4);
        ((int)FuseBufCopyFlags.SpliceMove).Should().Be(8);
    }

    [Fact]
    public void StatVfs_Layout()
    {
        Marshal.SizeOf<StatVfs>().Should().Be(112);
        Offset<StatVfs>("BlockSize").Should().Be(0);
        Offset<StatVfs>("FragmentSize").Should().Be(8);
        Offset<StatVfs>("Blocks").Should().Be(16);
        Offset<StatVfs>("BlocksFree").Should().Be(24);
        Offset<StatVfs>("BlocksAvailable").Should().Be(32);
        Offset<StatVfs>("Files").Should().Be(40);
        Offset<StatVfs>("FilesFree").Should().Be(48);
        Offset<StatVfs>("FilesAvailable").Should().Be(56);
        Offset<StatVfs>("FilesystemId").Should().Be(64);
        Offset<StatVfs>("MountFlags").Should().Be(72);
        Offset<StatVfs>("NameMax").Should().Be(80);
    }

    [Fact]
    public void FuseCtx_Layout()
    {
        Marshal.SizeOf<FuseCtx>().Should().Be(16);
        Offset<FuseCtx>("Gid").Should().Be(4);
        Offset<FuseCtx>("Pid").Should().Be(8);
        Offset<FuseCtx>("Umask").Should().Be(12);
    }

    /// <summary>Header order of <c>struct fuse_lowlevel_ops</c>; every member is one function pointer.</summary>
    private static readonly string[] OperationsInHeaderOrder =
    [
        "Init", "Destroy", "Lookup", "Forget", "Getattr", "Setattr", "Readlink", "Mknod", "Mkdir", "Unlink",
        "Rmdir", "Symlink", "Rename", "Link", "Open", "Read", "Write", "Flush", "Release", "Fsync",
        "Opendir", "Readdir", "Releasedir", "Fsyncdir", "Statfs", "Setxattr", "Getxattr", "Listxattr",
        "Removexattr", "Access", "Create", "Getlk", "Setlk", "Bmap", "Ioctl", "Poll", "WriteBuf",
        "RetrieveReply", "ForgetMulti", "Flock", "Fallocate", "Readdirplus", "CopyFileRange", "Lseek",
        "Tmpfile", "Statx",
    ];

    [Fact]
    public void FuseLowlevelOps_IsOnePointerPerOperation()
    {
        Marshal.SizeOf<FuseLowlevelOps>().Should().Be(368);
        (Marshal.SizeOf<FuseLowlevelOps>() / IntPtr.Size).Should().Be(46);
        OperationsInHeaderOrder.Should().HaveCount(46);
        typeof(FuseLowlevelOps).GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Select(f => f.Name).Should().Equal(OperationsInHeaderOrder);
    }

    [Fact]
    public void FuseLowlevelOps_OffsetsFollowHeaderOrder()
    {
        for (var i = 0; i < OperationsInHeaderOrder.Length; i++)
            Marshal.OffsetOf<FuseLowlevelOps>(OperationsInHeaderOrder[i]).Should().Be(i * 8, OperationsInHeaderOrder[i]);

        Offset<FuseLowlevelOps>("Statx").Should().Be(360);
    }

    [Fact]
    public void FusePassthroughCapability_MatchesTheHeader()
    {
        FuseCapabilities.Passthrough.Should().Be(536870912UL);
    }

    [Fact]
    [Trait("RequiresFuse", "True")]
    public void EntryPointsResolve()
    {
        RequiresFuse.SkipUnlessLibraryInstalled();

        NativeLibrary.TryLoad(FuseAbi.SupportedSoname, out var handle).Should().BeTrue();
        try
        {
            LibFuse3LowLevel.EntryPoints.Should().NotBeEmpty();
            foreach (var name in LibFuse3LowLevel.EntryPoints)
                NativeLibrary.TryGetExport(handle, name, out _).Should().BeTrue($"{FuseAbi.SupportedSoname} should export {name}");
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    private static int Offset<T>(string field) => (int)Marshal.OffsetOf<T>(field);

    private static uint BitsOf(FuseFileInfo info)
    {
        var bytes = new byte[Marshal.SizeOf<FuseFileInfo>()];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            Marshal.StructureToPtr(info, handle.AddrOfPinnedObject(), false);
            return BitConverter.ToUInt32(bytes, 4);
        }
        finally
        {
            handle.Free();
        }
    }
}
