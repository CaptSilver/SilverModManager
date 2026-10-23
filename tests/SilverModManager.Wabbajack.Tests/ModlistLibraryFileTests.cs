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
        var list = WabbajackFixture.EmptyList("Shiny list", new InlineFile(RelativePath.FromUnsanitizedInput("a.txt"), Hash.From(1), 700, "blob-1")) with
        {
            Author = "Someone",
            Version = "2.3.0.1",
            GameType = "Fallout4",
            IsNsfw = true,
            Archives =
            [
                new ArchiveEntry(Hash.From(10), 1000, "one.7z", "", new HttpState(new Uri("https://example.invalid/one.7z"), [])),
                new ArchiveEntry(Hash.From(11), 2500, "two.7z", "", new HttpState(new Uri("https://example.invalid/two.7z"), [])),
            ],
        };
        var path = WabbajackFixture.Write(folder.Path, list, new Dictionary<string, byte[]> { ["blob-1"] = [1, 2, 3] });

        var added = await libraryService.AddLocalFile(path);

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
        var added = await libraryService.AddLocalFile(FileSystem.Shared.FromUnsanitizedFullPath(WabbajackFixture.RealModlistPath));

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

        var added = await libraryService.AddLocalFile(path);

        added.AsLibraryFile().TryGetAsWabbajackModlistLibraryFile(out _).Should().BeFalse();
    }

    [Fact]
    public async Task AModlistUnderAnotherExtensionIsNotRecognised()
    {
        await using var folder = temporaryFileManager.CreateFolder();
        var path = WabbajackFixture.Write(folder.Path, WabbajackFixture.EmptyList(), fileName: "list.zip");

        var added = await libraryService.AddLocalFile(path);

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

        var added = await libraryService.AddLocalFile(path);

        added.AsLibraryFile().IsValid().Should().BeTrue();
        added.AsLibraryFile().TryGetAsWabbajackModlistLibraryFile(out _).Should().BeFalse();
    }
}
