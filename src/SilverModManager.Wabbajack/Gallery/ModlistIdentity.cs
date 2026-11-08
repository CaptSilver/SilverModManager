using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack.Gallery;

/// <summary>
/// Pairs a library file with its gallery entry and decides whether the entry is a newer release. A file is matched by
/// <c>(Repository, MachineUrl)</c>; the same machine URL in another repository is another list.
/// </summary>
public static class ModlistIdentity
{
    /// <summary>
    /// The entry the file was downloaded from, or null when the file has no recorded identity or the listing does not
    /// contain it. The second case means "unknown": a refresh that lost a repository says nothing about the list.
    /// </summary>
    public static GalleryEntry? EntryFor(GalleryListing listing, WabbajackModlistLibraryFile.ReadOnly file)
    {
        if (!WabbajackModlistLibraryFile.MachineUrl.TryGetValue(file, out var machineUrl)
            || !WabbajackModlistLibraryFile.Repository.TryGetValue(file, out var repository))
            return null;

        return listing.Entries.FirstOrDefault(e => e.MachineUrl == machineUrl && e.Repository == repository);
    }

    /// <summary>
    /// A release other than the one the file holds. The file's hash decides when it is known; without one the version
    /// strings are compared. An entry the gallery dates before the release the user downloaded is an older mirror,
    /// not an update.
    /// </summary>
    public static bool IsNewer(GalleryEntry entry, WabbajackModlistLibraryFile.ReadOnly file)
    {
        if (entry.IsForcedDown) return false;

        var differs = file.AsLibraryFile().XxHash64Identity() is { } installed && entry.DownloadHash is { } offered
            ? !offered.Equals(installed)
            : entry.Version.Length > 0 && entry.Version != file.Version;
        if (!differs) return false;

        if (!WabbajackModlistLibraryFile.GalleryDateUpdated.TryGetValue(file, out var recorded)) return true;
        return entry.DateUpdated is not { } date || date >= recorded;
    }

    /// <summary>A different file under the date the user already has: the author replaced the release in place.</summary>
    public static bool IsReupload(GalleryEntry entry, WabbajackModlistLibraryFile.ReadOnly file)
        => IsNewer(entry, file)
           && WabbajackModlistLibraryFile.GalleryDateUpdated.TryGetValue(file, out var recorded)
           && entry.DateUpdated == recorded;
}
