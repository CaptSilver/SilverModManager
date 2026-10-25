using Avalonia.Media;

namespace SilverModManager.App.UI.Controls.Cards;

/// <summary>
/// Supplies the thumbnails of the card grids (Thunderstore packages, Nexus Mods mods). Images are fetched one at a
/// time, when a card asks for its own, so a catalog of thousands costs nothing until it is scrolled through.
/// </summary>
public interface IThumbnailLoader
{
    /// <summary>Shown while an image loads, and for an entry that has none or whose image can't be fetched.</summary>
    IImage Placeholder { get; }

    /// <summary>The image when it was loaded before, so a card scrolled back into view doesn't flash the placeholder.</summary>
    IImage? TryGetCached(Uri url);

    /// <summary>
    /// Fetches the image, or returns <see cref="Placeholder"/> when it can't be had (a failure is not remembered, so
    /// the next request tries again). Cancelling, as a card does when it scrolls away, throws
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<IImage> LoadAsync(Uri url, CancellationToken cancellationToken);
}
