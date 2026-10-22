using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using FluentAssertions;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Backend.FileExtractor;
using NexusMods.Backend.FileExtractor.Extractors;
using NexusMods.Paths;
using NexusMods.Sdk.IO;
using NexusMods.Sdk;

namespace NexusMods.Backend.Tests.FileExtractor;

/// <summary>
/// An archive is never refused over an entry path: names are resolved against the archive root, so a
/// ".." that would climb out of it is discarded and the file lands where a well-formed archive would put
/// it. Nothing is written outside the destination. Every case runs through both extractors that handle
/// zips, plus the 7z path for tarballs.
/// </summary>
public class ArchiveTraversalTests : AFileExtractorTest
{
    public enum Extractor { ManagedZip, SevenZip }

    private sealed record Entry(string Name, string Content = "payload");

    private sealed record Outcome(Dictionary<string, string> Files, string[] Folders, string[] Outside, List<string> Warnings);

    private async Task<Outcome> ExtractZip(Extractor extractor, params Entry[] entries)
    {
        await using var container = TemporaryFileManager.CreateFolder();
        var archive = container.Path.Combine("Traversal Mod-1-0.zip");
        await using (var stream = archive.Create())
        await using (var output = new ZipOutputStream(stream))
        {
            foreach (var entry in entries)
            {
                await output.PutNextEntryAsync(new ZipEntry(entry.Name), TestContext.Current.CancellationToken);
                if (!entry.Name.EndsWith('/')) await output.WriteAsync(Encoding.UTF8.GetBytes(entry.Content), TestContext.Current.CancellationToken);
                await output.CloseEntryAsync(TestContext.Current.CancellationToken);
            }
        }

        return await Extract(container.Path, archive, extractor);
    }

    private async Task<Outcome> ExtractTar(Action<TarWriter> write)
    {
        await using var container = TemporaryFileManager.CreateFolder();
        var archive = container.Path.Combine("Traversal Mod-1-0.tar.gz");
        await using (var stream = archive.Create())
        await using (var gzip = new GZipStream(stream, CompressionLevel.Fastest))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Gnu))
        {
            write(writer);
        }

        return await Extract(container.Path, archive, Extractor.SevenZip);
    }

    private async Task<Outcome> Extract(AbsolutePath container, AbsolutePath archive, Extractor which, AbsolutePath? destination = null)
    {
        destination ??= container.Combine("a/b/extracted");
        destination.Value.CreateDirectory();

        var warnings = new List<string>();
        IExtractor extractor = which switch
        {
            Extractor.ManagedZip => new ManagedZipExtractor(new ListLogger<ManagedZipExtractor>(warnings)),
            _ => new SevenZipExtractor(
                new ListLogger<SevenZipExtractor>(warnings),
                TemporaryFileManager,
                FileSystem,
                ServiceProvider.GetRequiredService<IProcessRunner>(),
                ServiceProvider.GetRequiredService<IOSInformation>()),
        };

        await extractor.ExtractAllAsync(new NativeFileStreamFactory(archive), destination.Value, TestContext.Current.CancellationToken);
        return Describe(container, archive, destination.Value, warnings);
    }

    private static Outcome Describe(AbsolutePath container, AbsolutePath archive, AbsolutePath destination, List<string> warnings)
    {
        var files = destination.EnumerateFiles()
            .ToDictionary(f => f.RelativeTo(destination).ToString(), f => File.ReadAllText(f.ToString()));
        var folders = Directory.EnumerateDirectories(destination.ToString(), "*", SearchOption.AllDirectories)
            .Select(d => Path.GetRelativePath(destination.ToString(), d))
            .Order()
            .ToArray();
        var outside = container.EnumerateFiles()
            .Where(f => !f.InFolder(destination) && f != archive)
            .Select(f => f.RelativeTo(container).ToString())
            .ToArray();
        return new Outcome(files, folders, outside, warnings);
    }

    public static TheoryData<Extractor, string, string> RewrittenNames()
    {
        var data = new TheoryData<Extractor, string, string>();
        foreach (var extractor in Enum.GetValues<Extractor>())
        {
            data.Add(extractor, "../escaped.txt", "escaped.txt");
            data.Add(extractor, "../../escaped.txt", "escaped.txt");
            data.Add(extractor, "sub/../../escaped.txt", "escaped.txt");
            data.Add(extractor, "sub/../escaped.txt", "escaped.txt");
            data.Add(extractor, "a/b/../../../escaped.txt", "escaped.txt");
            data.Add(extractor, "a/b/../c/escaped.txt", "a/c/escaped.txt");
            data.Add(extractor, "./escaped.txt", "escaped.txt");
            data.Add(extractor, "a/./escaped.txt", "a/escaped.txt");
            data.Add(extractor, "/escaped.txt", "escaped.txt");
            data.Add(extractor, "C:\\escaped.txt", "escaped.txt");
            data.Add(extractor, "back\\slash\\escaped.txt", "back/slash/escaped.txt");
            data.Add(extractor, "..\\..\\escaped.txt", "escaped.txt");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RewrittenNames))]
    public async Task ARewrittenEntryLandsWhereAWellFormedArchiveWouldPutIt(Extractor extractor, string entryName, string expected)
    {
        var outcome = await ExtractZip(extractor, new Entry(entryName));

        outcome.Files.Should().Equal(new Dictionary<string, string> { [expected] = "payload" });
        outcome.Outside.Should().BeEmpty();
    }

    [Theory]
    [InlineData(Extractor.ManagedZip)]
    [InlineData(Extractor.SevenZip)]
    public async Task RewrittenEntriesAreReportedInOneWarningNamingTheArchive(Extractor extractor)
    {
        var outcome = await ExtractZip(extractor, new Entry("fine/x.txt"), new Entry("../a.txt"), new Entry("sub/../../b.txt"));

        outcome.Files.Keys.Should().BeEquivalentTo("fine/x.txt", "a.txt", "b.txt");
        var warning = outcome.Warnings.Should().ContainSingle(w => w.Contains("Traversal Mod-1-0.zip")).Subject;
        warning.Should().Contain("`../a.txt` -> `a.txt`").And.Contain("`sub/../../b.txt` -> `b.txt`");
        warning.Should().NotContain("fine/x.txt");
    }

    [Theory]
    [InlineData(Extractor.ManagedZip)]
    [InlineData(Extractor.SevenZip)]
    public async Task AnArchiveWithOnlyWellFormedNamesRaisesNoWarning(Extractor extractor)
    {
        var outcome = await ExtractZip(extractor, new Entry("a/x.txt"), new Entry("a/b/"), new Entry("y.txt"));

        outcome.Files.Keys.Should().BeEquivalentTo("a/x.txt", "y.txt");
        outcome.Folders.Should().Contain("a/b");
        outcome.Warnings.Should().BeEmpty();
    }

    [Theory]
    [InlineData(Extractor.ManagedZip, "..")]
    [InlineData(Extractor.ManagedZip, "../")]
    [InlineData(Extractor.ManagedZip, "C:\\")]
    [InlineData(Extractor.SevenZip, "../")]
    [InlineData(Extractor.SevenZip, "C:\\")]
    public async Task AnEntryThatResolvesToNothingIsSkippedWithAWarning(Extractor extractor, string entryName)
    {
        var outcome = await ExtractZip(extractor, new Entry(entryName), new Entry("kept.txt"));

        outcome.Files.Keys.Should().BeEquivalentTo("kept.txt");
        outcome.Outside.Should().BeEmpty();
        outcome.Warnings.Should().ContainSingle(w => w.Contains("Traversal Mod-1-0.zip"));
    }

    [Theory]
    [InlineData(Extractor.ManagedZip)]
    [InlineData(Extractor.SevenZip)]
    public async Task ARewrittenFolderEntryCreatesTheCleanedFolder(Extractor extractor)
    {
        var outcome = await ExtractZip(extractor, new Entry("../empty/inner/"), new Entry("f\\g/"));

        outcome.Folders.Should().BeEquivalentTo("empty", "empty/inner", "f", "f/g");
        outcome.Outside.Should().BeEmpty();
    }

    [Theory]
    [InlineData(Extractor.ManagedZip)]
    [InlineData(Extractor.SevenZip)]
    public async Task PlainDuplicateEntriesKeepTheLastOne(Extractor extractor)
    {
        var outcome = await ExtractZip(extractor, new Entry("x.txt", "first"), new Entry("x.txt", "second"));

        outcome.Files.Should().Equal(new Dictionary<string, string> { ["x.txt"] = "second" });
    }

    [Theory]
    [InlineData(Extractor.ManagedZip)]
    [InlineData(Extractor.SevenZip)]
    public async Task EntriesThatCleanToTheSamePathKeepTheLastOneWithAWarning(Extractor extractor)
    {
        var outcome = await ExtractZip(extractor, new Entry("x.txt", "first"), new Entry("sub/../x.txt", "second"));

        outcome.Files.Should().Equal(new Dictionary<string, string> { ["x.txt"] = "second" });
        outcome.Warnings.Should().ContainSingle(w => w.Contains("x.txt") && w.Contains("later entry"));
    }

    [Theory]
    [InlineData(Extractor.ManagedZip)]
    [InlineData(Extractor.SevenZip)]
    public async Task ALaterPlainEntryBeatsAnEarlierRewrittenOne(Extractor extractor)
    {
        var outcome = await ExtractZip(extractor, new Entry("../x.txt", "first"), new Entry("x.txt", "second"));

        outcome.Files.Should().Equal(new Dictionary<string, string> { ["x.txt"] = "second" });
        outcome.Warnings.Should().ContainSingle(w => w.Contains("later entry"));
    }

    [Theory]
    [InlineData(Extractor.ManagedZip)]
    [InlineData(Extractor.SevenZip)]
    public async Task UnicodeNamesSurviveTheRewrite(Extractor extractor)
    {
        var outcome = await ExtractZip(extractor, new Entry("../mods\\naïve\\ファイル.txt"));

        outcome.Files.Keys.Should().BeEquivalentTo("mods/naïve/ファイル.txt");
    }

    private static void AddFile(TarWriter writer, string name, string content = "payload")
    {
        var entry = new GnuTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) };
        writer.WriteEntry(entry);
    }

    [Fact]
    public async Task ARewrittenTarballEntryLandsWhereAWellFormedArchiveWouldPutIt()
    {
        var outcome = await ExtractTar(writer =>
        {
            AddFile(writer, "../one.txt");
            AddFile(writer, "sub/../../two.txt");
            AddFile(writer, "a/b/../../../three.txt");
            AddFile(writer, "/four.txt");
            AddFile(writer, "back\\slash\\five.txt");
            AddFile(writer, "kept/six.txt");
        });

        outcome.Files.Keys.Should().BeEquivalentTo("one.txt", "two.txt", "three.txt", "four.txt", "back/slash/five.txt", "kept/six.txt");
        outcome.Outside.Should().BeEmpty();
        outcome.Warnings.Should().ContainSingle(w => w.Contains("Traversal Mod-1-0.tar.gz"));
    }

    [Fact]
    public async Task ATarballOfDotPrefixedNamesExtractsInPlaceWithoutAWarning()
    {
        var outcome = await ExtractTar(writer =>
        {
            writer.WriteEntry(new GnuTarEntry(TarEntryType.Directory, "./"));
            writer.WriteEntry(new GnuTarEntry(TarEntryType.Directory, "./mod/"));
            AddFile(writer, "./mod/a.txt");
            AddFile(writer, "./mod//b.txt");
        });

        outcome.Files.Keys.Should().BeEquivalentTo("mod/a.txt", "mod/b.txt");
        outcome.Warnings.Should().BeEmpty();
    }

    // 7z refuses a link whose target climbs with "..", and words the refusal differently ("Dangerous
    // symbolic link path") when the target climbs back down inside the archive. Either way the link is
    // dropped and the rest of the archive extracts.
    [Fact]
    public async Task ANestedLinkThatClimbsIsDroppedAndTheRestExtracts()
    {
        var outcome = await ExtractTar(writer =>
        {
            writer.WriteEntry(new GnuTarEntry(TarEntryType.Directory, "x/"));
            writer.WriteEntry(new GnuTarEntry(TarEntryType.Directory, "x/y/"));
            AddFile(writer, "sub/ok.txt");
            writer.WriteEntry(new GnuTarEntry(TarEntryType.SymbolicLink, "x/y/up") { LinkName = "../../sub" });
        });

        outcome.Files.Keys.Should().BeEquivalentTo("sub/ok.txt");
        outcome.Outside.Should().BeEmpty();
        outcome.Warnings.Should().Contain(w => w.Contains("x/y/up"));
    }

    // 7z only stores a ".." name when asked to with -spf, and on its own would extract it to `a/x.txt`.
    [Fact]
    public async Task ARewrittenEntryOfA7zArchiveLandsWhereAWellFormedArchiveWouldPutIt()
    {
        await using var container = TemporaryFileManager.CreateFolder();
        var source = container.Path.Combine("source");
        source.Combine("a/b").CreateDirectory();
        await source.Combine("x.txt").WriteAllTextAsync("payload", TestContext.Current.CancellationToken);
        await source.Combine("a/b/kept.txt").WriteAllTextAsync("payload", TestContext.Current.CancellationToken);

        var archive = container.Path.Combine("Traversal Mod-1-0.7z");
        var sevenZip = FileSystem.GetKnownPath(KnownPath.EntryDirectory).Combine("runtimes/linux-x64/native/7zz").ToString();
        var startInfo = new ProcessStartInfo(sevenZip) { WorkingDirectory = source.ToString(), RedirectStandardOutput = true };
        foreach (var argument in new[] { "a", "-spf", archive.ToString(), "a/../x.txt", "a/b/kept.txt" }) startInfo.ArgumentList.Add(argument);
        using (var process = System.Diagnostics.Process.Start(startInfo)!)
        {
            await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            process.ExitCode.Should().Be(0);
        }

        var outcome = await Extract(container.Path, archive, Extractor.SevenZip);

        outcome.Files.Keys.Should().BeEquivalentTo("x.txt", "a/b/kept.txt");
        outcome.Warnings.Should().ContainSingle(w => w.Contains("`a/../x.txt` -> `x.txt`"));
    }

    // An archive can create a link and then write through it. 7z drops the link, and the entry that goes
    // through it has nowhere to land: the extraction fails, and nothing reaches the link target.
    [Fact]
    public async Task AFileWrittenThroughAnArchiveSymlinkNeverReachesTheLinkTarget()
    {
        await using var outside = TemporaryFileManager.CreateFolder();
        var extraction = ExtractTar(writer =>
        {
            writer.WriteEntry(new GnuTarEntry(TarEntryType.SymbolicLink, "link") { LinkName = outside.Path.ToString() });
            AddFile(writer, "link/escaped.txt");
        });

        await extraction.Awaiting(e => e).Should().ThrowAsync<FileExtractionException>();
        outside.Path.EnumerateFiles().Should().BeEmpty();
    }

    // A relative link that climbs out of the destination is dropped and 7z writes a real folder in its place.
    [Fact]
    public async Task AFileWrittenThroughARelativeArchiveSymlinkStaysInsideTheDestination()
    {
        var outcome = await ExtractTar(writer =>
        {
            writer.WriteEntry(new GnuTarEntry(TarEntryType.SymbolicLink, "rel") { LinkName = "../../../" });
            AddFile(writer, "rel/escaped.txt");
        });

        outcome.Files.Keys.Should().BeEquivalentTo("rel/escaped.txt");
        outcome.Outside.Should().BeEmpty();
    }

    // 7z resolves a hard link target inside the destination, so a target path outside it finds nothing to link.
    [Fact]
    public async Task AHardLinkEntryNeverExposesAFileOutsideTheDestination()
    {
        await using var outside = TemporaryFileManager.CreateFolder();
        var secret = outside.Path.Combine("secret.txt");
        await secret.WriteAllTextAsync("private", TestContext.Current.CancellationToken);

        var extraction = ExtractTar(writer =>
        {
            writer.WriteEntry(new GnuTarEntry(TarEntryType.HardLink, "stolen.txt") { LinkName = secret.ToString() });
            AddFile(writer, "ok.txt");
        });

        await extraction.Awaiting(e => e).Should().ThrowAsync<FileExtractionException>();
        File.ReadAllText(secret.ToString()).Should().Be("private");
    }

    [Fact]
    public async Task AFifoEntryDoesNotBlockExtraction()
    {
        var extraction = ExtractTar(writer =>
        {
            writer.WriteEntry(new GnuTarEntry(TarEntryType.Fifo, "pipe"));
            AddFile(writer, "ok.txt");
        });

        var finished = await Task.WhenAny(extraction, Task.Delay(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        finished.Should().BeSameAs(extraction, "7z must not open a FIFO for writing");
        (await extraction).Files.Keys.Should().Contain("ok.txt");
    }

    // The production destination is a fresh folder from TemporaryFileManager (AddLibraryFileJob), so no
    // link can exist before extraction starts. A link planted in a folder that is not fresh is followed by
    // the zip extractor like any file write; this records that the guarantee is the folder being new.
    [Fact]
    public async Task ADestinationFromTheTemporaryFileManagerIsEmpty()
    {
        await using var folder = TemporaryFileManager.CreateFolder();
        Directory.GetFileSystemEntries(folder.Path.ToString()).Should().BeEmpty();
    }

    private sealed class ListLogger<T>(List<string> warnings) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) warnings.Add(formatter(state, exception));
        }
    }
}
