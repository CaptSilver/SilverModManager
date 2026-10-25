using SilverModManager.Abstractions.NexusWebApi.Types;

namespace SilverModManager.Networking.NexusWebApi;

/// <summary>One collection of a search result, with the fields a card shows.</summary>
public sealed record NexusCollectionSearchHit(
    CollectionSlug Slug,
    GameDomain GameDomain,
    string Name,
    string Summary,
    string Author,
    int Endorsements,
    int Downloads,
    int ModCount,
    ulong TotalSizeBytes,
    RevisionNumber? LatestRevision,
    DateTimeOffset UpdatedAt,
    Uri? ImageUrl,
    bool AdultContent)
{
    internal static NexusCollectionSearchHit From(ICollectionCard card)
    {
        var revision = card.LatestPublishedRevision;
        return new NexusCollectionSearchHit(
            CollectionSlug.From(card.Slug),
            GameDomain.From(card.Game.DomainName),
            card.Name,
            card.Summary,
            card.User.Name,
            card.Endorsements,
            card.TotalDownloads,
            revision?.ModCount ?? 0,
            ulong.TryParse(revision?.TotalSize, out var size) ? size : 0,
            revision is null ? null : RevisionNumber.From((ulong)revision.RevisionNumber),
            card.UpdatedAt,
            AbsoluteOrNull(card.TileImage?.ThumbnailUrl) ?? AbsoluteOrNull(card.HeaderImage?.Url),
            revision?.AdultContent ?? false);
    }

    private static Uri? AbsoluteOrNull(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
}

/// <summary>One page of results; <see cref="TotalCount"/> counts every match, not just this page.</summary>
public sealed record NexusCollectionsSearchPage(IReadOnlyList<NexusCollectionSearchHit> Hits, int Offset, int TotalCount)
{
    public bool HasMore => Offset + Hits.Count < TotalCount;
}
