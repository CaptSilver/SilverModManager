using SilverModManager.Abstractions.Library.Catalog;
using SilverModManager.App.UI.Controls.Cards;

namespace SilverModManager.App.UI.Pages.Catalog;

/// <summary>
/// One card of the catalog page: what <see cref="CatalogCard"/> says, in the wording the card shows.
/// </summary>
/// <param name="InstallLabel">The catalog's verb for the card: Install, or Subscribe for a source the game or Steam syncs.</param>
public sealed record CatalogCardRow(CatalogCard Card, string InstallLabel)
{
    public string Title => Card.Title;

    public string Summary => Card.Summary;

    public Uri? Icon => Card.Icon;

    public Uri? Page => Card.Page;

    public bool IsAdult => Card.IsAdult;

    /// <summary>"by glarmer", or nothing for a card without a known author.</summary>
    public string OwnerLine => Card.Author == "" ? "" : $"by {Card.Author}";

    /// <summary>"4.0.2  ·  rating 112  ·  1.2M downloads", the small print under the title; no rating segment for a source without one.</summary>
    public string Details => Card.Rating is { } rating
        ? $"{Card.Version}  ·  rating {rating:0}  ·  {CardText.CompactCount(Card.Downloads)} downloads"
        : $"{Card.Version}  ·  {CardText.CompactCount(Card.Downloads)} downloads";
}
