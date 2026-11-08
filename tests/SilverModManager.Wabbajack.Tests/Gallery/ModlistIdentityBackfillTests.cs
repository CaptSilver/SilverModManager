using NexusMods.Hashing.xxHash3;
using SilverModManager.Sdk.Hashes;
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

    // An entry whose download link is the one FromGallery files were fetched from.
    private static GalleryEntry Linked(GalleryEntry entry) => entry with { Download = OldDownloadUrl };

    private WabbajackModlistLibraryFile.ReadOnly Fresh(WabbajackModlistLibraryFile.ReadOnly file) => WabbajackModlistLibraryFile.Load(connection.Db, file.Id);

    [Fact]
    public async Task AnExactHashMatchWinsOverATitleMatch()
    {
        var name = Unique("Hash");
        var file = await FromGallery(name);
        var byHash = Linked(Entry(name, machineUrl: "by-hash")) with { DownloadHash = file.AsLibraryFile().XxHash64Identity() };
        var byTitle = Linked(Entry(name, machineUrl: "by-title"));

        await Backfill.Run(Listing(byTitle, byHash), TestContext.Current.CancellationToken);

        Fresh(file).MachineUrl.Value.Should().Be("by-hash");
    }

    [Fact]
    public async Task TheLibraryFileNameMatchesTheGalleryTitle()
    {
        var title = Unique("Named");
        var entry = Linked(Entry(title, author: "Nobody", machineUrl: "by-file-name"));
        var file = await FromGallery(title, author: "Somebody else", fileName: WabbajackGalleryDownloader.FileNameFor(entry));

        await Backfill.Run(Listing(entry), TestContext.Current.CancellationToken);

        Fresh(file).MachineUrl.Value.Should().Be("by-file-name");
        Fresh(file).Repository.Value.Should().Be(entry.Repository);
    }

    [Fact]
    public async Task OneTitleAndAuthorMatchWritesTheIdentity()
    {
        var name = Unique("Title");
        var file = await FromGallery(name.ToUpperInvariant(), author: "SOMEONE");
        var entry = Linked(Entry(name.ToLowerInvariant(), author: "someone", machineUrl: "by-title")) with { DateUpdated = DateTimeOffset.UnixEpoch };

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
        var first = Linked(Entry(name, machineUrl: "one"));
        var second = Linked(Entry(name, machineUrl: "two", repository: "someone/community-lists"));
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
        var elsewhere = Linked(Entry(name, game: "fallout4"));

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

        var wouldBind = Linked(Entry(name, machineUrl: "other")) with { DownloadHash = file.AsLibraryFile().XxHash64Identity() };

        await Backfill.Run(Listing(wouldBind), TestContext.Current.CancellationToken);

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
        var official = Linked(Entry(name, machineUrl: "official"));
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
        var sse = Linked(Entry(name, machineUrl: "sse"));
        var file = await FromGallery(name, fileName: WabbajackGalleryDownloader.FileNameFor(sse));
        var fallout = Linked(Entry(name, machineUrl: "fo4", game: "fallout4"));

        await Backfill.Run(Listing(fallout, sse), TestContext.Current.CancellationToken);

        Fresh(file).MachineUrl.Value.Should().Be("sse");
    }

    [Fact]
    public async Task AHashMatchFromAnEntryWithAnotherDownloadLinkIsNotBound()
    {
        var name = Unique("Moved");
        var file = await FromGallery(name);
        var hash = file.AsLibraryFile().XxHash64Identity();
        // The official entry moved to a new release; another repository still advertises the old bytes at its own link.
        var official = Entry(name, machineUrl: "official") with { Download = new Uri("https://authored-files.wabbajack.org/new.wabbajack_2"), DownloadHash = FileIdentity.XxHash64(Hash.From(1UL)) };
        var other = Entry(Unique("Other"), machineUrl: "other", repository: "someone/community-lists") with { Download = new Uri("https://example.org/other.wabbajack"), DownloadHash = hash };

        await Backfill.Run(Listing(official, other), TestContext.Current.CancellationToken);

        Fresh(file).Contains(WabbajackModlistLibraryFile.MachineUrl).Should().BeFalse();
        Backfill.Candidates(file, Listing(official, other)).Should().BeEmpty();
    }

    [Fact]
    public async Task AnEntryWithTheRecordedLinkAndHashIsBound()
    {
        var name = Unique("Linked");
        var file = await FromGallery(name);
        var entry = Linked(Entry(name, machineUrl: "linked")) with
        {
            Download = new Uri("HTTPS://Authored-Files.Wabbajack.org/old.wabbajack_1"),
            DownloadHash = file.AsLibraryFile().XxHash64Identity(),
        };

        await Backfill.Run(Listing(entry), TestContext.Current.CancellationToken);

        Fresh(file).MachineUrl.Value.Should().Be("linked");
        Fresh(file).Repository.Value.Should().Be(entry.Repository);
    }

    [Fact]
    public async Task AnEntryWhoseLinkDiffersInPathCaseIsNotBound()
    {
        var file = await FromGallery(Unique("Case"));
        var entry = Entry("Whatever", machineUrl: "case") with
        {
            Download = new Uri("https://authored-files.wabbajack.org/OLD.wabbajack_1"),
            DownloadHash = file.AsLibraryFile().XxHash64Identity(),
        };

        await Backfill.Run(Listing(entry), TestContext.Current.CancellationToken);

        Fresh(file).Contains(WabbajackModlistLibraryFile.MachineUrl).Should().BeFalse();
    }

    [Fact]
    public async Task ASameTitledListInAnotherRepositoryLeavesTheFileForTheUser()
    {
        var name = Unique("Twin");
        var file = await FromGallery(name);
        var match = Linked(Entry(name, machineUrl: "mine")) with { DownloadHash = file.AsLibraryFile().XxHash64Identity() };
        var twin = Entry(name.ToUpperInvariant(), machineUrl: "twin", repository: "someone/community-lists") with { Download = new Uri("https://example.org/twin.wabbajack") };

        await Backfill.Run(Listing(match, twin), TestContext.Current.CancellationToken);

        Fresh(file).Contains(WabbajackModlistLibraryFile.MachineUrl).Should().BeFalse();
    }

    [Fact]
    public async Task TheSameMachineUrlInAnotherRepositoryLeavesTheFileForTheUser()
    {
        var name = Unique("Same");
        var file = await FromGallery(name);
        var match = Linked(Entry(name, machineUrl: "shared-id")) with { DownloadHash = file.AsLibraryFile().XxHash64Identity() };
        var twin = Entry(Unique("Different title"), machineUrl: "shared-id", repository: "someone/community-lists");

        await Backfill.Run(Listing(match, twin), TestContext.Current.CancellationToken);

        Fresh(file).Contains(WabbajackModlistLibraryFile.MachineUrl).Should().BeFalse();
    }

    [Fact]
    public async Task AnotherRepositoryAdvertisingTheRecordedLinkAndHashUnderItsOwnTitleIsNotBound()
    {
        var name = Unique("Official");
        var file = await FromGallery(name);
        var official = Entry(name, machineUrl: "official") with { Download = new Uri("https://authored-files.wabbajack.org/new.wabbajack_2"), DownloadHash = FileIdentity.XxHash64(Hash.From(1UL)) };
        var squatter = Linked(Entry(Unique("Squatter"), machineUrl: "squatter", repository: "someone/community-lists")) with { DownloadHash = file.AsLibraryFile().XxHash64Identity() };

        await Backfill.Run(Listing(official, squatter), TestContext.Current.CancellationToken);

        Fresh(file).Contains(WabbajackModlistLibraryFile.MachineUrl).Should().BeFalse("the link proves the bytes, not the repository that lists them");
    }
}
