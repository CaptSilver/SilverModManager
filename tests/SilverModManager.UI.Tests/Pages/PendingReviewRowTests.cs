using FluentAssertions;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.App.UI.Resources;
using SilverModManager.Games.RedEngine.Cyberpunk2077;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.UI.Tests.Pages;

public class PendingReviewRowTests(IServiceProvider provider) : AUiTest(provider)
{
    // The database is shared between tests, and user, collection and revision ids are unique in it.
    private static long _nextId = 1_000;

    /// <summary>A pending row and the download it is for, written straight to the database.</summary>
    internal static async Task<(NexusCollectionPendingInstall.ReadOnly Row, CollectionDownload.ReadOnly Download)> CreateRow(
        IConnection connection, ReviewKind kind, string? reason, bool isOptional = false, string name = "Some mod")
    {
        var id = (ulong)Interlocked.Increment(ref _nextId);
        using var tx = connection.BeginTransaction();
        var author = new User.New(tx) { Name = "author", NexusId = id };
        var collection = new CollectionMetadata.New(tx)
        {
            CollectionId = CollectionId.From(id),
            Slug = CollectionSlug.From($"pending-review-{id}"),
            Name = "pack",
            GameId = Cyberpunk2077Game.NexusModsGameId.Value,
            AuthorId = author.Id,
        };
        var revision = new CollectionRevisionMetadata.New(tx)
        {
            RevisionId = RevisionId.From(id),
            RevisionNumber = RevisionNumber.From(1),
            CollectionId = collection.Id,
        };
        var download = new CollectionDownload.New(tx) { Name = name, IsOptional = isOptional, ArrayIndex = 0, CollectionRevisionId = revision.Id };
        var row = new NexusCollectionPendingInstall.New(tx)
        {
            // The row only points at the group; the tests never need the group itself.
            CollectionGroupId = NexusCollectionLoadoutGroupId.From(EntityId.From(0xABCDEF)),
            DownloadId = CollectionDownloadId.From(download.Id),
            Kind = kind,
        };
        // A row recorded without a reason has no Reason datom at all, which is not the same as an empty one.
        if (reason is not null) row.Reason = reason;
        var result = await tx.Commit();
        return (NexusCollectionPendingInstall.Load(result.Db, result[row.Id]), CollectionDownload.Load(result.Db, result[download.Id]));
    }

    [Theory]
    [InlineData(ReviewKind.NoInstaller)]
    [InlineData(ReviewKind.NeedsChoices)]
    public async Task Headline_IsFixedByKind(ReviewKind kind)
    {
        var (row, download) = await CreateRow(Connection, kind, "whatever the installer said");

        var review = new PendingReviewRow(row, download);

        review.Headline.Should().Be(kind == ReviewKind.NoInstaller
            ? Language.CollectionReview_NoInstaller
            : Language.CollectionReview_NeedsChoices);
        review.Kind.Should().Be(kind);
        review.Name.Should().Be("Some mod");
        review.RowId.Should().Be(row.Id);
    }

    [Theory]
    [InlineData("Archive contains no matching files", "Archive contains no matching files")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public async Task Detail_IsTheReasonOrEmpty(string? reason, string expected)
    {
        var (row, download) = await CreateRow(Connection, ReviewKind.NoInstaller, reason);

        new PendingReviewRow(row, download).Detail.Should().Be(expected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IsOptional_FollowsTheDownload(bool isOptional)
    {
        var (row, download) = await CreateRow(Connection, ReviewKind.NoInstaller, "", isOptional);

        new PendingReviewRow(row, download).IsOptional.Should().Be(isOptional);
    }

    [Fact]
    public async Task IsArchiveMissing_StartsFalse_AndFollowsTheValueItIsGiven()
    {
        var (row, download) = await CreateRow(Connection, ReviewKind.NoInstaller, "");

        var review = new PendingReviewRow(row, download);

        review.IsArchiveMissing.Value.Should().BeFalse();
        review.IsArchiveMissing.Value = true;
        review.IsArchiveMissing.Value.Should().BeTrue();
    }
}
