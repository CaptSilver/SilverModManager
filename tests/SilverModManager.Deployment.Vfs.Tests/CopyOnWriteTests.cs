using System.Text;
using FluentAssertions;
using SilverModManager.Deployment.Vfs.Testing;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Deployment.Vfs.Tests;

public class CopyOnWriteTests : IDisposable
{
    private readonly AbsolutePath _directory = FileSystem.Shared
        .GetKnownPath(KnownPath.TempDirectory)
        .Combine($"vfs-cow-tests-{Guid.NewGuid():N}");

    private readonly AbsolutePath _backing;
    private readonly AbsolutePath _loose;
    private readonly AbsolutePath _stagingLocation;
    private readonly StagingDirectory _staging;

    public CopyOnWriteTests()
    {
        _backing = _directory.Combine("game");
        _loose = _directory.Combine("loose");
        _stagingLocation = _directory.Combine("staging");
        _backing.CreateDirectory();
        _loose.CreateDirectory();
        _staging = new StagingDirectory(_stagingLocation, GameInstallMetadataId.From(0xABC));
    }

    public void Dispose()
    {
        // Read-only fixtures cannot be deleted from inside a read-only parent, so open them up first.
        foreach (var file in Directory.EnumerateFiles(_directory.ToString(), "*", SearchOption.AllDirectories))
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _directory.DeleteDirectory(recursive: true);
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private AbsolutePath WriteBacking(string path, string text, UnixFileMode? mode = null)
    {
        var file = _backing.Combine(path);
        file.Parent.CreateDirectory();
        File.WriteAllBytes(file.ToString(), Bytes(text));
        if (mode is { } m) File.SetUnixFileMode(file.ToString(), m);
        return file;
    }

    private VfsFileSource LooseSource(string path, string text, UnixFileMode? mode = null)
    {
        var file = _loose.Combine(Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(file.ToString(), Bytes(text));
        if (mode is { } m) File.SetUnixFileMode(file.ToString(), m);
        return new VfsFileSource(RelativePath.FromUnsanitizedInput(path), VfsSourceKind.Staged, file, Size.From((ulong)Bytes(text).Length), Hash.From(2));
    }

    private async Task<FakeFuseKernel> Mount(params VfsFileSource[] sources)
    {
        var backing = new BackingScanner().Scan(_backing);
        var kernel = new FakeFuseKernel();
        await kernel.Mount(VfsTree.Build(_backing, backing, sources, _staging, VfsCachePolicy.Default), FuseMountOptions.Default(_directory), CancellationToken.None);
        return kernel;
    }

    private static string ReadAll(FakeFuseKernel kernel, string path)
    {
        var file = kernel.Open(path, VfsOpenFlags.Read);
        file.Ok.Should().BeTrue($"{path} should open (got {file.Error})");
        try { return Encoding.UTF8.GetString(kernel.Read(file.Value!, 0, 4096).Value!); }
        finally { kernel.Release(file.Value!); }
    }

    private static void WriteThrough(FakeFuseKernel kernel, string path, string text, ulong offset = 0, VfsOpenFlags flags = VfsOpenFlags.Write)
    {
        var file = kernel.Open(path, flags);
        file.Ok.Should().BeTrue($"{path} should open for writing (got {file.Error})");
        try { kernel.Write(file.Value!, offset, Bytes(text)).Value.Should().Be(Bytes(text).Length); }
        finally { kernel.Release(file.Value!); }
    }

    private string StagedText(string path) => File.ReadAllText(_staging.PathFor(RelativePath.FromUnsanitizedInput(path)).ToString());

    [Fact]
    public async Task WritingABackingFileChangesOnlyItsStagingCopy()
    {
        var vanilla = WriteBacking("Data/vanilla.txt", "vanilla", UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var kernel = await Mount();

        WriteThrough(kernel, "Data/vanilla.txt", "changed");

        File.ReadAllText(vanilla.ToString()).Should().Be("vanilla");
        StagedText("Data/vanilla.txt").Should().Be("changed");
        ReadAll(kernel, "Data/vanilla.txt").Should().Be("changed");
        (File.GetUnixFileMode(_staging.PathFor(RelativePath.FromUnsanitizedInput("Data/vanilla.txt")).ToString()) & UnixFileMode.UserWrite)
            .Should().NotBe(0, "the copy must be writable whatever the source's mode was");
    }

    [Fact]
    public async Task WritingALooseStoreFileChangesOnlyItsStagingCopy()
    {
        var source = LooseSource("Data/mod.esp", "mod", UnixFileMode.UserRead);
        var kernel = await Mount(source);

        WriteThrough(kernel, "Data/mod.esp", "MOD");

        File.ReadAllText(source.RealPath!.Value.ToString()).Should().Be("mod");
        StagedText("Data/mod.esp").Should().Be("MOD");
        ReadAll(kernel, "Data/mod.esp").Should().Be("MOD");
    }

    [Fact]
    public async Task AWriteInTheMiddleKeepsTheRestOfTheOriginal()
    {
        WriteBacking("Data/vanilla.txt", "vanilla");
        var kernel = await Mount();

        WriteThrough(kernel, "Data/vanilla.txt", "AN", offset: 1);

        ReadAll(kernel, "Data/vanilla.txt").Should().Be("vANilla");
    }

    [Fact]
    public async Task OpeningWithTruncateEmptiesOnlyTheStagingCopy()
    {
        var vanilla = WriteBacking("Data/vanilla.txt", "vanilla");
        var kernel = await Mount();

        var file = kernel.Open("Data/vanilla.txt", VfsOpenFlags.Write | VfsOpenFlags.Truncate).Value!;
        kernel.Release(file);

        ReadAll(kernel, "Data/vanilla.txt").Should().BeEmpty();
        kernel.Resolve("Data/vanilla.txt").Value.Attr.Size.Should().Be(0);
        File.ReadAllText(vanilla.ToString()).Should().Be("vanilla");
    }

    [Fact]
    public async Task CreatingAFileWritesOnlyIntoStaging()
    {
        WriteBacking("Data/vanilla.txt", "vanilla");
        var kernel = await Mount();

        var created = kernel.Create("Data/new.ini", 0x1A4, VfsOpenFlags.Write).Value;
        kernel.Write(created.File, 0, Bytes("[a]"));
        kernel.Release(created.File);

        StagedText("Data/new.ini").Should().Be("[a]");
        _backing.Combine("Data/new.ini").FileExists.Should().BeFalse();
        ReadAll(kernel, "Data/new.ini").Should().Be("[a]");
        kernel.Resolve("Data/new.ini").Value.Attr.Size.Should().Be(3, "sizes are read from the staged file, not remembered");
        kernel.List("Data").Value!.Select(e => e.Name).Should().BeEquivalentTo("vanilla.txt", "new.ini");
    }

    [Fact]
    public async Task CreatingAnExistingNameFails()
    {
        WriteBacking("Data/vanilla.txt", "vanilla");
        var kernel = await Mount();

        kernel.Create("DATA/VANILLA.TXT", 0x1A4, VfsOpenFlags.Write).Error.Should().Be(Errno.EEXIST);
    }

    [Fact]
    public async Task DeletingABackingFileWhiteoutsItAndLeavesItOnDisk()
    {
        var vanilla = WriteBacking("Data/vanilla.txt", "vanilla");
        var kernel = await Mount();

        kernel.Delete("Data/vanilla.txt").Ok.Should().BeTrue();

        kernel.Resolve("Data/vanilla.txt").Error.Should().Be(Errno.ENOENT);
        kernel.List("Data").Value!.Should().BeEmpty();
        _staging.ReadWhiteouts().Select(p => p.ToString()).Should().Equal("Data/vanilla.txt");
        vanilla.FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task AFileCalledWhiteoutsAtTheMountRootDoesNotCollideWithTheList()
    {
        WriteBacking("Data/vanilla.txt", "vanilla");
        var kernel = await Mount();

        var created = kernel.Create(".whiteouts", 0x1A4, VfsOpenFlags.Write).Value;
        kernel.Write(created.File, 0, Bytes("game data"));
        kernel.Release(created.File);
        kernel.Delete("Data/vanilla.txt").Ok.Should().BeTrue();

        ReadAll(kernel, ".whiteouts").Should().Be("game data");
        _staging.ReadWhiteouts().Select(p => p.ToString()).Should().Equal("Data/vanilla.txt");
        _staging.PathFor(RelativePath.FromUnsanitizedInput(".whiteouts")).FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task DeletingAFileTheGameCreatedLeavesNoWhiteout()
    {
        var kernel = await Mount();
        var created = kernel.Create("new.ini", 0x1A4, VfsOpenFlags.Write).Value;
        kernel.Release(created.File);

        kernel.Delete("new.ini").Ok.Should().BeTrue();

        kernel.Resolve("new.ini").Error.Should().Be(Errno.ENOENT);
        _staging.PathFor(RelativePath.FromUnsanitizedInput("new.ini")).FileExists.Should().BeFalse();
        _staging.ReadWhiteouts().Should().BeEmpty();
    }

    [Fact]
    public async Task DeletingAWrittenBackingFileRemovesTheCopyAndKeepsTheOriginalHidden()
    {
        var vanilla = WriteBacking("Data/vanilla.txt", "vanilla");
        var kernel = await Mount();
        WriteThrough(kernel, "Data/vanilla.txt", "changed");

        kernel.Delete("Data/vanilla.txt").Ok.Should().BeTrue();

        kernel.Resolve("Data/vanilla.txt").Error.Should().Be(Errno.ENOENT);
        _staging.PathFor(RelativePath.FromUnsanitizedInput("Data/vanilla.txt")).FileExists.Should().BeFalse();
        _staging.ReadWhiteouts().Select(p => p.ToString()).Should().Equal("Data/vanilla.txt");
        vanilla.FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task RenamingABackingFileCopiesItUnderTheNewNameAndWhiteoutsTheOld()
    {
        var vanilla = WriteBacking("Data/vanilla.txt", "vanilla");
        var kernel = await Mount();

        kernel.Rename("Data/vanilla.txt", "Data/renamed.txt").Ok.Should().BeTrue();

        ReadAll(kernel, "Data/renamed.txt").Should().Be("vanilla");
        StagedText("Data/renamed.txt").Should().Be("vanilla");
        kernel.Resolve("Data/vanilla.txt").Error.Should().Be(Errno.ENOENT);
        _staging.ReadWhiteouts().Select(p => p.ToString()).Should().Equal("Data/vanilla.txt");
        File.ReadAllText(vanilla.ToString()).Should().Be("vanilla");
    }

    [Fact]
    public async Task RenamingAStagedFileIsAPlainRename()
    {
        var kernel = await Mount();
        var created = kernel.Create("a.ini", 0x1A4, VfsOpenFlags.Write).Value;
        kernel.Write(created.File, 0, Bytes("x"));
        kernel.Release(created.File);

        kernel.Rename("a.ini", "b.ini").Ok.Should().BeTrue();

        ReadAll(kernel, "b.ini").Should().Be("x");
        _staging.PathFor(RelativePath.FromUnsanitizedInput("a.ini")).FileExists.Should().BeFalse();
        _staging.ReadWhiteouts().Should().BeEmpty();
    }

    [Fact]
    public async Task RenamingOverAnExistingFileReplacesIt()
    {
        WriteBacking("Data/a.txt", "a");
        WriteBacking("Data/b.txt", "b");
        var kernel = await Mount();

        kernel.Rename("Data/a.txt", "Data/b.txt").Ok.Should().BeTrue();

        ReadAll(kernel, "Data/b.txt").Should().Be("a");
        kernel.Resolve("Data/a.txt").Error.Should().Be(Errno.ENOENT);
    }

    [Fact]
    public async Task ARemountShowsWhatTheGameDid()
    {
        var vanilla = WriteBacking("Data/vanilla.txt", "vanilla");
        WriteBacking("Data/other.txt", "other");
        var first = await Mount();
        var created = first.Create("Data/new.ini", 0x1A4, VfsOpenFlags.Write).Value;
        first.Write(created.File, 0, Bytes("[a]"));
        first.Release(created.File);
        first.Rename("Data/vanilla.txt", "Data/renamed.txt").Ok.Should().BeTrue();
        first.MkDir("Data/Saves", 0x1ED).Ok.Should().BeTrue();

        var second = await Mount();

        second.List("Data").Value!.Select(e => e.Name).Should().BeEquivalentTo("new.ini", "renamed.txt", "other.txt", "Saves");
        second.Resolve("Data/vanilla.txt").Error.Should().Be(Errno.ENOENT);
        ReadAll(second, "Data/new.ini").Should().Be("[a]");
        ReadAll(second, "Data/renamed.txt").Should().Be("vanilla");
        vanilla.FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task ARemountKeepsAWrittenFileOverItsBackingOriginal()
    {
        WriteBacking("Data/vanilla.txt", "vanilla");
        var first = await Mount();
        WriteThrough(first, "Data/vanilla.txt", "changed");

        var second = await Mount();

        ReadAll(second, "Data/vanilla.txt").Should().Be("changed");
    }

    [Fact]
    public async Task ADeletedThenRecreatedNameIsVisibleAfterARemount()
    {
        WriteBacking("Data/vanilla.txt", "vanilla");
        var first = await Mount();
        first.Delete("Data/vanilla.txt").Ok.Should().BeTrue();
        var created = first.Create("Data/vanilla.txt", 0x1A4, VfsOpenFlags.Write).Value;
        first.Write(created.File, 0, Bytes("new"));
        first.Release(created.File);

        var second = await Mount();

        ReadAll(second, "Data/vanilla.txt").Should().Be("new");
        second.Delete("Data/vanilla.txt").Ok.Should().BeTrue();
        (await Mount()).Resolve("Data/vanilla.txt").Error.Should().Be(Errno.ENOENT, "the original underneath stays hidden");
    }

    [Fact]
    public async Task ATruncatingSetAttrCopiesBeforeShrinking()
    {
        var vanilla = WriteBacking("Data/vanilla.txt", "vanilla");
        var kernel = await Mount();
        var entry = kernel.Resolve("Data/vanilla.txt").Value;

        var attr = kernel.Operations.SetAttr(entry.Ino, new VfsSetAttr(3, null, null));

        attr.Value.Size.Should().Be(3);
        new FileInfo(_staging.PathFor(RelativePath.FromUnsanitizedInput("Data/vanilla.txt")).ToString()).Length.Should().Be(3);
        File.ReadAllText(vanilla.ToString()).Should().Be("vanilla");
        ReadAll(kernel, "Data/vanilla.txt").Should().Be("van");
    }

    [Fact]
    public async Task SetAttrCanGrowAFile()
    {
        WriteBacking("Data/vanilla.txt", "ab");
        var kernel = await Mount();
        var entry = kernel.Resolve("Data/vanilla.txt").Value;

        kernel.Operations.SetAttr(entry.Ino, new VfsSetAttr(4, null, null)).Value.Size.Should().Be(4);
    }

    [Fact]
    public async Task SetAttrWithoutAChangeLeavesTheFileUnstaged()
    {
        WriteBacking("Data/vanilla.txt", "vanilla");
        var kernel = await Mount();
        var entry = kernel.Resolve("Data/vanilla.txt").Value;

        kernel.Operations.SetAttr(entry.Ino, new VfsSetAttr(null, null, null)).Ok.Should().BeTrue();

        _staging.PathFor(RelativePath.FromUnsanitizedInput("Data/vanilla.txt")).FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task SetAttrCanChangeTheModificationTime()
    {
        WriteBacking("Data/vanilla.txt", "vanilla");
        var kernel = await Mount();
        var entry = kernel.Resolve("Data/vanilla.txt").Value;
        var when = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);

        kernel.Operations.SetAttr(entry.Ino, new VfsSetAttr(null, when, null)).Value.MTime.Should().Be(when);
    }

    [Fact]
    public async Task MakingAndRemovingADirectoryWorks()
    {
        WriteBacking("Data/vanilla.txt", "vanilla");
        var kernel = await Mount();

        kernel.MkDir("Data/Saves", 0x1ED).Ok.Should().BeTrue();
        kernel.MkDir("data/saves", 0x1ED).Error.Should().Be(Errno.EEXIST);
        _staging.PathFor(RelativePath.FromUnsanitizedInput("Data/Saves")).DirectoryExists().Should().BeTrue();
        kernel.Delete("Data/Saves").Ok.Should().BeTrue();
        kernel.Resolve("Data/Saves").Error.Should().Be(Errno.ENOENT);
        _staging.ReadWhiteouts().Should().BeEmpty();
    }

    [Fact]
    public async Task RemovingANonEmptyDirectoryFails()
    {
        WriteBacking("Data/vanilla.txt", "vanilla");
        var kernel = await Mount();

        kernel.Delete("Data").Error.Should().Be(Errno.ENOTEMPTY);
    }

    [Fact]
    public async Task RemovingAnEmptiedBackingDirectoryWhiteoutsIt()
    {
        WriteBacking("Data/vanilla.txt", "vanilla");
        var kernel = await Mount();
        kernel.Delete("Data/vanilla.txt").Ok.Should().BeTrue();

        kernel.Delete("Data").Ok.Should().BeTrue();

        (await Mount()).Resolve("Data").Error.Should().Be(Errno.ENOENT);
    }

    [Fact]
    public async Task RenamingAStagedDirectoryMovesItsFiles()
    {
        var kernel = await Mount();
        kernel.MkDir("Saves", 0x1ED).Ok.Should().BeTrue();
        var created = kernel.Create("Saves/slot1.sav", 0x1A4, VfsOpenFlags.Write).Value;
        kernel.Write(created.File, 0, Bytes("save"));
        kernel.Release(created.File);

        kernel.Rename("Saves", "Backups").Ok.Should().BeTrue();

        ReadAll(kernel, "Backups/slot1.sav").Should().Be("save");
        kernel.Resolve("Saves").Error.Should().Be(Errno.ENOENT);
        StagedText("Backups/slot1.sav").Should().Be("save");
        (await Mount()).List("").Value!.Select(e => e.Name).Should().Equal("Backups");
    }

    [Fact]
    public async Task RenamingADirectoryThatHoldsBackingFilesIsRefused()
    {
        WriteBacking("Data/vanilla.txt", "vanilla");
        var kernel = await Mount();

        kernel.Rename("Data", "Moved").Error.Should().Be(Errno.EXDEV);
        kernel.Resolve("Data/vanilla.txt").Ok.Should().BeTrue();
    }

    private void WriteStagedOnDisk(string path, string text)
    {
        var file = _staging.Root.Combine(path);
        file.Parent.CreateDirectory();
        File.WriteAllText(file.ToString(), text);
    }

    [Fact]
    public async Task RenamingAStagedFileSpelledDifferentlyOnDiskMovesIt()
    {
        // The tree spells the directory as the backing directory does; staging has it in lower case.
        WriteBacking("Data/vanilla.txt", "vanilla");
        WriteStagedOnDisk("data/new.txt", "new");
        var kernel = await Mount();

        kernel.Rename("Data/new.txt", "Data/other.txt").Ok.Should().BeTrue();

        ReadAll(kernel, "Data/other.txt").Should().Be("new");
        kernel.Resolve("Data/new.txt").Error.Should().Be(Errno.ENOENT);
        var remounted = await Mount();
        remounted.Resolve("Data/new.txt").Error.Should().Be(Errno.ENOENT);
        ReadAll(remounted, "Data/other.txt").Should().Be("new");
    }

    [Fact]
    public async Task RenamingAStagedDirectorySpelledDifferentlyOnDiskIsRefused()
    {
        WriteBacking("Data/vanilla.txt", "vanilla");
        WriteStagedOnDisk("data/Sub/x.txt", "x");
        var kernel = await Mount();

        kernel.Rename("Data/Sub", "Data/Moved").Error.Should().Be(Errno.EXDEV);

        ReadAll(kernel, "Data/Sub/x.txt").Should().Be("x");
    }

    [Fact]
    public async Task RemovingADirectoryMovedOverAnEmptiedBackingDirectoryKeepsTheBackingOneHidden()
    {
        WriteBacking("Data/Sub/vanilla.txt", "vanilla");
        var kernel = await Mount();
        kernel.Delete("Data/Sub/vanilla.txt").Ok.Should().BeTrue();
        kernel.MkDir("Data/New", 0x1ED).Ok.Should().BeTrue();
        kernel.Rename("Data/New", "Data/Sub").Ok.Should().BeTrue();

        kernel.Delete("Data/Sub").Ok.Should().BeTrue();

        (await Mount()).Resolve("Data/Sub").Error.Should().Be(Errno.ENOENT);
    }

    [Fact]
    public async Task WritingThroughAReadOnlyOpenFails()
    {
        WriteBacking("Data/vanilla.txt", "vanilla");
        var kernel = await Mount();
        var file = kernel.Open("Data/vanilla.txt", VfsOpenFlags.Read).Value!;
        try { kernel.Write(file, 0, Bytes("x")).Error.Should().Be(Errno.EACCES); }
        finally { kernel.Release(file); }
    }

    [Fact]
    public async Task StagingScanListsWhatTheGameCreated()
    {
        var kernel = await Mount();
        var created = kernel.Create("new.ini", 0x1A4, VfsOpenFlags.Write).Value;
        kernel.Release(created.File);
        var backing = new BackingScanner().Scan(_backing);

        _staging.Scan().Select(e => e.Path.ToString()).Should().Equal("new.ini");
        VfsTree.Build(_backing, backing, [], _staging, VfsCachePolicy.Default).Should().NotBeNull();
    }

    [Fact]
    public async Task TheTreeWithoutAStagingDirectoryStaysReadOnly()
    {
        WriteBacking("Data/vanilla.txt", "vanilla");
        var kernel = new FakeFuseKernel();
        await kernel.Mount(VfsTree.Build(_backing, new BackingScanner().Scan(_backing), [], VfsCachePolicy.Default), FuseMountOptions.Default(_directory), CancellationToken.None);

        kernel.Open("Data/vanilla.txt", VfsOpenFlags.Write).Error.Should().Be(Errno.EROFS);
        kernel.Delete("Data/vanilla.txt").Error.Should().Be(Errno.EROFS);
    }

    [Fact]
    public void ClearRemovesStagedFilesAndTheWhiteoutList()
    {
        _staging.PathFor(RelativePath.FromUnsanitizedInput("Data/x.txt")).Parent.CreateDirectory();
        File.WriteAllText(_staging.PathFor(RelativePath.FromUnsanitizedInput("Data/x.txt")).ToString(), "x");
        _staging.WriteWhiteouts([RelativePath.FromUnsanitizedInput("Data/y.txt")]);

        _staging.Clear();

        _staging.Root.DirectoryExists().Should().BeFalse();
        _staging.WhiteoutFile.FileExists.Should().BeFalse();
        _staging.ReadWhiteouts().Should().BeEmpty();
    }

    [Fact]
    public void WhiteoutsAreWrittenSortedOrdinallyOnePerLine()
    {
        _staging.WriteWhiteouts([RelativePath.FromUnsanitizedInput("b/x"), RelativePath.FromUnsanitizedInput("B/a"), RelativePath.FromUnsanitizedInput("a")]);

        File.ReadAllLines(_staging.WhiteoutFile.ToString()).Should().Equal("B/a", "a", "b/x");
        // The rename leaves no temp file next to the list.
        _staging.WhiteoutFile.Parent.EnumerateFiles().Select(f => f.FileName).Should().Equal([_staging.WhiteoutFile.FileName]);
    }
}
