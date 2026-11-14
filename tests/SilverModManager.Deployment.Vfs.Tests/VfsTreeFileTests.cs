using SilverModManager.Sdk.Hashes;
using FluentAssertions;
using SilverModManager.Deployment.Vfs.Testing;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;

namespace SilverModManager.Deployment.Vfs.Tests;

public class VfsTreeFileTests : IDisposable
{
    private readonly AbsolutePath _directory = FileSystem.Shared
        .GetKnownPath(KnownPath.TempDirectory)
        .Combine($"vfs-tree-tests-{Guid.NewGuid():N}");

    public VfsTreeFileTests() => _directory.CreateDirectory();

    public void Dispose() => _directory.DeleteDirectory(recursive: true);

    private async Task<FakeFuseKernel> MountFile(string path, AbsolutePath? realPath, long size)
    {
        var source = new VfsFileSource(RelativePath.FromUnsanitizedInput(path), VfsSourceKind.Backing, realPath, Size.From((ulong)size), FileIdentity.XxHash3(Hash.From(1)));
        var kernel = new FakeFuseKernel();
        await kernel.Mount(VfsTree.Build(_directory, [], [source], VfsCachePolicy.Default), FuseMountOptions.Default(_directory), CancellationToken.None);
        return kernel;
    }

    [Fact]
    public async Task ReadsComeFromTheRealFileAndStopAtItsEnd()
    {
        var real = _directory.Combine("a.esp");
        await real.WriteAllBytesAsync("hello"u8.ToArray());
        var kernel = await MountFile("Data/a.esp", real, 5);

        var file = kernel.Open("DATA/A.ESP", VfsOpenFlags.Read);
        file.Ok.Should().BeTrue();
        try
        {
            kernel.Read(file.Value!, 0, 5).Value.Should().Equal("hello"u8.ToArray());
            kernel.Read(file.Value!, 3, 10).Value.Should().Equal("lo"u8.ToArray());
            kernel.Read(file.Value!, 9, 10).Value.Should().BeEmpty();
        }
        finally
        {
            kernel.Release(file.Value!).Ok.Should().BeTrue();
        }
    }

    [Theory]
    [InlineData(VfsOpenFlags.Write)]
    [InlineData(VfsOpenFlags.Read | VfsOpenFlags.Truncate)]
    [InlineData(VfsOpenFlags.Read | VfsOpenFlags.Append)]
    public async Task OpeningForChangeIsEROFS(VfsOpenFlags flags)
    {
        var real = _directory.Combine("a.esp");
        await real.WriteAllBytesAsync("hello"u8.ToArray());
        var kernel = await MountFile("Data/a.esp", real, 5);

        kernel.Open("Data/a.esp", flags).Error.Should().Be(Errno.EROFS);
    }

    [Fact]
    public async Task ChangingTheTreeIsEROFS()
    {
        var kernel = await MountFile("Data/a.esp", null, 5);

        kernel.Create("Data/new.esp", 0x1A4, VfsOpenFlags.Write).Error.Should().Be(Errno.EROFS);
        kernel.MkDir("Data/New", 0x1ED).Error.Should().Be(Errno.EROFS);
        kernel.Delete("Data/a.esp").Error.Should().Be(Errno.EROFS);
        kernel.Delete("Data").Error.Should().Be(Errno.EROFS);
        kernel.Rename("Data/a.esp", "Data/b.esp").Error.Should().Be(Errno.EROFS);
    }

    [Fact]
    public async Task OpeningAFileWithNoRealPathIsEIO()
    {
        var kernel = await MountFile("Data/a.esp", null, 5);
        kernel.Open("Data/a.esp", VfsOpenFlags.Read).Error.Should().Be(Errno.EIO);
    }

    [Fact]
    public async Task OpeningADirectoryIsEISDIR()
    {
        var kernel = await MountFile("Data/a.esp", null, 5);
        kernel.Open("Data", VfsOpenFlags.Read).Error.Should().Be(Errno.EISDIR);
    }

    private static string ReadAll(FakeFuseKernel kernel, string path)
    {
        var file = kernel.Open(path, VfsOpenFlags.Read);
        file.Ok.Should().BeTrue($"{path} should open (got {file.Error})");
        try { return System.Text.Encoding.UTF8.GetString(kernel.Read(file.Value!, 0, 4096).Value!); }
        finally { kernel.Release(file.Value!); }
    }

    [Fact]
    public async Task OpenABackingFile_UsesTheDescriptorNotThePath()
    {
        var game = _directory.Combine("game");
        game.Combine("Data").CreateDirectory();
        await game.Combine("Data/vanilla.txt").WriteAllBytesAsync("vanilla"u8.ToArray());
        using var handle = DirectoryHandle.Open(game);
        var tree = VfsTree.Build(handle, new BackingScanner().Scan(handle), [], VfsCachePolicy.Default);
        var kernel = new FakeFuseKernel();
        await kernel.Mount(tree, FuseMountOptions.Default(_directory), CancellationToken.None);

        // Whatever gets mounted over the folder, its path no longer leads to the backing files.
        Directory.Move(game.ToString(), _directory.Combine("renamed").ToString());

        ReadAll(kernel, "Data/vanilla.txt").Should().Be("vanilla");
    }

    [Fact]
    public async Task AFileUnderASymlinkedDirectory_StillServesAfterTheRootIsRenamed()
    {
        var game = _directory.Combine("game");
        game.CreateDirectory();
        var elsewhere = _directory.Combine("elsewhere");
        elsewhere.CreateDirectory();
        await elsewhere.Combine("file.txt").WriteAllBytesAsync("linked"u8.ToArray());
        File.CreateSymbolicLink(game.Combine("link").ToString(), "../elsewhere");
        using var handle = DirectoryHandle.Open(game);
        var tree = VfsTree.Build(handle, new BackingScanner().Scan(handle), [], VfsCachePolicy.Default);
        var kernel = new FakeFuseKernel();
        await kernel.Mount(tree, FuseMountOptions.Default(_directory), CancellationToken.None);

        // Both moved, so neither the root's path nor the link's target leads anywhere by name.
        Directory.Move(game.ToString(), _directory.Combine("renamed").ToString());
        Directory.Move(elsewhere.ToString(), _directory.Combine("moved").ToString());

        ReadAll(kernel, "link/file.txt").Should().Be("linked");
    }

    [Fact]
    public async Task OpenWithNoHandleAndNoPath_FailsWithEIO()
    {
        var source = new VfsFileSource(RelativePath.FromUnsanitizedInput("Data/a.esp"), VfsSourceKind.Backing, RealPath: null, Size.From(1), FileIdentity.XxHash3(Hash.From(1)));
        var kernel = new FakeFuseKernel();
        await kernel.Mount(VfsTree.Build(_directory, [], [source], VfsCachePolicy.Default), FuseMountOptions.Default(_directory), CancellationToken.None);

        kernel.Open("Data/a.esp", VfsOpenFlags.Read).Error.Should().Be(Errno.EIO);
    }
}
