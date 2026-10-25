using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Gallery;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.App.UI.Pages.WabbajackGallery;

/// <inheritdoc/>
public sealed class WabbajackGalleryService(WabbajackGalleryClient client, WabbajackGalleryDownloader downloader, IConnection connection) : IWabbajackGalleryService
{
    public Task<GalleryListing> GetListing(bool refresh, CancellationToken cancellationToken) => client.Get(refresh, cancellationToken);

    public IReadOnlyDictionary<string, LibraryFileId> ListsInLibrary()
    {
        var lists = new Dictionary<string, LibraryFileId>(StringComparer.Ordinal);
        foreach (var file in WabbajackModlistLibraryFile.All(connection.Db))
        {
            if (file.Contains(WabbajackModlistLibraryFile.GalleryDownloadUrl))
                lists[file.GalleryDownloadUrl.Value] = LibraryFileId.From(file.Id);
        }
        return lists;
    }

    public async Task<LibraryFileId> Download(GalleryEntry entry, CancellationToken cancellationToken)
        => LibraryFileId.From((await downloader.Download(entry, cancellationToken)).Id);
}
