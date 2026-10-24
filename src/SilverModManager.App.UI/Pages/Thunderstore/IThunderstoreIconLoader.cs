using Avalonia.Media;

namespace SilverModManager.App.UI.Pages.Thunderstore;

/// <summary>
/// Supplies the package icons of the Thunderstore page. Icons are fetched one at a time, when a row asks for its
/// own, so a catalog of thousands of packages costs nothing until it is scrolled through.
/// </summary>
public interface IThunderstoreIconLoader
{
    /// <summary>Shown while an icon loads, and for a package that has none or whose icon can't be fetched.</summary>
    IImage Placeholder { get; }

    /// <summary>The icon when it was loaded before, so a row scrolled back into view doesn't flash the placeholder.</summary>
    IImage? TryGetCached(Uri url);

    /// <summary>
    /// Fetches the icon, or returns <see cref="Placeholder"/> when it can't be had (a failure is not remembered, so
    /// the next request tries again). Cancelling, as a row does when it scrolls away, throws
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<IImage> LoadAsync(Uri url, CancellationToken cancellationToken);
}
