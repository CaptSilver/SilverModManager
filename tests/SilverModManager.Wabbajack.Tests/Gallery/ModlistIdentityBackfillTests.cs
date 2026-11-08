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

    // The same list after the author published again: the CDN gives each release its own uuid suffix.
    private static readonly Uri RenewedUrl = new("https://authored-files.wabbajack.org/old.wabbajack_39f36b83-0000-0000-0000-000000000002");

    private static GalleryEntry Renewed(GalleryEntry entry, Uri? link = null) => entry with { Download = link ?? RenewedUrl };

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
    public async Task AnIncompleteListingBindsNothingAutomatically()
    {
        var name = Unique("Partial");
        var file = await FromGallery(name);
        var official = Linked(Entry(name, machineUrl: "official"));
        var partial = Listing(official) with { FailedRepositories = ["someone/community-lists"] };

        await Backfill.Run(partial, TestContext.Current.CancellationToken);

        Fresh(file).Contains(WabbajackModlistLibraryFile.MachineUrl).Should().BeFalse();
        Backfill.Candidates(file, partial).Should().BeEmpty();

        // Even the exact link, hash and title: the repository that failed might list a twin that a complete listing would have shown.
        var exact = official with { DownloadHash = file.AsLibraryFile().XxHash64Identity() };
        await Backfill.Run(partial with { Entries = [exact] }, TestContext.Current.CancellationToken);

        Fresh(file).Contains(WabbajackModlistLibraryFile.MachineUrl).Should().BeFalse();
        Fresh(file).Contains(WabbajackModlistLibraryFile.Repository).Should().BeFalse();
    }

    [Fact]
    public async Task AnIncompleteListingStillAllowsTheUsersOwnPick()
    {
        var name = Unique("Picked while partial");
        var file = await FromGallery(name);
        var entry = Linked(Entry(name, machineUrl: "picked-partial")) with { DownloadHash = file.AsLibraryFile().XxHash64Identity() };
        var partial = Listing(entry) with { FailedRepositories = ["someone/community-lists"] };

        // A complete listing would bind this one by link, hash and title, so only the guard keeps it unwritten.
        await Backfill.Run(partial, TestContext.Current.CancellationToken);
        Fresh(file).Contains(WabbajackModlistLibraryFile.MachineUrl).Should().BeFalse();

        await Backfill.Assign(file, entry);

        Fresh(file).MachineUrl.Value.Should().Be("picked-partial");
        Fresh(file).Repository.Value.Should().Be(entry.Repository);
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
            Download = new Uri("HTTPS://Authored-Files.Wabbajack.org/old.wabbajack_00000000-0000-0000-0000-000000000001"),
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
            Download = new Uri("https://authored-files.wabbajack.org/OLD.wabbajack_00000000-0000-0000-0000-000000000001"),
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

    [Fact]
    public async Task ARenewedReleaseOfTheSameListIsBound()
    {
        var name = Unique("Yggdrasil");
        var file = await Add(libraryService, temporaryFileManager, connection, name, "Crit", gameType: "SkyrimVR", fromGallery: true);
        var entry = Renewed(Entry(name, author: "Crit", machineUrl: "yggdrasil_vr", repository: "wj-featured", game: "skyrimvr")) with { DateUpdated = DateTimeOffset.UnixEpoch };

        await Backfill.Run(Listing(entry), TestContext.Current.CancellationToken);

        var after = Fresh(file);
        after.MachineUrl.Value.Should().Be("yggdrasil_vr");
        after.Repository.Value.Should().Be("wj-featured");
        after.Contains(WabbajackModlistLibraryFile.GalleryDateUpdated).Should().BeFalse();
    }

    [Fact]
    public async Task TheRecordedLinkWinsOverARenewedSibling()
    {
        var name = Unique("Both");
        var file = await FromGallery(name);
        var linked = Linked(Entry(name, machineUrl: "linked"));
        var sibling = Renewed(Entry(name, machineUrl: "sibling", repository: "someone/community-lists"));

        Backfill.Candidates(file, Listing(sibling, linked)).Should().BeEquivalentTo([linked]);
    }

    [Fact]
    public async Task ARenewedLinkUnderAnotherTitleIsNotACandidate()
    {
        var file = await FromGallery(Unique("Title"));
        var entry = Renewed(Entry(Unique("Other title")));

        await Backfill.Run(Listing(entry), TestContext.Current.CancellationToken);

        Backfill.Candidates(file, Listing(entry)).Should().BeEmpty();
        Fresh(file).Contains(WabbajackModlistLibraryFile.MachineUrl).Should().BeFalse();
    }

    [Fact]
    public async Task ARenewedLinkUnderAnotherAuthorIsNotACandidate()
    {
        var name = Unique("Author");
        var file = await FromGallery(name, author: "Someone");
        var entry = Renewed(Entry(name, author: "Somebody else"));

        await Backfill.Run(Listing(entry), TestContext.Current.CancellationToken);

        Backfill.Candidates(file, Listing(entry)).Should().BeEmpty();
        Fresh(file).Contains(WabbajackModlistLibraryFile.MachineUrl).Should().BeFalse();
    }

    [Fact]
    public async Task ARenewedLinkInAnotherGameIsNotACandidate()
    {
        var name = Unique("Game");
        var file = await FromGallery(name);
        var entry = Renewed(Entry(name, game: "fallout4"));

        await Backfill.Run(Listing(entry), TestContext.Current.CancellationToken);

        Backfill.Candidates(file, Listing(entry)).Should().BeEmpty();
        Fresh(file).Contains(WabbajackModlistLibraryFile.MachineUrl).Should().BeFalse();
    }

    [Fact]
    public async Task ARenewedLinkOnAnotherHostIsNotACandidate()
    {
        var name = Unique("Host");
        var file = await FromGallery(name);
        var entry = Renewed(Entry(name), new Uri("https://example.org/old.wabbajack_39f36b83-0000-0000-0000-000000000002"));

        await Backfill.Run(Listing(entry), TestContext.Current.CancellationToken);

        Backfill.Candidates(file, Listing(entry)).Should().BeEmpty();
        Fresh(file).Contains(WabbajackModlistLibraryFile.MachineUrl).Should().BeFalse();
    }

    [Fact]
    public async Task ALinkWithNoReleaseSuffixIsNotASibling()
    {
        var name = Unique("Suffix");
        var file = await FromGallery(name);
        var entry = Renewed(Entry(name), new Uri("https://authored-files.wabbajack.org/old.wabbajack"));

        await Backfill.Run(Listing(entry), TestContext.Current.CancellationToken);

        Backfill.Candidates(file, Listing(entry)).Should().BeEmpty();
        Fresh(file).Contains(WabbajackModlistLibraryFile.MachineUrl).Should().BeFalse();
    }

    [Fact]
    public async Task ARenewedLinkOnTheCdnAliasHostIsBound()
    {
        var name = Unique("Alias");
        var file = await FromGallery(name);
        var entry = Renewed(Entry(name, machineUrl: "alias"), new Uri("https://wabbajack.b-cdn.net/old.wabbajack_39f36b83-0000-0000-0000-000000000002"));

        await Backfill.Run(Listing(entry), TestContext.Current.CancellationToken);

        Fresh(file).MachineUrl.Value.Should().Be("alias");
    }

    [Fact]
    public async Task ARenewedLinkFromAnotherRepositoryIsBlockedByTheRealEntry()
    {
        var name = Unique("Mimic");
        var file = await FromGallery(name);
        var official = Renewed(Entry(name, machineUrl: "official"));
        var mimic = Renewed(Entry(name, machineUrl: "mimic", repository: "someone/community-lists"));

        await Backfill.Run(Listing(official, mimic), TestContext.Current.CancellationToken);

        Backfill.Candidates(file, Listing(official, mimic)).Should().BeEquivalentTo([official, mimic]);
        Backfill.AutomaticMatch(file, Listing(official, mimic)).Should().BeNull();
        Fresh(file).Contains(WabbajackModlistLibraryFile.MachineUrl).Should().BeFalse();
    }

    [Fact]
    public async Task OneRenewedCandidateWithALookalikeElsewhereIsOfferedButNotBound()
    {
        var name = Unique("Lookalike");
        var file = await FromGallery(name);
        var renewed = Renewed(Entry(name, machineUrl: "renewed"));
        var lookalike = Entry(name, machineUrl: "elsewhere", repository: "someone/community-lists") with { Download = new Uri("https://example.org/other.wabbajack") };
        var listing = Listing(renewed, lookalike);

        await Backfill.Run(listing, TestContext.Current.CancellationToken);

        Backfill.Candidates(file, listing).Should().BeEquivalentTo([renewed]);
        Backfill.AutomaticMatch(file, listing).Should().BeNull();
        Fresh(file).Contains(WabbajackModlistLibraryFile.MachineUrl).Should().BeFalse();
    }

    [Fact]
    public async Task ARenewedLinkBindsNothingOnAnIncompleteListing()
    {
        var name = Unique("Renewed partial");
        var file = await FromGallery(name);
        var partial = Listing(Renewed(Entry(name))) with { FailedRepositories = ["someone/community-lists"] };

        await Backfill.Run(partial, TestContext.Current.CancellationToken);

        Backfill.Candidates(file, partial).Should().BeEmpty();
        Fresh(file).Contains(WabbajackModlistLibraryFile.MachineUrl).Should().BeFalse();
    }

    [Fact]
    public async Task ARenewedReleaseIsConsideredWhenTheLinkedEntryIsFilteredOut()
    {
        var name = Unique("Filtered");
        var file = await FromGallery(name);
        var otherGame = Linked(Entry(name, machineUrl: "other-game", game: "fallout4"));
        var renewed = Renewed(Entry(name, machineUrl: "renewed"));
        var listing = Listing(otherGame, renewed);

        Backfill.Candidates(file, listing).Should().BeEquivalentTo([renewed]);

        await Backfill.Run(listing, TestContext.Current.CancellationToken);

        Fresh(file).MachineUrl.Value.Should().Be("renewed");
    }

    [Fact]
    public async Task ASquatterHoldingTheOldLinkDoesNotHideTheRenewedRealEntryFromThePicker()
    {
        var name = Unique("Squatted");
        var file = await FromGallery(name);
        var real = Renewed(Entry(name, machineUrl: "real"));
        var squatter = Linked(Entry(name, machineUrl: "squatter", repository: "someone/community-lists")) with { DownloadHash = file.AsLibraryFile().XxHash64Identity() };
        var listing = Listing(real, squatter);

        await Backfill.Run(listing, TestContext.Current.CancellationToken);

        Backfill.AutomaticMatch(file, listing).Should().BeNull();
        Backfill.PickerCandidates(file, listing).Should().BeEquivalentTo([squatter, real]);
        Fresh(file).Contains(WabbajackModlistLibraryFile.MachineUrl).Should().BeFalse();
    }

    [Fact]
    public async Task ARecordedLinkWithNoReleaseSuffixHasNoSiblings()
    {
        var name = Unique("Unsuffixed file");
        var file = await Add(libraryService, temporaryFileManager, connection, name, "Someone", fromGallery: true, downloadUrl: new Uri("https://authored-files.wabbajack.org/old.wabbajack"));
        var entry = Renewed(Entry(name));

        await Backfill.Run(Listing(entry), TestContext.Current.CancellationToken);

        Backfill.Candidates(file, Listing(entry)).Should().BeEmpty();
        Fresh(file).Contains(WabbajackModlistLibraryFile.MachineUrl).Should().BeFalse();
    }
}
