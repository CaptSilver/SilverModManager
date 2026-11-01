using SilverModManager.Abstractions.Library;
using SilverModManager.Networking.Thunderstore;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Thunderstore;

// Not SilverModManager.Library.ModSources: App.UI has a static class ModSources and the names would clash.
namespace SilverModManager.Library;

/// <summary>
/// The downloaded versions of one Thunderstore package that have a newer version, and the library items holding them.
/// </summary>
internal sealed record ThunderstoreModUpdate : ModUpdate
{
    /// <summary>
    /// One update per downloaded version, all to <see cref="Newest"/>.
    /// </summary>
    public IReadOnlyList<ThunderstoreUpdate> Updates { get; }

    public ThunderstoreVersionRef Newest { get; }

    private ThunderstoreModUpdate(IReadOnlyList<ThunderstoreUpdate> updates, ThunderstoreUpdate newest, ThunderstoreVersionNumber current)
        : base(
            [.. updates.Select(static update => LibraryItemId.From(update.LibraryFileId))],
            current.ToString(),
            newest.Newest.Version.ToString(),
            newest.PublishedAt)
    {
        Updates = updates;
        Newest = newest.Newest;
    }

    /// <summary>
    /// None when <paramref name="updates"/> is empty. The updates are for one package, so they agree on the newest
    /// version; the version shown as current is the highest of the ones they replace.
    /// </summary>
    public static ThunderstoreModUpdate? From(IReadOnlyList<ThunderstoreUpdate> updates)
    {
        if (updates.Count == 0) return null;

        var newest = updates[0];
        var current = updates[0].Current.Version;
        foreach (var update in updates)
        {
            if (update.Newest.Version > newest.Newest.Version) newest = update;
            if (update.Current.Version > current) current = update.Current.Version;
        }
        return new ThunderstoreModUpdate(updates, newest, current);
    }
}
