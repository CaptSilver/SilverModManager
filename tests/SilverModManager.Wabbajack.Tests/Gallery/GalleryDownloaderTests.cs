using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Library;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Abstractions.Downloads;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Gallery;
using SilverModManager.Wabbajack.Models;
using SilverModManager.Wabbajack.Tests.Acquire;
using SilverModManager.Wabbajack.Tests.Fixtures;

namespace SilverModManager.Wabbajack.Tests.Gallery;

public class GalleryDownloaderTests(IServiceProvider provider, ILibraryService libraryService, IConnection connection, TemporaryFileManager temporaryFileManager)
{
    /// <summary>Hands out a job that writes the given bytes, whatever the address.</summary>
    private sealed class FixedDownloads(IServiceProvider provider, byte[] bytes) : IModlistDownloadJobs
    {
        public List<Uri> Requested { get; } = [];
        public AbsolutePath? Destination { get; private set; }

        public IJobTask<IDownloadJob, AbsolutePath> Create(Uri url, AbsolutePath destination)
        {
            Requested.Add(url);
            Destination = destination;
            var job = new FakeDownloadJob { Destination = destination, Bytes = bytes, Gauge = new ConcurrencyGauge() };
            return provider.GetRequiredService<IJobMonitor>().Begin<FakeDownloadJob, AbsolutePath>(job);
        }
    }

    private static byte[] ModlistBytes(AbsolutePath folder, string name)
    {
        var path = WabbajackFixture.Write(folder, WabbajackFixture.EmptyList(name), fileName: "built.wabbajack");
        return File.ReadAllBytes(path.ToString());
    }

    private static GalleryEntry EntryFor(byte[] bytes, Hash? hash = null, ulong? size = null) => new()
    {
        Title = $"Fixture list {Guid.NewGuid():N}",
        Author = "Someone",
        GalleryGame = "skyrimspecialedition",
        MachineUrl = "fixture-list",
        Repository = "wabbajack-tools/mod-lists",
        Download = new Uri("https://authored-files.wabbajack.org/Fixture_List.wabbajack_ff"),
        DownloadHash = hash ?? Xx64Hasher.Hash(bytes),
        DownloadSize = size ?? (ulong)bytes.Length,
    };

    /// <summary>The database is shared with the tests running alongside, so look for this entry's file, not a count.</summary>
    private void NothingWasAdded(GalleryEntry entry)
        => LibraryFile.All(connection.Db).Select(f => f.FileName.ToString()).Should().NotContain(WabbajackGalleryDownloader.FileNameFor(entry));

    private WabbajackGalleryDownloader Downloader(FixedDownloads downloads) => new(downloads, libraryService, connection, temporaryFileManager);

    [Fact]
    public async Task ADownloadedListEntersTheLibraryAsAModlistRecordedAsFromTheGallery()
    {
        await using var folder = temporaryFileManager.CreateFolder();
        var bytes = ModlistBytes(folder.Path, "From the gallery");
        var downloads = new FixedDownloads(provider, bytes);
        var entry = EntryFor(bytes);

        var model = await Downloader(downloads).Download(entry, TestContext.Current.CancellationToken);

        downloads.Requested.Should().Equal(entry.Download!);
        model.Name.Should().Be("From the gallery");
        model.Contains(WabbajackModlistLibraryFile.GalleryDownloadUrl).Should().BeTrue();
        WabbajackModlistLibraryFile.GalleryDownloadUrl.TryGetValue(model, out var recorded).Should().BeTrue();
        recorded.Should().Be(entry.Download!.ToString());
        WabbajackModlistLibraryFile.All(connection.Db).Select(m => m.Id).Should().Contain(model.Id);
    }

    [Fact]
    public async Task AListAddedFromDiskIsNotFromTheGallery()
    {
        await using var folder = temporaryFileManager.CreateFolder();
        var path = WabbajackFixture.Write(folder.Path, WabbajackFixture.EmptyList("From disk"));

        var added = await libraryService.AddLocalFile(path);

        added.AsLibraryFile().TryGetAsWabbajackModlistLibraryFile(out var model).Should().BeTrue();
        model.Contains(WabbajackModlistLibraryFile.GalleryDownloadUrl).Should().BeFalse();
    }

    [Fact]
    public async Task AHashMismatchDeletesTheFileAndAddsNothing()
    {
        await using var folder = temporaryFileManager.CreateFolder();
        var bytes = ModlistBytes(folder.Path, "Tampered");
        var downloads = new FixedDownloads(provider, bytes);

        var entry = EntryFor(bytes, hash: Hash.From(42));

        var act = async () => await Downloader(downloads).Download(entry, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<GalleryDownloadException>()).Which.Message.Should().Contain("hash");
        downloads.Destination!.Value.FileExists.Should().BeFalse();
        NothingWasAdded(entry);
    }

    [Fact]
    public async Task ASizeMismatchDeletesTheFileAndAddsNothing()
    {
        await using var folder = temporaryFileManager.CreateFolder();
        var bytes = ModlistBytes(folder.Path, "Short");
        var downloads = new FixedDownloads(provider, bytes);

        var entry = EntryFor(bytes, size: (ulong)bytes.Length + 1);

        var act = async () => await Downloader(downloads).Download(entry, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<GalleryDownloadException>()).Which.Message.Should().Contain("size");
        downloads.Destination!.Value.FileExists.Should().BeFalse();
        NothingWasAdded(entry);
    }

    [Fact]
    public async Task AnEntryWithoutADownloadHashOrSizeIsRefusedBeforeAnythingIsFetched()
    {
        var downloads = new FixedDownloads(provider, [1]);
        var entry = EntryFor([1]) with { DownloadHash = null };

        var act = async () => await Downloader(downloads).Download(entry, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<GalleryDownloadException>();
        downloads.Requested.Should().BeEmpty();
    }

    [Fact]
    public async Task VerifiedBytesThatAreNotAModlistAreReportedAndNotKept()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var downloads = new FixedDownloads(provider, bytes);

        var entry = EntryFor(bytes);

        var act = async () => await Downloader(downloads).Download(entry, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<GalleryDownloadException>();
        NothingWasAdded(entry);
    }

    [Fact]
    public async Task TheFileNameIsMadeFromTheTitleWithoutPathCharacters()
    {
        await using var folder = temporaryFileManager.CreateFolder();
        var bytes = ModlistBytes(folder.Path, "Named");
        var downloads = new FixedDownloads(provider, bytes);
        var entry = EntryFor(bytes) with { Title = "../../etc/evil: list" };

        await Downloader(downloads).Download(entry, TestContext.Current.CancellationToken);

        downloads.Destination!.Value.FileName.Should().EndWith(".wabbajack");
        downloads.Destination.Value.FileName.Should().NotContainAny("/", "\\", ":");
    }
}
