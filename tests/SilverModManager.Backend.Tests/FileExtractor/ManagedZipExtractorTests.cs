using System.IO.Hashing;
using System.Text;
using FluentAssertions;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Extensions.Logging;
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
        var prefix = FileSystem.Shared.FromUnsanitizedFullPath(TestScratch.Combine($"SilverModManager.Backend.Tests-{Guid.NewGuid()}"));
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

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    // Builds a one-entry zip whose name is written as raw bytes. SharpZipLib always flags the entry as UTF-8,
    // so the entry is created under an ASCII placeholder of the same length, the bytes are patched in
    // afterwards (local header and central directory) and bit 11 is cleared in both headers.
    private static MemoryStream ZipWithRawName(string prefix, byte[] rawTail, string suffix, byte[]? unicodePathExtra = null)
    {
        var placeholderTail = new string('X', rawTail.Length);
        var entry = new ZipEntry(prefix + placeholderTail + suffix);
        if (unicodePathExtra is not null) entry.ExtraData = unicodePathExtra;

        var zip = new MemoryStream();
        using (var output = new ZipOutputStream(zip))
        {
            output.IsStreamOwner = false;
            output.PutNextEntry(entry);
            output.Write("content"u8);
            output.CloseEntry();
        }

        var bytes = zip.ToArray();
        var needle = Encoding.ASCII.GetBytes(placeholderTail);
        var start = 0;
        while (true)
        {
            var at = bytes.AsSpan(start).IndexOf(needle);
            if (at < 0) break;
            rawTail.CopyTo(bytes.AsSpan(start + at));
            start += at + needle.Length;
        }

        ClearUtf8Flag(bytes, [0x50, 0x4B, 0x03, 0x04], flagOffset: 6);
        ClearUtf8Flag(bytes, [0x50, 0x4B, 0x01, 0x02], flagOffset: 8);
        return new MemoryStream(bytes);
    }

    private static void ClearUtf8Flag(byte[] zip, byte[] headerSignature, int flagOffset)
    {
        var start = 0;
        while (true)
        {
            var at = zip.AsSpan(start).IndexOf(headerSignature);
            if (at < 0) return;
            zip[start + at + flagOffset + 1] &= 0xF7;
            start += at + headerSignature.Length;
        }
    }

    private static async Task<string[]> ExtractNames(MemoryStream zip, ILogger<ManagedZipExtractor>? logger = null)
    {
        using var temporaryFileManager = CreateTemporaryFileManager();
        await using var destination = temporaryFileManager.CreateFolder();
        var extractor = new ManagedZipExtractor(logger ?? NullLogger<ManagedZipExtractor>.Instance);
        await extractor.ExtractAllAsync(new MemoryStreamFactory("legacy.zip", zip), destination, TestContext.Current.CancellationToken);
        return destination.Path.EnumerateFiles().Select(f => f.RelativeTo(destination.Path).ToString()).ToArray();
    }

    // Bytes 0x8B and 0x94 are 'ï' and 'ö' in code page 437, the encoding a zip name has unless it is flagged as UTF-8.
    // They are not valid UTF-8, which is what the extraction used to decode them with.
    [Fact]
    public async Task UnflaggedNamesAreDecodedAsCodePage437()
    {
        var zip = ZipWithRawName("filename_with_", [0x8B, (byte)'o', 0x94], ".txt");
        (await ExtractNames(zip)).Should().Equal("filename_with_\u00EFo\u00F6.txt");
    }

    [Fact]
    public async Task WindowsOneTwoFiveTwoQuotesDecodeWithoutFailing()
    {
        // 0x93 and 0x94 are curly quotes in Windows-1252 and 'ô'/'ö' in code page 437. The spec says 437.
        var zip = ZipWithRawName("it", [0x93, (byte)'s', 0x94], ".txt");
        (await ExtractNames(zip)).Should().Equal("it\u00F4s\u00F6.txt");
    }

    [Fact]
    public async Task UtfEightFlaggedNamesAreDecodedAsUtfEight()
    {
        var zip = new MemoryStream();
        using (var output = new ZipOutputStream(zip))
        {
            output.IsStreamOwner = false;
            output.PutNextEntry(new ZipEntry("na\u00EFve_\u65E5\u672C.txt"));
            output.Write("x"u8);
            output.CloseEntry();
        }
        zip.Position = 0;

        (await ExtractNames(zip)).Should().Equal("na\u00EFve_\u65E5\u672C.txt");
    }

    [Fact]
    public async Task UnicodePathExtraFieldWinsOverTheLegacyName()
    {
        byte[] legacy = [0x8B, 0x94];
        // The field carries the whole name, not just the part that differs.
        var utf8 = Encoding.UTF8.GetBytes("pre_\u65E5\u672C.txt");
        var extra = new byte[4 + 1 + 4 + utf8.Length];
        BitConverter.GetBytes((ushort)0x7075).CopyTo(extra, 0);
        BitConverter.GetBytes((ushort)(1 + 4 + utf8.Length)).CopyTo(extra, 2);
        extra[4] = 1;
        Crc32.Hash(legacy).CopyTo(extra, 5);
        utf8.CopyTo(extra, 9);

        var zip = ZipWithRawName("pre_", legacy, ".txt", extra);
        (await ExtractNames(zip)).Should().Equal("pre_\u65E5\u672C.txt");
    }

    [Fact]
    public async Task ControlCharactersInANameAreReplacedAndReportedInsteadOfThrowing()
    {
        var logger = new CapturingLogger<ManagedZipExtractor>();
        var zip = ZipWithRawName("bad_", [0x01, 0x02], "_name.txt");

        (await ExtractNames(zip, logger)).Should().Equal("bad____name.txt");

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Message.Contains("legacy.zip") && e.Message.Contains("bad_"));
    }

    [Fact]
    public async Task ANameNoDecodingCanRepresentStillExtractsUnderADeterministicName()
    {
        // 0xEF 0xBF 0xBD is U+FFFD in a UTF-8 flagged entry: a replacement character the path layer rejects.
        var zip = new MemoryStream();
        using (var output = new ZipOutputStream(zip))
        {
            output.IsStreamOwner = false;
            output.PutNextEntry(new ZipEntry("a\uFFFDb\u00EF.txt"));
            output.Write("x"u8);
            output.CloseEntry();
        }
        zip.Position = 0;

        (await ExtractNames(zip)).Should().Equal("a_b\u00EF.txt");
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
