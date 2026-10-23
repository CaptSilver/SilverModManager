using System.IO.Hashing;
using System.Text;
using FluentAssertions;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Backend.FileExtractor.Extractors;
using NexusMods.Paths;
using SilverModManager.Sdk.IO;

namespace SilverModManager.Backend.Tests.FileExtractor;

public class ManagedZipExtractorTests
{
    // The default folder is shared with the tests that get their manager from the service provider, and
    // disposing a manager deletes its folder, so each test here gets its own.
    private static TemporaryFileManager CreateTemporaryFileManager()
    {
        var prefix = FileSystem.Shared.GetKnownPath(KnownPath.EntryDirectory).Combine($"SilverModManager.Backend.Tests-{Guid.NewGuid()}");
        return new TemporaryFileManager(FileSystem.Shared, prefix);
    }

    [Theory]
    [InlineData("unicode-names.zip", "こんにちわ.txt")]
    [InlineData("unicode-extra-data.zip", "こんにちわ.txt")]
    public async Task Test(string archiveName, string expectedFileName)
    {
        // NOTE(erri120): The archive "unicode-extra-data" was created using the CreateZipFile() method
        // and then manually binary patched to remove the unicode flag.

        var archivePath = FileSystem.Shared.GetKnownPath(KnownPath.CurrentDirectory).Combine("Resources").Combine(archiveName);
        archivePath.FileExists.Should().BeTrue();

        var extractor = new ManagedZipExtractor(NullLogger<ManagedZipExtractor>.Instance);
        using var temporaryFileManager = CreateTemporaryFileManager();

        await using var destinationPath = temporaryFileManager.CreateFolder();
        await extractor.ExtractAllAsync(new NativeFileStreamFactory(archivePath), destinationPath, TestContext.Current.CancellationToken);

        var files = destinationPath.Path.EnumerateFiles().ToArray();
        files.Where(f => f.FileName == expectedFileName).Should().ContainSingle();
    }

    // A zip entry can be marked as a Unix symlink, its data being the link target. The managed extractor
    // writes entry data to a regular file and never creates links, so the target is just file content.
    [Fact]
    public async Task SymlinkEntriesAreWrittenAsRegularFiles()
    {
        using var temporaryFileManager = CreateTemporaryFileManager();
        await using var outside = temporaryFileManager.CreateFolder();
        var secret = outside.Path.Combine("outside-secret.txt");
        await secret.WriteAllTextAsync("private", TestContext.Current.CancellationToken);

        var archive = new MemoryStream();
        await using (var output = new ZipOutputStream(archive))
        {
            output.IsStreamOwner = false;
            const int symlinkMode = 0xA000 | 0x1FF;
            var entry = new ZipEntry("link") { HostSystem = (int)HostSystemID.Unix, ExternalFileAttributes = symlinkMode << 16 };
            await output.PutNextEntryAsync(entry, TestContext.Current.CancellationToken);
            await output.WriteAsync(Encoding.UTF8.GetBytes(secret.ToString()), TestContext.Current.CancellationToken);
            await output.CloseEntryAsync(TestContext.Current.CancellationToken);
        }
        archive.Position = 0;

        var destination = outside.Path.Combine("extracted");
        destination.CreateDirectory();
        var extractor = new ManagedZipExtractor(NullLogger<ManagedZipExtractor>.Instance);
        await extractor.ExtractAllAsync(new MemoryStreamFactory("link.zip", archive), destination, TestContext.Current.CancellationToken);

        var extracted = destination.Combine("link").ToString();
        new FileInfo(extracted).LinkTarget.Should().BeNull();
        File.ReadAllText(extracted).Should().Be(secret.ToString());
        File.ReadAllText(secret.ToString()).Should().Be("private");
    }

    private static async Task CreateZipFile()
    {
        const string fileName = "こんにちわ.txt";
        var nameBytes = Encoding.UTF8.GetBytes(fileName);

        using var zipStream = new MemoryStream();
        await using (var outputStream = new ZipOutputStream(zipStream))
        {
            outputStream.IsStreamOwner = false;

            var fakeName = string.Create(length: nameBytes.Length, state: false, action: (span, _) => span.Fill('a'));
            var entry = new ZipEntry(fakeName)
            {
                ExtraData = CreateExtraData(nameBytes, fakeName),
            };

            var data = Encoding.UTF8.GetBytes(fileName);
            entry.Size = data.Length;

            await outputStream.PutNextEntryAsync(entry);
            await outputStream.WriteAsync(data);
            await outputStream.CloseEntryAsync(CancellationToken.None);
        }

        zipStream.Position = 0;

        await using (var fs = FileSystem.Shared.CreateFile(FileSystem.Shared.GetKnownPath(KnownPath.CurrentDirectory).Combine("b.zip")))
        {
            await zipStream.CopyToAsync(fs);
        }
    }

    private static byte[] CreateExtraData(byte[] nameBytes, string fakeName)
    {
        var fakeNameBytes = Encoding.UTF8.GetBytes(fakeName);
        var hash = Crc32.Hash(fakeNameBytes);

        var index = 0;
        var headerLength = sizeof(ushort) * 2;
        var dataLength = sizeof(byte) + sizeof(uint) + nameBytes.Length;
        var result = new byte[headerLength + dataLength];

        BitConverter.GetBytes((ushort)0x7075).AsSpan().CopyTo(result.AsSpan(start: index, length: sizeof(ushort)));
        index += sizeof(ushort);

        BitConverter.GetBytes((ushort)dataLength).AsSpan().CopyTo(result.AsSpan(start: index, length: sizeof(ushort)));
        index += sizeof(ushort);

        result[index++] = 1; // version

        hash.AsSpan().CopyTo(result.AsSpan(start: index));
        index += hash.Length;

        nameBytes.AsSpan().CopyTo(result.AsSpan(start: index));
        return result;
    }
}
