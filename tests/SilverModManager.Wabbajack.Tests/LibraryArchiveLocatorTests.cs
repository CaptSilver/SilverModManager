using System.IO.Compression;
using FluentAssertions;
using SilverModManager.Abstractions.Library;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Library;

namespace SilverModManager.Wabbajack.Tests;

public class LibraryArchiveLocatorTests(ILibraryService libraryService, IConnection connection, ILibraryArchiveLocator locator, TemporaryFileManager temporaryFileManager)
{
    [Fact]
    public async Task FindsAnArchiveByItsXxHash64()
    {
        await using var folder = temporaryFileManager.CreateFolder();
        var path = folder.Path.Combine("pack.zip");
        using (var zip = new ZipArchive(File.Create(path.ToString()), ZipArchiveMode.Create))
        {
            using var entry = zip.CreateEntry("a.txt").Open();
            entry.Write("hello"u8);
        }
        var expected = await Hash64(path);

        var added = await libraryService.AddLocalFile(path, UnassignedAcquisition.Instance);

        var found = locator.FindByXxHash64(connection.Db, expected);
        found.Should().NotBeNull();
        found!.Value.Id.Should().Be(added.AsLibraryFile().Id);
        found.Value.TryGetAsLibraryArchive(out _).Should().BeTrue();
    }

    [Fact]
    public async Task FindsAPlainFileByItsXxHash64()
    {
        await using var folder = temporaryFileManager.CreateFolder();
        var path = folder.Path.Combine("plugin.esp");
        await File.WriteAllBytesAsync(path.ToString(), Enumerable.Range(0, 2000).Select(i => (byte)(i * 13)).ToArray(), TestContext.Current.CancellationToken);
        var expected = await Hash64(path);

        var added = await libraryService.AddLocalFile(path, UnassignedAcquisition.Instance);

        var found = locator.FindByXxHash64(connection.Db, expected);
        found.Should().NotBeNull();
        found!.Value.Id.Should().Be(added.AsLibraryFile().Id);
        found.Value.TryGetAsLibraryArchive(out _).Should().BeFalse();
    }

    [Fact]
    public void AnUnknownHashFindsNothing()
    {
        locator.FindByXxHash64(connection.Db, Xx64.From(0xDEADBEEF12345678)).Should().BeNull();
    }

    [Fact]
    public async Task LibraryArchiveFileEntry_IsMember_MatchesTheOldLocatorCheck()
    {
        await using var folder = temporaryFileManager.CreateFolder();
        var path = folder.Path.Combine("holder.zip");
        using (var zip = new ZipArchive(File.Create(path.ToString()), ZipArchiveMode.Create))
        {
            using var entry = zip.CreateEntry("inside.txt").Open();
            entry.Write("a member of the archive"u8);
        }

        var archive = (await libraryService.AddLocalFile(path, UnassignedAcquisition.Instance)).AsLibraryFile();
        var member = LibraryArchiveFileEntry.All(connection.Db).Single(entry => entry.ParentId.Value == archive.Id);

        LibraryArchiveFileEntry.IsMember(connection.Db, archive.Id).Should().BeFalse("the archive is held by the library itself");
        LibraryArchiveFileEntry.IsMember(connection.Db, member.Id).Should().BeTrue();
        LibraryArchiveLocator.IsArchiveMember(connection.Db, archive.Id).Should().Be(LibraryArchiveFileEntry.IsMember(connection.Db, archive.Id));
        LibraryArchiveLocator.IsArchiveMember(connection.Db, member.Id).Should().Be(LibraryArchiveFileEntry.IsMember(connection.Db, member.Id));
    }

    private static async Task<FileIdentity> Hash64(AbsolutePath path)
    {
        await using var stream = path.Read();
        return FileIdentity.XxHash64(await Xx64Hasher.HashAsync(stream));
    }
}
