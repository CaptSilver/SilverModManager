using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Networking.NexusWebApi;

/// <summary>One mod of a search result, with the fields a card shows.</summary>
public sealed record NexusModsSearchHit(
    ModId ModId,
    string Uid,
    string Name,
    string Summary,
    string Author,
    string Version,
    int Endorsements,
    int Downloads,
    DateTimeOffset UpdatedAt,
    Uri? ThumbnailUrl,
    Uri? PictureUrl,
    bool AdultContent)
{
    internal static NexusModsSearchHit From(IModCard card) => new(
        ModId.From((uint)card.ModId),
        card.Uid,
        card.Name,
        card.Summary,
        string.IsNullOrWhiteSpace(card.Author) ? card.Uploader.Name : card.Author,
        card.Version,
        card.Endorsements,
        card.Downloads,
        card.UpdatedAt,
        AbsoluteOrNull(card.ThumbnailUrl),
        AbsoluteOrNull(card.PictureUrl),
        card.AdultContent ?? false);

    private static Uri? AbsoluteOrNull(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
}

/// <summary>One page of results; <see cref="TotalCount"/> counts every match, not just this page.</summary>
public sealed record NexusModsSearchPage(IReadOnlyList<NexusModsSearchHit> Hits, int Offset, int TotalCount)
{
    public bool HasMore => Offset + Hits.Count < TotalCount;
}
