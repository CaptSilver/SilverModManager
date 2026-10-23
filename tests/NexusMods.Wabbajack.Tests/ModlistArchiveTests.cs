using System.Text;
using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Abstractions.Library;
using NexusMods.Paths;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Library;
using NexusMods.Wabbajack.Format;
using NexusMods.Wabbajack.Tests.Fixtures;

namespace NexusMods.Wabbajack.Tests;

public class ModlistArchiveTests(ILibraryService libraryService, IFileStore fileStore, TemporaryFileManager temporaryFileManager)
{
    private async Task<LibraryArchive.ReadOnly> Add(AbsolutePath file)
    {
        var localFile = await libraryService.AddLocalFile(file);
        localFile.AsLibraryFile().TryGetAsLibraryArchive(out var archive).Should().BeTrue();
        return archive;
    }

    [Fact]
    public async Task ReadsTheModlistAndABlobBackOutOfTheStore()
    {
        await using var folder = temporaryFileManager.CreateFolder();
        var hash = Hash.From(42);
        var list = WabbajackFixture.EmptyList(
            "From the store",
            new InlineFile(RelativePath.FromUnsanitizedInput("a.txt"), hash, 5, "blob-1"));
        var path = WabbajackFixture.Write(folder.Path, list, new Dictionary<string, byte[]> { ["blob-1"] = Encoding.UTF8.GetBytes("hello") });

        var archive = await Add(path);

        var read = await ModlistArchive.ReadModlist(fileStore, archive);
        read.Name.Should().Be("From the store");
        read.Directives.Should().ContainSingle().Which.Should().BeOfType<InlineFile>();

        await using var blob = await ModlistArchive.ReadBlob(fileStore, archive, "blob-1");
        using var reader = new StreamReader(blob);
        (await reader.ReadToEndAsync()).Should().Be("hello");
    }

    [Fact]
    public async Task ReadsTheRealModlist()
    {
        var archive = await Add(FileSystem.Shared.FromUnsanitizedFullPath(WabbajackFixture.RealModlistPath));

        var read = await ModlistArchive.ReadModlist(fileStore, archive);

        read.Name.Should().Be("MO2AndSKSETest");
        read.Directives.Should().HaveCount(1409);
    }

    [Fact]
    public async Task AMissingBlobIsReportedByName()
    {
        await using var folder = temporaryFileManager.CreateFolder();
        var path = WabbajackFixture.Write(folder.Path, WabbajackFixture.EmptyList());
        var archive = await Add(path);

        var act = async () => await ModlistArchive.ReadBlob(fileStore, archive, "no-such-blob");

        (await act.Should().ThrowAsync<UnsupportedModlistException>()).Which.Message.Should().Contain("no-such-blob");
    }

    [Fact]
    public async Task AnArchiveWithoutAModlistEntryIsRefused()
    {
        await using var folder = temporaryFileManager.CreateFolder();
        var path = folder.Path.Combine("not-a-list.wabbajack");
        using (var zip = new System.IO.Compression.ZipArchive(File.Create(path.ToString()), System.IO.Compression.ZipArchiveMode.Create))
        {
            using var entry = zip.CreateEntry("something-else").Open();
            entry.Write("x"u8);
        }
        var archive = await Add(path);

        var act = async () => await ModlistArchive.ReadModlist(fileStore, archive);

        (await act.Should().ThrowAsync<UnsupportedModlistException>()).Which.Message.Should().Contain("modlist");
    }
}
