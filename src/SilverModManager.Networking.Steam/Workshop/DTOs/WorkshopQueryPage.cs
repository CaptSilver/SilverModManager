namespace SilverModManager.Networking.Steam.Workshop.DTOs;

/// <summary>
/// One page of a Workshop search.
/// </summary>
/// <param name="Items">The items on the page, in Steam's order.</param>
/// <param name="NextCursor">What to pass to get the next page; null on the last one.</param>
/// <param name="Total">How many items match the search in all.</param>
public sealed record WorkshopQueryPage(IReadOnlyList<PublishedFileDetails> Items, string? NextCursor, int Total);
