using NexusMods.Paths;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Library;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Gallery;
using SilverModManager.Wabbajack.Models;
using static SilverModManager.Wabbajack.Tests.Gallery.GalleryLibraryFiles;

namespace SilverModManager.Wabbajack.Tests.Gallery;

public class ModlistIdentityBackfillTests(ILibraryService libraryService, IConnection connection, TemporaryFileManager temporaryFileManager)
{
    private ModlistIdentityBackfill Backfill => new(connection, NullLogger<ModlistIdentityBackfill>.Instance);

    private static string Unique(string prefix) => $"{prefix} {Guid.NewGuid():N}";

    private Task<WabbajackModlistLibraryFile.ReadOnly> FromGallery(string name, string? author = "Someone", string? fileName = null)
        => Add(libraryService, temporaryFileManager, connection, name, author, fileName: fileName, fromGallery: true);

    private WabbajackModlistLibraryFile.ReadOnly Fresh(WabbajackModlistLibraryFile.ReadOnly file) => WabbajackModlistLibraryFile.Load(connection.Db, file.Id);

    [Fact]
    public async Task AnExactHashMatchWinsOverATitleMatch()
    {
        var name = Unique("Hash");
        var file = await FromGallery(name);
        var byHash = Entry("Renamed by the gallery", machineUrl: "by-hash") with { DownloadHash = file.AsLibraryFile().XxHash64Identity() };
        var byTitle = Entry(name, machineUrl: "by-title");

        await Backfill.Run(Listing(byTitle, byHash), TestContext.Current.CancellationToken);

        Fresh(file).MachineUrl.Value.Should().Be("by-hash");
    }

    [Fact]
    public async Task TheLibraryFileNameMatchesTheGalleryTitle()
    {
        var title = Unique("Named");
        var entry = Entry(title, author: "Nobody", machineUrl: "by-file-name");
        var file = await FromGallery(Unique("Internal name"), author: "Somebody else", fileName: WabbajackGalleryDownloader.FileNameFor(entry));

        await Backfill.Run(Listing(entry), TestContext.Current.CancellationToken);

        Fresh(file).MachineUrl.Value.Should().Be("by-file-name");
        Fresh(file).Repository.Value.Should().Be(entry.Repository);
    }

    [Fact]
    public async Task OneTitleAndAuthorMatchWritesTheIdentity()
    {
        var name = Unique("Title");
        var file = await FromGallery(name.ToUpperInvariant(), author: "SOMEONE");
        var entry = Entry(name.ToLowerInvariant(), author: "someone", machineUrl: "by-title") with { DateUpdated = DateTimeOffset.UnixEpoch };

        await Backfill.Run(Listing(entry, Entry(Unique("Unrelated"), machineUrl: "unrelated")), TestContext.Current.CancellationToken);

        var after = Fresh(file);
        after.MachineUrl.Value.Should().Be("by-title");
        after.Repository.Value.Should().Be(entry.Repository);
        after.Contains(WabbajackModlistLibraryFile.GalleryDateUpdated).Should().BeFalse("the date of the release the user has is not known");
    }

    [Fact]
    public async Task TwoTitleMatchesLeaveTheFileAndAreOfferedAsCandidates()
    {
        var name = Unique("Twice");
        var file = await FromGallery(name);
        var first = Entry(name, machineUrl: "one");
        var second = Entry(name, machineUrl: "two", repository: "someone/community-lists");
        var listing = Listing(first, second);

        await Backfill.Run(listing, TestContext.Current.CancellationToken);

        Fresh(file).Contains(WabbajackModlistLibraryFile.MachineUrl).Should().BeFalse();
        Backfill.Candidates(file, listing).Should().BeEquivalentTo([first, second]);
    }

    [Fact]
    public async Task ATitleMatchInAnotherGameIsNotACandidate()
    {
        var name = Unique("Game");
        var file = await FromGallery(name);
        var elsewhere = Entry(name, game: "fallout4");

        await Backfill.Run(Listing(elsewhere), TestContext.Current.CancellationToken);

        Fresh(file).Contains(WabbajackModlistLibraryFile.MachineUrl).Should().BeFalse();
        Backfill.Candidates(file, Listing(elsewhere)).Should().BeEmpty();
    }

    [Fact]
    public async Task AFileAddedFromDiskIsNeverBackfilled()
    {
        var name = Unique("Disk");
        var file = await Add(libraryService, temporaryFileManager, connection, name, "Someone");

        await Backfill.Run(Listing(Entry(name)), TestContext.Current.CancellationToken);

        Fresh(file).Contains(WabbajackModlistLibraryFile.MachineUrl).Should().BeFalse();
    }

    [Fact]
    public async Task AFileWithAnIdentityIsNotTouchedByABackfill()
    {
        var name = Unique("Known");
        var file = await WithIdentity(connection, await FromGallery(name), "mine", "my/repository");

        await Backfill.Run(Listing(Entry(name, machineUrl: "other")), TestContext.Current.CancellationToken);

        Fresh(file).MachineUrl.Value.Should().Be("mine");
        Fresh(file).Repository.Value.Should().Be("my/repository");
    }

    [Fact]
    public async Task AssigningAPickedEntryWritesItsIdentity()
    {
        var file = await FromGallery(Unique("Picked"));
        var entry = Entry("Whatever", machineUrl: "picked", repository: "someone/community-lists");

        await Backfill.Assign(file, entry);

        Fresh(file).MachineUrl.Value.Should().Be("picked");
        Fresh(file).Repository.Value.Should().Be("someone/community-lists");
    }

    [Fact]
    public async Task AnIncompleteListingOnlyAcceptsAnExactHashMatch()
    {
        var name = Unique("Partial");
        var file = await FromGallery(name);
        var official = Entry(name, machineUrl: "official");
        var partial = Listing(official) with { FailedRepositories = ["someone/community-lists"] };

        await Backfill.Run(partial, TestContext.Current.CancellationToken);

        Fresh(file).Contains(WabbajackModlistLibraryFile.MachineUrl).Should().BeFalse();
        Backfill.Candidates(file, partial).Should().BeEmpty();

        var byHash = official with { DownloadHash = file.AsLibraryFile().XxHash64Identity() };
        await Backfill.Run(partial with { Entries = [byHash] }, TestContext.Current.CancellationToken);
        Fresh(file).MachineUrl.Value.Should().Be("official");
    }

    [Fact]
    public async Task TheFileNameRuleStaysInTheFilesGame()
    {
        var name = Unique("Games");
        var sse = Entry(name, machineUrl: "sse");
        var file = await FromGallery(name, fileName: WabbajackGalleryDownloader.FileNameFor(sse));
        var fallout = Entry(name, machineUrl: "fo4", game: "fallout4");

        await Backfill.Run(Listing(fallout, sse), TestContext.Current.CancellationToken);

        Fresh(file).MachineUrl.Value.Should().Be("sse");
    }
}
