using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Abstractions.Games.LoadOrder;

/// <summary>Turns the references a source lists into the groups installed in a collection or list.</summary>
public static class ModOrderMatcher
{
    private const string NoMatch = "no installed mod matches it (optional download not installed, or install failed)";
    private const string NoReference = "carries no md5, mod id or name";
    private const string OnlyModId = "carries a mod id but no file id, md5 or name";
    private const string ListedTwice = "the source lists the same installed mod more than once";

    /// <summary>
    /// Resolves <paramref name="entries"/> (winner first) against the direct child groups of <paramref name="container"/>.
    /// A reference matches by md5, else by modId+fileId, else by name (ordinal, ignore case). Every group the reference
    /// matches is placed (two installs of one download sit together, in their current priority order). An entry that
    /// matches nothing becomes a <see cref="ModOrderDrop"/>; so does one whose groups an earlier entry already placed.
    /// </summary>
    public static ModOrder Match(IDb db, LoadoutItemGroupId container, IReadOnlyList<ModOrderSourceEntry> entries)
    {
        var index = Index.Build(db, container);
        // An optional download is installed from the collection's page, whose tab carries the collection's name, as the container does.
        var containerPage = LoadoutItemGroup.Load(db, container).AsLoadoutItem().Name;
        var placed = new HashSet<LoadoutItemGroupId>();
        var order = new List<ModOrderEntry>();
        var dropped = new List<ModOrderDrop>();

        foreach (var entry in entries)
        {
            var reference = entry.Reference;
            if (!reference.IsIdentified)
            {
                dropped.Add(new ModOrderDrop(reference.Display, reference.HasOnlyModId ? OnlyModId : NoReference));
                continue;
            }

            var candidates = index.Find(reference);
            if (candidates.Count == 0)
            {
                dropped.Add(new ModOrderDrop(reference.Display, NoMatch, containerPage));
                continue;
            }

            var fresh = candidates.Where(placed.Add).ToArray();
            if (fresh.Length == 0)
            {
                dropped.Add(new ModOrderDrop(reference.Display, ListedTwice));
                continue;
            }

            order.AddRange(fresh.Select(group => new ModOrderEntry(group, entry.Enabled)));
        }

        return ModOrder.Create(order, dropped);
    }

    /// <summary>True when a manifest can name the group: its library item is a NexusModsLibraryItem or a DownloadedFile. The one test CollectionCreator and the unshareable-mod card both apply.</summary>
    public static bool CanIdentify(LibraryLinkedLoadoutItem.ReadOnly item)
    {
        var libraryItem = item.LibraryItem;
        return libraryItem.TryGetAsNexusModsLibraryItem(out _) || libraryItem.TryGetAsDownloadedFile(out _);
    }

    /// <summary>The reference a group would be written out as: md5 and Nexus ids when it has them, always its name.</summary>
    public static ModOrderReference ReferenceOf(LoadoutItemGroup.ReadOnly group)
    {
        var md5 = default(Md5Value);
        ModId? modId = null;
        FileId? fileId = null;

        if (group.TryGetAsNexusCollectionItemLoadoutGroup(out var item))
        {
            var download = item.Download;
            if (download.TryGetAsCollectionDownloadNexusMods(out var nexus))
            {
                fileId = nexus.FileUid.FileId;
                modId = nexus.ModUid.ModId;
            }
            else if (download.TryGetAsCollectionDownloadExternal(out var external))
            {
                md5 = external.Md5;
            }
        }

        if (group.TryGetAsLibraryLinkedLoadoutItem(out var linked))
        {
            var libraryItem = linked.LibraryItem;
            if (md5 == default && libraryItem.TryGetAsLibraryFile(out var file) && file.Md5.HasValue)
                md5 = file.Md5.Value;

            if (fileId is null && libraryItem.TryGetAsNexusModsLibraryItem(out var nexusItem))
            {
                fileId = nexusItem.FileMetadata.Uid.FileId;
                modId = nexusItem.ModPageMetadata.Uid.ModId;
            }
        }

        return new ModOrderReference(md5, modId, fileId, group.AsLoadoutItem().Name);
    }

    /// <summary>The container's children keyed three ways, each list with the highest-priority group first.</summary>
    private sealed class Index
    {
        private readonly Dictionary<Md5Value, List<LoadoutItemGroupId>> _byMd5 = new();
        private readonly Dictionary<(ModId, FileId), List<LoadoutItemGroupId>> _byIds = new();
        private readonly Dictionary<string, List<LoadoutItemGroupId>> _byName = new(StringComparer.OrdinalIgnoreCase);

        public static Index Build(IDb db, LoadoutItemGroupId container)
        {
            var index = new Index();
            foreach (var child in ContainerOrder.ChildrenWinnerFirst(db, container))
            {
                var reference = ReferenceOf(child);
                var id = child.LoadoutItemGroupId;
                if (reference.Md5 != default) Add(index._byMd5, reference.Md5, id);
                if (reference.Ids is { } ids) Add(index._byIds, ids, id);
                if (!string.IsNullOrWhiteSpace(reference.Name)) Add(index._byName, reference.Name, id);
            }
            return index;
        }

        public IReadOnlyList<LoadoutItemGroupId> Find(ModOrderReference reference)
        {
            if (reference.Md5 != default && _byMd5.TryGetValue(reference.Md5, out var byMd5)) return byMd5;
            if (reference.Ids is { } ids && _byIds.TryGetValue(ids, out var byIds)) return byIds;
            if (!string.IsNullOrWhiteSpace(reference.Name) && _byName.TryGetValue(reference.Name, out var byName)) return byName;
            return [];
        }

        private static void Add<TKey>(Dictionary<TKey, List<LoadoutItemGroupId>> map, TKey key, LoadoutItemGroupId id) where TKey : notnull
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            list.Add(id);
        }
    }
}
