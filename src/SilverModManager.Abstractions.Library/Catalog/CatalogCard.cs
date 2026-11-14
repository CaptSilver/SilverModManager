namespace SilverModManager.Abstractions.Library.Catalog;

/// <summary>
/// What the catalog's source needs back to install a card, opaque to the page. <see cref="Catalog"/> names the
/// catalog that made it, so a card is never handed to another source's install.
/// </summary>
public interface ICatalogHandle
{
    string Catalog { get; }
}

/// <summary>
/// One mod in a catalog listing.
/// </summary>
/// <param name="Id">The source's own id, which the install verb takes.</param>
/// <param name="Rating">Null when the source has no rating.</param>
public sealed record CatalogCard(
    string Id,
    string Title,
    string Author,
    string Summary,
    Uri? Icon,
    Uri? Page,
    string Version,
    long Downloads,
    double? Rating,
    bool IsAdult,
    DateTimeOffset? Updated,
    ICatalogHandle Handle);

/// <summary>
/// What the user asked a catalog for. <see cref="Cursor"/> is the previous page's <see cref="CatalogPage.NextCursor"/>.
/// </summary>
public sealed record CatalogQuery(string Text, CatalogSort Sort, string? Cursor, bool IncludeAdult);

/// <summary>
/// One page of results. <see cref="NextCursor"/> is null on the last page, <see cref="Total"/> null when the source
/// does not say, and <see cref="FromCache"/> true when the list came from a saved copy rather than the source.
/// </summary>
public sealed record CatalogPage(IReadOnlyList<CatalogCard> Cards, string? NextCursor, int? Total, bool FromCache);
