using System.IO.Compression;
using FluentAssertions;
using NexusMods.Abstractions.Library;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Library;
using NexusMods.Wabbajack.Library;

namespace NexusMods.Wabbajack.Tests;

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

        var added = await libraryService.AddLocalFile(path);

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

        var added = await libraryService.AddLocalFile(path);

        var found = locator.FindByXxHash64(connection.Db, expected);
        found.Should().NotBeNull();
        found!.Value.Id.Should().Be(added.AsLibraryFile().Id);
        found.Value.TryGetAsLibraryArchive(out _).Should().BeFalse();
    }

    [Fact]
    public void AnUnknownHashFindsNothing()
    {
        locator.FindByXxHash64(connection.Db, Hash.From(0xDEADBEEF12345678)).Should().BeNull();
    }

    private static async Task<Hash> Hash64(AbsolutePath path)
    {
        await using var stream = path.Read();
        return await Xx64Hasher.HashAsync(stream);
    }
}
