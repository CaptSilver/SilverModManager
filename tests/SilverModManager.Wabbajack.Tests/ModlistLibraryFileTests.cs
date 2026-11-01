using System.IO.Compression;
using FluentAssertions;
using SilverModManager.Abstractions.Library;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Library;
using SilverModManager.Wabbajack.Models;
using SilverModManager.Wabbajack.Tests.Fixtures;

namespace SilverModManager.Wabbajack.Tests;

public class ModlistLibraryFileTests(ILibraryService libraryService, IConnection connection, TemporaryFileManager temporaryFileManager)
{
    [Fact]
    public async Task AddingAModlistRecordsItsFields()
    {
        await using var folder = temporaryFileManager.CreateFolder();
        var list = WabbajackFixture.EmptyList("Shiny list", new InlineFile(RelativePath.FromUnsanitizedInput("a.txt"), Xx64.From(1), 700, "blob-1")) with
        {
            Author = "Someone",
            Version = "2.3.0.1",
            GameType = "Fallout4",
            IsNsfw = true,
            Archives =
            [
                new ArchiveEntry(Xx64.From(10), 1000, "one.7z", "", new HttpState(new Uri("https://example.invalid/one.7z"), [])),
                new ArchiveEntry(Xx64.From(11), 2500, "two.7z", "", new HttpState(new Uri("https://example.invalid/two.7z"), [])),
            ],
        };
        var path = WabbajackFixture.Write(folder.Path, list, new Dictionary<string, byte[]> { ["blob-1"] = [1, 2, 3] });

        var added = await libraryService.AddLocalFile(path, UnassignedAcquisition.Instance);

        added.AsLibraryFile().TryGetAsWabbajackModlistLibraryFile(out var model).Should().BeTrue();
        model.Name.Should().Be("Shiny list");
        model.Author.Should().Be("Someone");
        model.Version.Should().Be("2.3.0.1");
        model.GameType.Should().Be("Fallout4");
        model.ArchiveCount.Should().Be(2);
        model.ArchiveSize.Value.Should().Be(3500);
        model.InstallSize.Value.Should().Be(700);
        model.IsNsfw.Should().BeTrue();
    }

    [Fact]
    public async Task AddingTheRealModlistRecordsItsCounts()
    {
        var added = await libraryService.AddLocalFile(FileSystem.Shared.FromUnsanitizedFullPath(WabbajackFixture.RealModlistPath), UnassignedAcquisition.Instance);

        added.AsLibraryFile().TryGetAsWabbajackModlistLibraryFile(out var model).Should().BeTrue();
        model.Name.Should().Be("MO2AndSKSETest");
        model.ArchiveCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task AZipWithoutAModlistEntryIsNotAModlist()
    {
        await using var folder = temporaryFileManager.CreateFolder();
        var path = folder.Path.Combine("not-a-list.wabbajack");
        using (var zip = new ZipArchive(File.Create(path.ToString()), ZipArchiveMode.Create))
        {
            using var entry = zip.CreateEntry("something-else").Open();
            entry.Write("x"u8);
        }

        var added = await libraryService.AddLocalFile(path, UnassignedAcquisition.Instance);

        added.AsLibraryFile().TryGetAsWabbajackModlistLibraryFile(out _).Should().BeFalse();
    }

    [Fact]
    public async Task AModlistUnderAnotherExtensionIsNotRecognised()
    {
        await using var folder = temporaryFileManager.CreateFolder();
        var path = WabbajackFixture.Write(folder.Path, WabbajackFixture.EmptyList(), fileName: "list.zip");

        var added = await libraryService.AddLocalFile(path, UnassignedAcquisition.Instance);

        added.AsLibraryFile().TryGetAsWabbajackModlistLibraryFile(out _).Should().BeFalse();
    }

    [Fact]
    public async Task AModlistThisVersionCannotReadStaysAPlainLibraryFile()
    {
        await using var folder = temporaryFileManager.CreateFolder();
        var path = folder.Path.Combine("broken.wabbajack");
        using (var zip = new ZipArchive(File.Create(path.ToString()), ZipArchiveMode.Create))
        {
            using var entry = zip.CreateEntry("modlist").Open();
            entry.Write("{\"Name\": \"x\", \"Directives\": [{\"$type\": \"NoSuchDirective, Wabbajack.DTOs\"}]}"u8);
        }

        var added = await libraryService.AddLocalFile(path, UnassignedAcquisition.Instance);

        added.AsLibraryFile().IsValid().Should().BeTrue();
        added.AsLibraryFile().TryGetAsWabbajackModlistLibraryFile(out _).Should().BeFalse();
    }

    [Fact]
    public async Task AddingAModlistRecordsOneArchiveRowPerEntryAndNoneIsFetched()
    {
        await using var folder = temporaryFileManager.CreateFolder();
        var list = WabbajackFixture.EmptyList("Rows") with
        {
            Archives =
            [
                new ArchiveEntry(Xx64.From(10), 1000, "one.7z", "", new HttpState(new Uri("https://example.invalid/one.7z"), [])),
                new ArchiveEntry(Xx64.From(11), 2500, "two.7z", "", new HttpState(new Uri("https://example.invalid/two.7z"), [])),
            ],
        };
        var path = WabbajackFixture.Write(folder.Path, list);

        var added = await libraryService.AddLocalFile(path, UnassignedAcquisition.Instance);

        var rows = WabbajackModlistArchive.FindByModlist(connection.Db, added.AsLibraryFile().Id).ToArray();
        rows.Select(r => (r.Name, r.Size.Value)).Should().BeEquivalentTo([("one.7z", 1000UL), ("two.7z", 2500UL)]);
        rows.Select(r => r.Identity).Should().BeEquivalentTo([Xx64.From(10), Xx64.From(11)]);
        rows.Should().OnlyContain(r => r.FetchedFiles.Count == 0);
    }

    [Fact]
    public async Task TheRealModlistGetsOneArchiveRowPerEntry()
    {
        var added = await libraryService.AddLocalFile(FileSystem.Shared.FromUnsanitizedFullPath(WabbajackFixture.RealModlistPath), UnassignedAcquisition.Instance);

        added.AsLibraryFile().TryGetAsWabbajackModlistLibraryFile(out var model).Should().BeTrue();
        WabbajackModlistArchive.FindByModlist(connection.Db, model.Id).Count().Should().Be(model.ArchiveCount);
    }
}
