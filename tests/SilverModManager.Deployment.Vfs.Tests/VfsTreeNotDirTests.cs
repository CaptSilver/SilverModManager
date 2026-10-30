using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Deployment.Vfs.Tests;

public class VfsTreeNotDirTests : IDisposable
{
    private readonly AbsolutePath _directory = FileSystem.Shared
        .GetKnownPath(KnownPath.TempDirectory)
        .Combine($"vfs-notdir-tests-{Guid.NewGuid():N}");

    public void Dispose() => _directory.DeleteDirectory(recursive: true);

    private static VfsFileSource Backing(string path) =>
        new(RelativePath.FromUnsanitizedInput(path), VfsSourceKind.Backing, null, Size.From(3), FileIdentity.XxHash3(Hash.From(1)));

    private VfsTree Tree(params VfsFileSource[] sources)
    {
        var staging = new StagingDirectory(_directory.Combine("staging"), GameInstallMetadataId.From(0xABC));
        return VfsTree.Build(default, [], sources, staging, VfsCachePolicy.Default);
    }

    private static ulong Ino(VfsTree tree, ulong parent, string name) => tree.Lookup(parent, name).Value.Ino;

    [Fact]
    public void NodesAreDirectoriesAndFilesByKind()
    {
        var tree = Tree(Backing("Data/a.esp"));
        var data = Ino(tree, IVfsOperations.RootInode, "Data");
        var file = Ino(tree, data, "a.esp");

        tree.NodeForTesting(data).Should().BeOfType<VfsDirectory>();
        tree.NodeForTesting(file).Should().BeOfType<VfsFile>();
        tree.NodeForTesting(9999).Should().BeNull();
    }

    [Fact]
    public void ReadDirPlusOnAFileIsENOTDIR()
    {
        var tree = Tree(Backing("a.esp"));
        tree.ReadDirPlus(Ino(tree, IVfsOperations.RootInode, "a.esp"), 0, 10).Error.Should().Be(Errno.ENOTDIR);
    }

    [Fact]
    public void LookupUnderAFileIsENOTDIR()
    {
        var tree = Tree(Backing("a.esp"));
        tree.Lookup(Ino(tree, IVfsOperations.RootInode, "a.esp"), "x").Error.Should().Be(Errno.ENOTDIR);
    }

    [Fact]
    public void CreateUnderAFileIsENOTDIR()
    {
        var tree = Tree(Backing("a.esp"));
        tree.Create(Ino(tree, IVfsOperations.RootInode, "a.esp"), "x", 0x1A4, VfsOpenFlags.Write).Error.Should().Be(Errno.ENOTDIR);
    }

    [Fact]
    public void MkDirUnderAFileIsENOTDIR()
    {
        var tree = Tree(Backing("a.esp"));
        tree.MkDir(Ino(tree, IVfsOperations.RootInode, "a.esp"), "x", 0x1ED).Error.Should().Be(Errno.ENOTDIR);
    }

    [Fact]
    public void RmDirOnAFileIsENOTDIR()
    {
        var tree = Tree(Backing("a.esp"));
        tree.RmDir(IVfsOperations.RootInode, "a.esp").Error.Should().Be(Errno.ENOTDIR);
    }

    [Fact]
    public void UnlinkOnADirectoryIsEISDIR()
    {
        var tree = Tree(Backing("Data/a.esp"));
        tree.Unlink(IVfsOperations.RootInode, "Data").Error.Should().Be(Errno.EISDIR);
    }

    [Fact]
    public void RenameIntoAFileIsENOTDIR()
    {
        var tree = Tree(Backing("Data/a.esp"), Backing("b.esp"));
        tree.Rename(IVfsOperations.RootInode, "Data", Ino(tree, IVfsOperations.RootInode, "b.esp"), "c").Error.Should().Be(Errno.ENOTDIR);
    }

    [Fact]
    public void LookupUnderAMissingInodeIsENOENT()
    {
        var tree = Tree(Backing("a.esp"));
        tree.Lookup(9999, "x").Error.Should().Be(Errno.ENOENT);
    }

    // A staged source with no real path breaks an invariant of the tree; operations answer EIO and never throw.
    private VfsTree TreeWithStagedFileMissingItsRealPath()
    {
        var source = new VfsFileSource(RelativePath.FromUnsanitizedInput("a.esp"), VfsSourceKind.Staged, null, Size.From(3), FileIdentity.None, FromStaging: true);
        return Tree(source);
    }

    [Fact]
    public void SetAttrOnAStagedFileWithoutARealPathIsEIO()
    {
        var tree = TreeWithStagedFileMissingItsRealPath();
        var ino = Ino(tree, IVfsOperations.RootInode, "a.esp");
        tree.SetAttr(ino, new VfsSetAttr(Size: 1, MTime: null, Mode: null)).Error.Should().Be(Errno.EIO);
    }

    [Fact]
    public void UnlinkOnAStagedFileWithoutARealPathIsEIO()
    {
        var tree = TreeWithStagedFileMissingItsRealPath();
        tree.Unlink(IVfsOperations.RootInode, "a.esp").Error.Should().Be(Errno.EIO);
    }

    [Fact]
    public void RenameOfAStagedFileWithoutARealPathIsEIO()
    {
        var tree = TreeWithStagedFileMissingItsRealPath();
        tree.Rename(IVfsOperations.RootInode, "a.esp", IVfsOperations.RootInode, "b.esp").Error.Should().Be(Errno.EIO);
    }
}
