using JetBrains.Annotations;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Library.Listing;

/// <summary>
/// One library item as <c>library list</c> prints it. The Nexus Mods fields are null for anything that is not a Nexus
/// Mods file, and <see cref="Size"/> and <see cref="InStore"/> are null for an item that is not a file.
/// </summary>
[PublicAPI]
public sealed record LibraryItemRow(
    string Id,
    string Name,
    string Kind,
    string? Host,
    uint? NexusGameId,
    uint? NexusModId,
    uint? NexusFileId,
    string? ModPageId,
    string? ModPageName,
    string? Version,
    ulong? Size,
    bool? InStore,
    string? ThumbnailUri,
    bool Dangling,
    string[] Loadouts);

/// <summary>
/// Reads the library items of a game or a loadout (archive members, which the Library page never shows, are left out
/// of the unfiltered list) into <see cref="LibraryItemRow"/>s. It only reads: a digest the row
/// lacks is not computed and written back.
/// </summary>
[PublicAPI]
public sealed class LibraryListing(IConnection connection, LibraryFileDigests digests, IEnumerable<ILibraryItemKind> kinds)
{
    /// <summary>
    /// The items destined for <paramref name="game"/> (including the ones no game is assigned to, as the game's Library
    /// page shows them) and linked into <paramref name="loadout"/>; a null filter does not narrow the list.
    /// </summary>
    public async ValueTask<IReadOnlyList<LibraryItemRow>> List(GameId? game, Loadout.ReadOnly? loadout)
    {
        var db = connection.Db;
        var loadoutsByItem = LoadoutNamesByItem(db);

        IEnumerable<LibraryItem.ReadOnly> items = game is { } gameId ? LibraryDestinations.ShownFor(db, gameId) : LibraryItem.All(db).Where(item => !LibraryArchiveFileEntry.IsMember(db, item.Id));
        if (loadout is { } only)
        {
            var linked = LibraryLinkedLoadoutItem.All(db)
                .Where(link => link.AsLoadoutItem().LoadoutId == only.LoadoutId)
                .Select(link => link.LibraryItem.Id)
                .ToHashSet();
            items = items.Where(item => linked.Contains(item.Id));
        }

        var rows = new List<LibraryItemRow>();
        foreach (var item in items.DistinctBy(item => item.Id))
        {
            var names = loadoutsByItem.TryGetValue(item.Id, out var found) ? found : [];
            rows.Add(await ToRow(item, names));
        }

        return rows.OrderBy(row => row.Name, StringComparer.Ordinal).ThenBy(row => row.Id, StringComparer.Ordinal).ToArray();
    }

    private static Dictionary<EntityId, string[]> LoadoutNamesByItem(IDb db)
    {
        return LibraryLinkedLoadoutItem.All(db)
            .GroupBy(link => link.LibraryItem.Id)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(link => Loadout.Load(db, link.AsLoadoutItem().LoadoutId))
                    .Where(loadout => loadout.IsValid())
                    .Select(loadout => loadout.Name)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray());
    }

    private async ValueTask<LibraryItemRow> ToRow(LibraryItem.ReadOnly item, string[] loadouts)
    {
        var hasFile = item.TryGetAsLibraryFile(out var file);
        bool? inStore = null;
        var dangling = false;
        ulong? size = null;
        if (hasFile)
        {
            inStore = await digests.HasBytes(file);
            dangling = await digests.IsDangling(file);
            size = file.Size.Value;
        }

        var match = kinds.OrderBy(kind => kind.Order).Select(kind => kind.Match(item)).FirstOrDefault(found => found is not null)
                    ?? new LibraryItemKindMatch(LibraryItemKinds.Other);

        return new LibraryItemRow(item.Id.ToString(), item.Name, match.Kind, match.Host, match.NexusGameId, match.NexusModId, match.NexusFileId, match.ModPageId, match.ModPageName, match.Version, size, inStore, match.ThumbnailUri, dangling, loadouts);
    }
}
