using FluentAssertions;
using SilverModManager.Deployment.Vfs.Testing;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;

namespace SilverModManager.Deployment.Vfs.Tests;

public class VfsTreeTests
{
    private static VfsFileSource Backing(string path) =>
        new(RelativePath.FromUnsanitizedInput(path), VfsSourceKind.Backing, null, Size.From(3), Hash.From(1));

    private static VfsFileSource Hidden(string path) =>
        new(RelativePath.FromUnsanitizedInput(path), VfsSourceKind.Hidden, null, Size.Zero, Hash.From(0));

    private static async Task<FakeFuseKernel> Mount(params VfsFileSource[] sources)
    {
        var kernel = new FakeFuseKernel();
        var tree = VfsTree.Build(default, [], sources, VfsCachePolicy.Default);
        await kernel.Mount(tree, FuseMountOptions.Default(FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory)), CancellationToken.None);
        return kernel;
    }

    [Fact]
    public async Task DirectoriesKeepTheFirstSpellingAndMergeCaseVariants()
    {
        var kernel = await Mount(Backing("Data/Textures/a.dds"), Backing("data/textures/B.dds"));

        var data = kernel.List("Data");
        data.Ok.Should().BeTrue();
        data.Value!.Select(e => e.Name).Should().Equal("Textures");

        var textures = kernel.List("Data/Textures");
        textures.Value!.Select(e => e.Name).Should().BeEquivalentTo("a.dds", "B.dds");

        kernel.Resolve("DATA/TEXTURES/A.DDS").Ok.Should().BeTrue();
    }

    [Fact]
    public async Task HiddenSourceRemovesAnEarlierNode()
    {
        var kernel = await Mount(Backing("Data/x.esp"), Backing("Data/y.esp"), Hidden("Data/x.esp"));

        kernel.Resolve("Data/x.esp").Error.Should().Be(Errno.ENOENT);
        kernel.List("Data").Value!.Select(e => e.Name).Should().Equal("y.esp");
    }

    [Fact]
    public async Task MissingNameIsENOENT()
    {
        var kernel = await Mount(Backing("Data/a.esp"));
        kernel.Resolve("Data/missing").Error.Should().Be(Errno.ENOENT);
    }

    [Fact]
    public async Task WalkingThroughAFileIsENOTDIR()
    {
        var kernel = await Mount(Backing("Data/Textures/a.dds"));
        kernel.Resolve("Data/Textures/a.dds/child").Error.Should().Be(Errno.ENOTDIR);
    }

    [Fact]
    public async Task KeysAreWholeSegments()
    {
        var kernel = await Mount(Backing("modsX/a.esp"));
        kernel.Resolve("mods").Error.Should().Be(Errno.ENOENT);
        kernel.Resolve("modsX/a.esp").Ok.Should().BeTrue();
    }

    [Fact]
    public async Task EveryLookupIsRecorded()
    {
        var kernel = await Mount(Backing("Data/a.esp"));
        kernel.Resolve("Data/a.esp");
        kernel.Lookups.Select(l => l.Name).Should().Equal("Data", "a.esp");
    }

    [Fact]
    public async Task InodesAreStableAcrossLookups()
    {
        var kernel = await Mount(Backing("Data/a.esp"));
        kernel.Resolve("DATA/a.esp").Value.Ino.Should().Be(kernel.Resolve("Data/A.ESP").Value.Ino);
    }

    [Fact]
    public async Task ReadDirPlusPagesThroughAllEntries()
    {
        var kernel = await Mount(Backing("d/a"), Backing("d/b"), Backing("d/c"));
        var dir = kernel.Resolve("d").Value;
        var tree = kernel.Operations;
        var first = tree.ReadDirPlus(dir.Ino, 0, 2).Value.Entries;
        first.Should().HaveCount(2);
        var rest = tree.ReadDirPlus(dir.Ino, first[^1].NextOffset, 2).Value.Entries;
        rest.Should().HaveCount(1);
        tree.ReadDirPlus(dir.Ino, rest[^1].NextOffset, 2).Value.Entries.Should().BeEmpty();
    }
}
