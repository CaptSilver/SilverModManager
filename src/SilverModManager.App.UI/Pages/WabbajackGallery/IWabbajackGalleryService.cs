using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
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

    /// <summary>The library file that holds the bytes the entry describes (its published hash), or null when the library lacks that release.</summary>
    LibraryFileId? FindInLibrary(GalleryEntry entry);

    /// <summary>
    /// The install in <paramref name="loadout"/> of an older release of the entry's list (same repository and machine
    /// URL), or null when the list is not installed there or the entry is not newer than what is installed.
    /// </summary>
    InstalledModlist? InstalledOlderThan(GalleryEntry entry, LoadoutId loadout);

    /// <exception cref="GalleryDownloadException">The list can't be fetched or isn't what the gallery described.</exception>
    Task<LibraryFileId> Download(GalleryEntry entry, CancellationToken cancellationToken);
}

/// <summary>An installed Wabbajack list: the loadout group that holds it and the library file it was installed from.</summary>
public sealed record InstalledModlist(EntityId Group, LibraryFileId File);
