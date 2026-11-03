using System.IO.Hashing;
using System.Text;
using FluentAssertions;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Backend.FileExtractor;
using SilverModManager.Backend.FileExtractor.Extractors;
using SilverModManager.Sdk;
using SilverModManager.Sdk.IO;

namespace SilverModManager.Backend.Tests.FileExtractor;

/// <summary>
/// How zip entry names come out of the extractor: code page 437 or UTF-8 for names the zip does not flag,
/// the Info-ZIP unicode-path field, unusable characters, trailing spaces and dots. Every case goes through
/// <see cref="IFileExtractor"/>, so whichever extractor claims zips is what is checked.
/// </summary>
public class ZipNameTests : AFileExtractorTest
{

    [Theory]
    [InlineData("unicode-names.zip", "こんにちわ.txt")]
    [InlineData("unicode-extra-data.zip", "こんにちわ.txt")]
    public async Task Test(string archiveName, string expectedFileName)
    {
        // NOTE(erri120): The archive "unicode-extra-data" was created using the CreateZipFile() method
        // and then manually binary patched to remove the unicode flag.

        var archivePath = FileSystem.GetKnownPath(KnownPath.CurrentDirectory).Combine("Resources").Combine(archiveName);
        archivePath.FileExists.Should().BeTrue();

        await using var destinationPath = TemporaryFileManager.CreateFolder();
        await FileExtractor.ExtractAllAsync(new NativeFileStreamFactory(archivePath), destinationPath, TestContext.Current.CancellationToken);

        var files = destinationPath.Path.EnumerateFiles().ToArray();
        files.Where(f => f.FileName == expectedFileName).Should().ContainSingle();
    }

    // A zip entry can be marked as a Unix symlink, its data being the link target. No link is created and
    // the target text is never written as a file: a link to somewhere outside the destination is dropped.
    [Fact]
    public async Task ASymlinkEntryPointingOutsideTheDestinationIsDropped()
    {
        await using var outside = TemporaryFileManager.CreateFolder();
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
        await FileExtractor.ExtractAllAsync(WriteZip(archive, "link.zip"), destination, TestContext.Current.CancellationToken);

        var extracted = destination.Combine("link").ToString();
        new FileInfo(extracted).LinkTarget.Should().BeNull();
        File.Exists(extracted).Should().BeFalse();
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

    // Without a logger the extraction goes through the service-provider extractor; the warning tests hand in a
    // capturing logger, which needs the extractor built by hand.
    private async Task<string[]> ExtractNames(MemoryStream zip, ILogger<SevenZipExtractor>? logger = null)
    {
        await using var destination = TemporaryFileManager.CreateFolder();
        var source = WriteZip(zip, "legacy.zip");
        if (logger is null)
        {
            await FileExtractor.ExtractAllAsync(source, destination, TestContext.Current.CancellationToken);
        }
        else
        {
            var extractor = new SevenZipExtractor(logger, TemporaryFileManager, FileSystem,
                ServiceProvider.GetRequiredService<IProcessRunner>(), ServiceProvider.GetRequiredService<IOSInformation>());
            await extractor.ExtractAllAsync(source, destination, TestContext.Current.CancellationToken);
        }

        return destination.Path.EnumerateFiles().Select(f => f.RelativeTo(destination.Path).ToString()).Order().ToArray();
    }

    // The extractor opens the source more than once (signature check, then the extraction), which a
    // MemoryStreamFactory does not allow, so the archive goes through a file.
    private NativeFileStreamFactory WriteZip(MemoryStream zip, string name)
    {
        var folder = TemporaryFileManager.CreateFolder();
        var path = folder.Path.Combine(name);
        File.WriteAllBytes(path.ToString(), zip.ToArray());
        return new NativeFileStreamFactory(path);
    }

    private static MemoryStream ZipOf(params (string Name, string Content)[] entries)
    {
        var zip = new MemoryStream();
        using (var output = new ZipOutputStream(zip))
        {
            output.IsStreamOwner = false;
            foreach (var (name, content) in entries)
            {
                output.PutNextEntry(new ZipEntry(name));
                if (!name.EndsWith('/')) output.Write(Encoding.UTF8.GetBytes(content));
                output.CloseEntry();
            }
        }

        zip.Position = 0;
        return zip;
    }

    // Bytes 0x8B and 0x94 are 'ï' and 'ö' in code page 437, the encoding a zip name has unless it is flagged as UTF-8.
    // They are not valid UTF-8, which is what the extraction used to decode them with.
    [Fact]
    public async Task UnflaggedNamesAreDecodedAsCodePage437()
    {
        var zip = ZipWithRawName("filename_with_", [0x8B, (byte)'o', 0x94], ".txt");
        (await ExtractNames(zip)).Should().Equal("filename_with_\u00EFo\u00F6.txt");
    }

    // The same decoding applies to a folder in the path, which has to be renamed as well as the file.
    [Fact]
    public async Task UnflaggedFolderNamesAreDecodedAsCodePage437()
    {
        var zip = ZipWithRawName("dir_", [0x8B, 0x94], "/inner.txt");
        (await ExtractNames(zip)).Should().Equal("dir_\u00EF\u00F6/inner.txt");
    }

    // A name that is not UTF-8 and ends in whitespace cannot be given to 7z by name, so the entry is
    // extracted with the rest and fixed afterwards.
    [Fact]
    public async Task AnUnflaggedNameEndingInASpaceIsDecodedAndTrimmed()
    {
        var zip = ZipWithRawName("end_", [0x8B], " ");
        (await ExtractNames(zip)).Should().Equal("end_\u00EF");
    }

    // macOS Archive Utility and several Linux tools write UTF-8 names without bit 11.
    [Fact]
    public async Task UnflaggedNamesThatAreValidUtfEightKeepTheirUtfEightSpelling()
    {
        var zip = ZipWithRawName("pl", [0xC3, 0xB6, 0xD0, 0x9F], ".esp");
        (await ExtractNames(zip)).Should().Equal("pl\u00F6\u041F.esp");
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
        // Info-ZIP: the CRC-32 of the whole stored name, which is how a reader knows the field belongs to it.
        Crc32.Hash([.. "pre_"u8, .. legacy, .. ".txt"u8]).CopyTo(extra, 5);
        utf8.CopyTo(extra, 9);

        var zip = ZipWithRawName("pre_", legacy, ".txt", extra);
        (await ExtractNames(zip)).Should().Equal("pre_\u65E5\u672C.txt");
    }

    [Fact]
    public async Task ControlCharactersInANameAreReplacedAndReportedInsteadOfThrowing()
    {
        var logger = new CapturingLogger<SevenZipExtractor>();
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

    // Windows and macOS cannot keep a trailing space or dot on a name, and the path layer trims them, so a
    // folder or file with one comes out trimmed.
    [Fact]
    public async Task AFolderNameWithATrailingSpaceIsTrimmedEvenWithoutAFolderEntry()
    {
        var zip = ZipOf(("trailing space /file.txt", "x"));
        (await ExtractNames(zip)).Should().Equal("trailing space/file.txt");
    }

    [Fact]
    public async Task AFolderNameEndingInADotAndASpaceIsTrimmed()
    {
        var zip = ZipOf(("name. /f.txt", "x"));
        (await ExtractNames(zip)).Should().Equal("name/f.txt");
    }

    [Fact]
    public async Task AFileNameWithATrailingSpaceIsTrimmed()
    {
        var zip = ZipOf(("end.txt ", "x"));
        (await ExtractNames(zip)).Should().Equal("end.txt");
    }

    [Fact]
    public async Task TwoNamesThatTrimToOneKeepTheLaterEntry()
    {
        var zip = ZipOf(("a ", "first"), ("a", "second"));

        await using var destination = TemporaryFileManager.CreateFolder();
        await FileExtractor.ExtractAllAsync(WriteZip(zip, "legacy.zip"), destination, TestContext.Current.CancellationToken);

        destination.Path.EnumerateFiles().Select(f => f.RelativeTo(destination.Path).ToString()).Should().Equal("a");
        File.ReadAllText(destination.Path.Combine("a").ToString()).Should().Be("second");
    }

    [Fact]
    public async Task TwoFoldersThatTrimToOneAreMerged()
    {
        var zip = ZipOf(("a /x.txt", "x"), ("a/y.txt", "y"));
        (await ExtractNames(zip)).Should().Equal("a/x.txt", "a/y.txt");
    }

    // 7z stops with a CRC error and leaves the wrong bytes on disk; the extractor layer clears a fresh
    // destination, so a corrupt archive leaves nothing a caller could import.
    [Fact]
    public async Task ACorruptDeflateEntryFailsTheExtractionAndLeavesNothing()
    {
        var content = string.Concat(Enumerable.Range(0, 4000).Select(i => $"line {i % 37} of a repetitive mod file\n"));
        var zip = ZipOf(("a/one.txt", content), ("a/two.txt", content)).ToArray();

        // Past the first local header (30 bytes plus the 9 byte name) and into its deflate stream.
        zip[30 + "a/one.txt".Length + 20] ^= 0xFF;

        await using var destination = TemporaryFileManager.CreateFolder();
        var act = async () => await FileExtractor.ExtractAllAsync(WriteZip(new MemoryStream(zip), "corrupt.zip"), destination, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<FileExtractionException>()).Which.Message.Should().MatchRegex("CRC Failed|Data Error");
        Directory.GetFileSystemEntries(destination.Path.ToString()).Should().BeEmpty();
    }

    // Data that still inflates (or is stored) but no longer matches its CRC: the old managed extractor wrote
    // the wrong bytes without complaint.
    [Fact]
    public async Task AnEntryWhoseBytesNoLongerMatchItsCrcFailsTheExtractionAndLeavesNothing()
    {
        var zip = new MemoryStream();
        using (var output = new ZipOutputStream(zip))
        {
            output.IsStreamOwner = false;
            output.PutNextEntry(new ZipEntry("a/one.txt") { CompressionMethod = CompressionMethod.Stored });
            output.Write("some stored content"u8);
            output.CloseEntry();
        }

        var bytes = zip.ToArray();
        bytes[bytes.AsSpan().IndexOf("some stored content"u8) + 3] ^= 0x01;

        await using var destination = TemporaryFileManager.CreateFolder();
        var act = async () => await FileExtractor.ExtractAllAsync(WriteZip(new MemoryStream(bytes), "corrupt.zip"), destination, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<FileExtractionException>()).Which.Message.Should().Contain("CRC Failed");
        Directory.GetFileSystemEntries(destination.Path.ToString()).Should().BeEmpty();
    }
}
