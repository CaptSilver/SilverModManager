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

public class ModlistIdentityTests(ILibraryService libraryService, IConnection connection, TemporaryFileManager temporaryFileManager)
{
    private static readonly DateTimeOffset Released = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private Task<WabbajackModlistLibraryFile.ReadOnly> Installed(string version = "1.0.0.0", bool recordDate = true, string machineUrl = "a-list", string repository = "wabbajack-tools/mod-lists")
        => Add(libraryService, temporaryFileManager, connection, $"Identity {Guid.NewGuid():N}", version: version, fromGallery: true)
            .ContinueWith(t => WithIdentity(connection, t.Result, machineUrl, repository, recordDate ? Released : null)).Unwrap();

    /// <summary>An entry whose hash is what the file already has, so only the field under test differs.</summary>
    private static GalleryEntry SameAs(WabbajackModlistLibraryFile.ReadOnly file) => Entry("x") with { DownloadHash = file.AsLibraryFile().XxHash64Identity(), DateUpdated = Released, Version = file.Version };

    [Fact]
    public async Task NewerWhenTheHashDiffersAndTheDateIsLater()
    {
        var file = await Installed();
        var entry = SameAs(file) with { DownloadHash = Xx64.From(1), DateUpdated = Released.AddDays(5) };

        ModlistIdentity.IsNewer(entry, file).Should().BeTrue();
        ModlistIdentity.IsReupload(entry, file).Should().BeFalse();
    }

    [Fact]
    public async Task NotNewerWhenTheHashIsTheSame()
    {
        var file = await Installed();
        var entry = SameAs(file) with { DateUpdated = Released.AddDays(5) };

        ModlistIdentity.IsNewer(entry, file).Should().BeFalse();
    }

    [Fact]
    public async Task NewerWhenTheFileHasNoRecordedDate()
    {
        var file = await Installed(recordDate: false);
        var entry = SameAs(file) with { DownloadHash = Xx64.From(1), DateUpdated = Released.AddDays(-30) };

        ModlistIdentity.IsNewer(entry, file).Should().BeTrue();
    }

    [Fact]
    public async Task NotNewerWhenTheEntryIsOlderThanTheRecordedDate()
    {
        var file = await Installed();
        var entry = SameAs(file) with { DownloadHash = Xx64.From(1), DateUpdated = Released.AddDays(-1) };

        ModlistIdentity.IsNewer(entry, file).Should().BeFalse();
    }

    [Fact]
    public async Task NeverNewerWhenTheEntryIsForcedDown()
    {
        var file = await Installed();
        var entry = SameAs(file) with { DownloadHash = Xx64.From(1), DateUpdated = Released.AddDays(5), IsForcedDown = true };

        ModlistIdentity.IsNewer(entry, file).Should().BeFalse();
    }

    [Fact]
    public async Task AReuploadWithTheSameDateIsNewerAndLabelledAsAReupload()
    {
        var file = await Installed();
        var entry = SameAs(file) with { DownloadHash = Xx64.From(1), DateUpdated = Released };

        ModlistIdentity.IsNewer(entry, file).Should().BeTrue();
        ModlistIdentity.IsReupload(entry, file).Should().BeTrue();
    }

    [Fact]
    public async Task WithoutAnXxHash64TheVersionStringDecides()
    {
        var file = await Installed(version: "1.0.0.0");
        using (var tx = connection.BeginTransaction())
        {
            tx.RetractAll(connection.Db, file.Id, LibraryFile.XxHash64);
            file = WabbajackModlistLibraryFile.Load((await tx.Commit()).Db, file.Id);
        }
        file.AsLibraryFile().XxHash64Identity().Should().BeNull();

        var other = Entry("x") with { Version = "2.0.0.0", DownloadHash = Xx64.From(1), DateUpdated = Released.AddDays(1) };
        var same = other with { Version = "1.0.0.0" };

        ModlistIdentity.IsNewer(other, file).Should().BeTrue();
        ModlistIdentity.IsNewer(same, file).Should().BeFalse();
    }

    [Fact]
    public async Task AnEntryMissingFromTheListingIsUnknownNotGone()
    {
        var file = await Installed();
        var listing = Listing(Entry("Other list", machineUrl: "other-list"));

        ModlistIdentity.EntryFor(listing, file).Should().BeNull();
        await new ModlistIdentityBackfill(connection, NullLogger<ModlistIdentityBackfill>.Instance).Run(
            Listing(Entry(file.Name, machineUrl: "another-list", repository: "someone/community-lists")), TestContext.Current.CancellationToken);

        var after = WabbajackModlistLibraryFile.Load(connection.Db, file.Id);
        after.MachineUrl.Value.Should().Be("a-list");
        after.Repository.Value.Should().Be("wabbajack-tools/mod-lists");
    }

    [Fact]
    public async Task AFileWithoutAnIdentityMatchesNothing()
    {
        var file = await Add(libraryService, temporaryFileManager, connection, $"Bare {Guid.NewGuid():N}");

        ModlistIdentity.EntryFor(Listing(Entry("x")), file).Should().BeNull();
    }

    [Fact]
    public async Task TwoRepositoriesWithTheSameMachineUrlDoNotMatchEachOther()
    {
        var file = await Installed(repository: "someone/community-lists");
        var official = Entry("Official", repository: "wabbajack-tools/mod-lists");
        var community = Entry("Community", repository: "someone/community-lists");

        ModlistIdentity.EntryFor(Listing(official, community), file).Should().BeSameAs(community);
        ModlistIdentity.EntryFor(Listing(official), file).Should().BeNull();
    }
}
