using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Gallery;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.App.UI.Pages.WabbajackGallery;

/// <inheritdoc/>
public sealed class WabbajackGalleryService(IWabbajackGalleryClient client, WabbajackGalleryDownloader downloader, IConnection connection) : IWabbajackGalleryService
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

    public LibraryFileId? FindInLibrary(GalleryEntry entry)
    {
        if (entry.DownloadHash is not { } published) return null;

        foreach (var file in WabbajackModlistLibraryFile.All(connection.Db))
        {
            if (file.AsLibraryFile().XxHash64Identity() is { } held && held.Equals(published)) return LibraryFileId.From(file.Id);
        }
        return null;
    }

    public InstalledModlist? InstalledOlderThan(GalleryEntry entry, LoadoutId loadout)
    {
        var db = connection.Db;
        foreach (var group in WabbajackModlistLoadoutGroup.All(db))
        {
            if (group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().LoadoutId != loadout) continue;

            // An update that stopped halfway is finished or undone first, not stacked with another.
            if (ModlistInstallRuns.IsMidUpdate(group)) continue;

            var installed = group.Modlist;
            if (!installed.IsValid()) continue;
            if (!WabbajackModlistLibraryFile.MachineUrl.TryGetValue(installed, out var machineUrl) || machineUrl != entry.MachineUrl) continue;
            if (!WabbajackModlistLibraryFile.Repository.TryGetValue(installed, out var repository) || repository != entry.Repository) continue;
            if (ModlistIdentity.IsNewer(entry, installed)) return new InstalledModlist(group.Id, LibraryFileId.From(installed.Id));
        }
        return null;
    }

    public async Task<LibraryFileId> Download(GalleryEntry entry, CancellationToken cancellationToken)
        => LibraryFileId.From((await downloader.Download(entry, cancellationToken)).Id);
}
