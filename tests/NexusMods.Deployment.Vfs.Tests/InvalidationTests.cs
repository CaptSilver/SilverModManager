using System.Text;
using FluentAssertions;
using NexusMods.Deployment.Vfs.Testing;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Deployment.Vfs.Tests;

public class InvalidationTests : IDisposable
{
    private readonly AbsolutePath _directory = FileSystem.Shared
        .GetKnownPath(KnownPath.TempDirectory)
        .Combine($"vfs-invalidation-tests-{Guid.NewGuid():N}");

    private readonly AbsolutePath _backing;
    private readonly StagingDirectory _staging;

    public InvalidationTests()
    {
        _backing = _directory.Combine("game");
        _backing.CreateDirectory();
        _staging = new StagingDirectory(_directory.Combine("staging"), GameInstallMetadataId.From(0xABC));
    }

    public void Dispose() => _directory.DeleteDirectory(recursive: true);

    private void WriteBacking(string path, string text)
    {
        var file = _backing.Combine(path);
        file.Parent.CreateDirectory();
        File.WriteAllText(file.ToString(), text);
    }

    private async Task<FakeFuseKernel> Mount()
    {
        var backing = new BackingScanner().Scan(_backing);
        var tree = VfsTree.Build(_backing, backing, [], _staging, VfsCachePolicy.Default);
        var kernel = new FakeFuseKernel();
        await kernel.Mount(tree, FuseMountOptions.Default(_directory), CancellationToken.None);
        return kernel;
    }

    private static ulong Ino(FakeFuseKernel kernel, string path) => kernel.Resolve(path).Value.Ino;

    private static (string, ulong, string?) Entry(ulong parent, string name) => ("Entry", parent, name);
    private static (string, ulong, string?) Inode(ulong ino) => ("Inode", ino, null);

    [Fact]
    public async Task CreatingANameInvalidatesEveryRecordedSpelling()
    {
        WriteBacking("Data/other.txt", "x");
        var kernel = await Mount();
        var data = Ino(kernel, "Data");

        kernel.Resolve("data/Mesh.nif").Error.Should().Be(Errno.ENOENT);
        kernel.Resolve("Data/mesh.NIF").Error.Should().Be(Errno.ENOENT);
        kernel.Create("DATA/MESH.NIF", 0x1A4, VfsOpenFlags.Write).Ok.Should().BeTrue();

        kernel.Invalidations.Should().Contain(Entry(data, "Mesh.nif"));
        kernel.Invalidations.Should().Contain(Entry(data, "mesh.NIF"));
        kernel.Invalidations.Should().Contain(Entry(data, "MESH.NIF"), "the spelling the kernel used is dropped too");
    }

    [Fact]
    public async Task MakingADirectoryInvalidatesRecordedSpellings()
    {
        WriteBacking("Data/other.txt", "x");
        var kernel = await Mount();
        var data = Ino(kernel, "Data");

        kernel.Resolve("Data/Meshes").Error.Should().Be(Errno.ENOENT);
        kernel.MkDir("Data/MESHES", 0x1ED).Ok.Should().BeTrue();

        kernel.Invalidations.Should().Contain(Entry(data, "Meshes"));
        kernel.Invalidations.Should().Contain(Entry(data, "MESHES"));
    }

    [Fact]
    public async Task CopyUpInvalidatesTheInode()
    {
        WriteBacking("Data/vanilla.txt", "vanilla");
        var kernel = await Mount();
        var file = Ino(kernel, "Data/vanilla.txt");
        kernel.Invalidations.Should().BeEmpty();

        var open = kernel.Open("Data/vanilla.txt", VfsOpenFlags.Write);
        open.Ok.Should().BeTrue();
        kernel.Release(open.Value!);

        kernel.Invalidations.Should().Contain(Inode(file));
    }

    [Fact]
    public async Task ResizingAFileInvalidatesTheInode()
    {
        WriteBacking("Data/vanilla.txt", "vanilla");
        var kernel = await Mount();
        var file = Ino(kernel, "Data/vanilla.txt");

        kernel.Operations.SetAttr(file, new VfsSetAttr(3, null, null)).Ok.Should().BeTrue();

        kernel.Invalidations.Should().Contain(Inode(file));
    }

    [Fact]
    public async Task RenameInvalidatesBothNamesInBothParents()
    {
        var kernel = await Mount();
        kernel.MkDir("Data", 0x1ED).Ok.Should().BeTrue();
        kernel.MkDir("Other", 0x1ED).Ok.Should().BeTrue();
        kernel.Create("Data/a.txt", 0x1A4, VfsOpenFlags.Write).Ok.Should().BeTrue();
        var data = Ino(kernel, "Data");
        var other = Ino(kernel, "Other");
        kernel.Resolve("data/A.TXT").Ok.Should().BeTrue();
        kernel.Resolve("Other/B.txt").Error.Should().Be(Errno.ENOENT);
        kernel.Resolve("Other/b.TXT").Error.Should().Be(Errno.ENOENT);
        var before = kernel.Invalidations.Count;

        kernel.Rename("Data/a.txt", "Other/B.txt").Ok.Should().BeTrue();

        var dropped = kernel.Invalidations.Skip(before).ToArray();
        dropped.Should().Contain(Entry(data, "a.txt"));
        dropped.Should().Contain(Entry(data, "A.TXT"));
        dropped.Should().Contain(Entry(other, "B.txt"));
        dropped.Should().Contain(Entry(other, "b.TXT"));
    }

    [Fact]
    public async Task UnlinkInvalidatesRecordedSpellings()
    {
        WriteBacking("Data/vanilla.txt", "vanilla");
        var kernel = await Mount();
        var data = Ino(kernel, "Data");
        kernel.Resolve("DATA/VANILLA.TXT").Ok.Should().BeTrue();

        kernel.Delete("Data/vanilla.txt").Ok.Should().BeTrue();

        kernel.Invalidations.Should().Contain(Entry(data, "VANILLA.TXT"));
        kernel.Invalidations.Should().Contain(Entry(data, "vanilla.txt"));
    }

    [Fact]
    public async Task RemovingADirectoryInvalidatesRecordedSpellings()
    {
        WriteBacking("Data/Empty/.keep", "x");
        var kernel = await Mount();
        kernel.Delete("Data/Empty/.keep").Ok.Should().BeTrue();
        var data = Ino(kernel, "Data");
        kernel.Resolve("DATA/EMPTY").Ok.Should().BeTrue();

        kernel.Delete("Data/Empty").Ok.Should().BeTrue();

        kernel.Invalidations.Should().Contain(Entry(data, "EMPTY"));
    }

    [Fact]
    public async Task SpellingsAreBoundedPerName()
    {
        WriteBacking("Data/other.txt", "x");
        var kernel = await Mount();
        var data = Ino(kernel, "Data");
        // Nine spellings of one name: only the case of the letters differs.
        var spellings = new[] { "a.nif", "A.nif", "a.Nif", "A.Nif", "a.nIf", "A.nIf", "a.NIf", "A.NIf", "a.niF" };
        foreach (var spelling in spellings) kernel.Resolve($"Data/{spelling}").Error.Should().Be(Errno.ENOENT);

        kernel.Create("Data/A.Nif", 0x1A4, VfsOpenFlags.Write).Ok.Should().BeTrue();

        var dropped = kernel.Invalidations.Where(i => i.Kind == "Entry" && i.Node == data).Select(i => i.Name).ToArray();
        dropped.Should().NotContain("a.nif", "the oldest spelling was dropped to stay within the bound");
        dropped.Should().Contain(spellings.Skip(1));
    }

    [Fact]
    public async Task WritesIssueNoInvalidation()
    {
        var kernel = await Mount();
        kernel.MkDir("Data", 0x1ED).Ok.Should().BeTrue();
        var created = kernel.Create("Data/new.txt", 0x1A4, VfsOpenFlags.Write);
        var before = kernel.Invalidations.Count;

        kernel.Write(created.Value.File, 0, Encoding.UTF8.GetBytes("hello")).Ok.Should().BeTrue();
        kernel.Release(created.Value.File);
        var open = kernel.Open("Data/new.txt", VfsOpenFlags.Write);
        kernel.Write(open.Value!, 5, Encoding.UTF8.GetBytes(" again")).Ok.Should().BeTrue();
        kernel.Release(open.Value!);

        kernel.Invalidations.Count.Should().Be(before);
    }

    [Fact]
    public void ANullSinkIsFine()
    {
        WriteBacking("Data/vanilla.txt", "vanilla");
        var backing = new BackingScanner().Scan(_backing);
        var tree = VfsTree.Build(_backing, backing, [], _staging, VfsCachePolicy.Default);
        tree.Invalidation.Should().BeNull();
        var data = tree.Lookup(IVfsOperations.RootInode, "Data").Value.Ino;
        var vanilla = tree.Lookup(data, "vanilla.txt").Value.Ino;

        tree.Lookup(data, "Missing").Ok.Should().BeTrue();
        var open = tree.Open(vanilla, VfsOpenFlags.Write);
        open.Ok.Should().BeTrue();
        tree.Write(open.Value!, 0, "V"u8).Ok.Should().BeTrue();
        tree.Release(open.Value!);
        tree.SetAttr(vanilla, new VfsSetAttr(2, null, null)).Ok.Should().BeTrue();
        var created = tree.Create(data, "Missing", 0x1A4, VfsOpenFlags.Write);
        created.Ok.Should().BeTrue();
        tree.Release(created.Value.File);
        tree.MkDir(data, "Dir", 0x1ED).Ok.Should().BeTrue();
        tree.Rename(data, "Missing", data, "Renamed").Ok.Should().BeTrue();
        tree.Unlink(data, "Renamed").Ok.Should().BeTrue();
        tree.RmDir(data, "Dir").Ok.Should().BeTrue();
    }

    [Fact]
    public async Task UnmountingDetachesTheSink()
    {
        WriteBacking("Data/vanilla.txt", "vanilla");
        var backing = new BackingScanner().Scan(_backing);
        var tree = VfsTree.Build(_backing, backing, [], _staging, VfsCachePolicy.Default);
        var kernel = new FakeFuseKernel();
        var mount = await kernel.Mount(tree, FuseMountOptions.Default(_directory), CancellationToken.None);
        tree.Invalidation.Should().NotBeNull();

        await mount.Unmount(CancellationToken.None);

        tree.Invalidation.Should().BeNull();
    }
}
