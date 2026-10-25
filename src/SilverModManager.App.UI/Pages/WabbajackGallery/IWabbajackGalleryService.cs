using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Gallery;

namespace SilverModManager.App.UI.Pages.WabbajackGallery;

/// <summary>
/// What the gallery page needs from the gallery client, the downloader and the library, behind one seam so the view
/// model can be driven without a network or a database.
/// </summary>
public interface IWabbajackGalleryService
{
    /// <exception cref="GalleryUnavailableException">The gallery can't be read and there is no cached copy.</exception>
    Task<GalleryListing> GetListing(bool refresh, CancellationToken cancellationToken);

    /// <summary>The gallery lists already in the library, by the address they were downloaded from.</summary>
    IReadOnlyDictionary<string, LibraryFileId> ListsInLibrary();

    /// <exception cref="GalleryDownloadException">The list can't be fetched or isn't what the gallery described.</exception>
    Task<LibraryFileId> Download(GalleryEntry entry, CancellationToken cancellationToken);
}
