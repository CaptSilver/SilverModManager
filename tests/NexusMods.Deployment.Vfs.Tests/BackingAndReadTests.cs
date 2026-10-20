using FluentAssertions;
using NexusMods.Deployment.Vfs.Testing;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;

namespace NexusMods.Deployment.Vfs.Tests;

public class BackingAndReadTests : IDisposable
{
    private readonly AbsolutePath _directory = FileSystem.Shared
        .GetKnownPath(KnownPath.TempDirectory)
        .Combine($"vfs-backing-tests-{Guid.NewGuid():N}");

    private readonly AbsolutePath _backing;
    private readonly AbsolutePath _staged;

    public BackingAndReadTests()
    {
        _backing = _directory.Combine("game");
        _staged = _directory.Combine("staged");
        _backing.CreateDirectory();
        _staged.CreateDirectory();
    }

    public void Dispose() => _directory.DeleteDirectory(recursive: true);

    private async Task<AbsolutePath> WriteBacking(string path, string text)
    {
        var file = _backing.Combine(path);
        file.Parent.CreateDirectory();
        await file.WriteAllBytesAsync(System.Text.Encoding.UTF8.GetBytes(text));
        return file;
    }

    private async Task<VfsFileSource> StagedSource(string path, string text)
    {
        var file = _staged.Combine(Guid.NewGuid().ToString("N"));
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        await file.WriteAllBytesAsync(bytes);
        return new VfsFileSource(RelativePath.FromUnsanitizedInput(path), VfsSourceKind.Staged, file, Size.From((ulong)bytes.Length), Hash.From(2));
    }

    private async Task<FakeFuseKernel> Mount(IReadOnlyList<VfsFileSource> sources, VfsCachePolicy? policy = null)
    {
        var backing = new BackingScanner().Scan(_backing);
        var kernel = new FakeFuseKernel();
        await kernel.Mount(VfsTree.Build(_backing, backing, sources, policy ?? VfsCachePolicy.Default), FuseMountOptions.Default(_directory), CancellationToken.None);
        return kernel;
    }

    private static string ReadAll(FakeFuseKernel kernel, string path)
    {
        var file = kernel.Open(path, VfsOpenFlags.Read);
        file.Ok.Should().BeTrue();
        try { return System.Text.Encoding.UTF8.GetString(kernel.Read(file.Value!, 0, 4096).Value!); }
        finally { kernel.Release(file.Value!); }
    }

    [Fact]
    public async Task BackingAndStagedFilesAreServedWithTheirOwnAttributes()
    {
        var vanilla = await WriteBacking("Data/vanilla.txt", "vanilla");
        File.SetUnixFileMode(vanilla.ToString(), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var mod = await StagedSource("Data/mod.txt", "mod");
        var kernel = await Mount([mod]);

        var vanillaAttr = kernel.Resolve("Data/vanilla.txt").Value.Attr;
        vanillaAttr.Size.Should().Be(7);
        (vanillaAttr.Mode & 0xFFF).Should().Be(0x180, "the backing file's own 0600 is what the scanner saw");
        vanillaAttr.MTime.Should().BeCloseTo(File.GetLastWriteTimeUtc(vanilla.ToString()), TimeSpan.FromSeconds(1));

        var modAttr = kernel.Resolve("Data/mod.txt").Value.Attr;
        modAttr.Size.Should().Be(3);
        (modAttr.Mode & 0xFFF).Should().Be(0x1A4);
        modAttr.MTime.Should().BeCloseTo(File.GetLastWriteTimeUtc(mod.RealPath!.Value.ToString()), TimeSpan.FromSeconds(1));

        ReadAll(kernel, "Data/vanilla.txt").Should().Be("vanilla");
        ReadAll(kernel, "Data/mod.txt").Should().Be("mod");
        (kernel.Resolve("Data").Value.Attr.Mode & 0xFFF).Should().Be(0x1ED);
    }

    [Fact]
    public async Task EmptyBackingDirectoriesAreListed()
    {
        _backing.Combine("Data/Empty").CreateDirectory();
        var kernel = await Mount([]);

        kernel.List("Data").Value!.Select(e => e.Name).Should().Equal("Empty");
    }

    [Fact]
    public async Task AStagedFileWinsOverTheBackingFile()
    {
        await WriteBacking("Data/vanilla.txt", "vanilla");
        var kernel = await Mount([await StagedSource("Data/vanilla.txt", "mod")]);

        ReadAll(kernel, "Data/vanilla.txt").Should().Be("mod");
        kernel.Resolve("Data/vanilla.txt").Value.Attr.Size.Should().Be(3);
    }

    [Fact]
    public async Task HiddenSourceRemovesABackingFile()
    {
        await WriteBacking("Data/vanilla.txt", "vanilla");
        var hidden = new VfsFileSource(RelativePath.FromUnsanitizedInput("data/VANILLA.txt"), VfsSourceKind.Hidden, null, Size.Zero, Hash.Zero);
        var kernel = await Mount([hidden]);

        kernel.Resolve("Data/vanilla.txt").Error.Should().Be(Errno.ENOENT);
    }

    [Fact]
    public async Task DefaultPolicyStampsDaysOnEntriesAndAnHourOnMisses()
    {
        await WriteBacking("Data/vanilla.txt", "vanilla");
        var kernel = await Mount([]);

        var data = kernel.Operations.Lookup(IVfsOperations.RootInode, "Data").Value;
        var file = kernel.Operations.Lookup(data.Ino, "vanilla.txt").Value;
        file.EntryTtl.Should().Be(TimeSpan.FromHours(24));
        file.AttrTtl.Should().Be(TimeSpan.FromHours(24));

        var miss = kernel.Operations.Lookup(data.Ino, "missing.txt");
        miss.Ok.Should().BeTrue("a cached negative entry is a reply with inode 0, not an error");
        miss.Value.Ino.Should().Be(0);
        miss.Value.EntryTtl.Should().Be(TimeSpan.FromHours(1));

        var opened = kernel.Open("Data/vanilla.txt", VfsOpenFlags.Read).Value!;
        opened.KeepCache.Should().BeTrue();
        opened.DirectIo.Should().BeFalse();
        kernel.Release(opened);
    }

    [Fact]
    public async Task DisabledPolicyStampsZeroAndDoesNotKeepCache()
    {
        await WriteBacking("Data/vanilla.txt", "vanilla");
        var kernel = await Mount([], VfsCachePolicy.Disabled);

        var data = kernel.Operations.Lookup(IVfsOperations.RootInode, "Data").Value;
        var file = kernel.Operations.Lookup(data.Ino, "vanilla.txt").Value;
        file.EntryTtl.Should().Be(TimeSpan.Zero);
        file.AttrTtl.Should().Be(TimeSpan.Zero);
        kernel.Operations.Lookup(data.Ino, "missing.txt").Value.EntryTtl.Should().Be(TimeSpan.Zero);

        var opened = kernel.Open("Data/vanilla.txt", VfsOpenFlags.Read).Value!;
        opened.KeepCache.Should().BeFalse();
        kernel.Release(opened);
    }

    [Fact]
    public async Task CaseVariantsOfOneNameAreTheSameInode()
    {
        await WriteBacking("Data/vanilla.txt", "vanilla");
        var kernel = await Mount([]);

        var first = kernel.Resolve("DATA/VANILLA.TXT").Value;
        var lookupsAfterFirst = kernel.Lookups.Count;
        var second = kernel.Resolve("data/Vanilla.TXT").Value;

        lookupsAfterFirst.Should().Be(2);
        kernel.Lookups.Count.Should().Be(4, "each Resolve costs one Lookup per segment");
        second.Ino.Should().Be(first.Ino);
        kernel.Operations.GetAttr(second.Ino).Value.Should().Be(second.Attr);
    }

    [Fact]
    public async Task ReadsAreAnsweredWithTheOpenHandle()
    {
        await WriteBacking("Data/vanilla.txt", "vanilla");
        var kernel = await Mount([]);

        var file = kernel.Open("Data/vanilla.txt", VfsOpenFlags.Read).Value!;
        var reply = kernel.Operations.Read(file, 2, 100).Value;
        reply.Handle.Should().BeSameAs(file.Handle);
        reply.Offset.Should().Be(2);
        reply.Length.Should().Be(5);
        kernel.Release(file);
    }
}
