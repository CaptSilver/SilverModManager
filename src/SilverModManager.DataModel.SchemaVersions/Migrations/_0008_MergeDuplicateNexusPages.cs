using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;

namespace SilverModManager.DataModel.SchemaVersions;

/// <summary>
/// Folds Nexus Mods mod page rows that share a uid into the oldest of them, and file rows that end up sharing a
/// uid on the surviving page into the oldest of those.
/// </summary>
/// <remarks>
/// A page's uid is indexed but not unique, and collections resolving the same mod in separate transactions each
/// wrote their own row. Lookups return the oldest row, so library items pointing at a younger one were listed under
/// a page that update checks never refreshed. The survivor keeps its own values; a thumbnail or picture address it
/// lacks is taken from a duplicate. Everything that pointed at a deleted row (files, library items, collection
/// downloads) is pointed at the survivor.
/// </remarks>
// ReSharper disable once InconsistentNaming
public sealed class _0008_MergeDuplicateNexusPages(ILogger<_0008_MergeDuplicateNexusPages> logger) : ITransactionalMigration
{
    public static (MigrationId Id, string Name) IdAndName => MigrationId.ParseNameAndId(nameof(_0008_MergeDuplicateNexusPages));

    public Task Prepare(IDb db) => Task.CompletedTask;

    public void Migrate(ITransaction tx, IDb db)
    {
        var survivingPage = new Dictionary<EntityId, EntityId>();
        foreach (var rows in DuplicateGroups(NexusModsModPageMetadata.All(db), page => page.Uid, page => page.Id))
        {
            logger.LogInformation("Merging {Count} Nexus Mods page rows for mod {Uid} into {Survivor}", rows.Length, rows[0].Uid, rows[0].Id);
            foreach (var duplicate in rows[1..])
                survivingPage[duplicate.Id] = rows[0].Id;
            CarryMissingValues(tx, rows[0], rows[1..]);
        }

        // Files are grouped by the page they will be on once the pages are merged, so a file with the same uid on two
        // different pages (which GetOrAddFile allows) is left alone.
        EntityId PageAfterMerge(NexusModsFileMetadata.ReadOnly file) => survivingPage.GetValueOrDefault(file.ModPageId.Value, file.ModPageId.Value);
        var survivingFile = new Dictionary<EntityId, EntityId>();
        foreach (var rows in DuplicateGroups(NexusModsFileMetadata.All(db), file => (file.Uid, PageAfterMerge(file)), file => file.Id))
        {
            logger.LogInformation("Merging {Count} Nexus Mods file rows for file {Uid} into {Survivor}", rows.Length, rows[0].Uid, rows[0].Id);
            foreach (var duplicate in rows[1..])
                survivingFile[duplicate.Id] = rows[0].Id;
        }

        var deleted = survivingPage.Keys.Concat(survivingFile.Keys).ToHashSet();
        foreach (var (duplicate, survivor) in survivingPage.Concat(survivingFile))
        {
            // A deleted file's own page reference goes with it.
            ReferenceRepointing.Repoint(tx, db, duplicate, survivor, reference => deleted.Contains(reference.E));
            tx.Delete(duplicate, recursive: false);
        }
    }

    // Each group of more than one row sharing a key, oldest first.
    private static IEnumerable<T[]> DuplicateGroups<T, TKey>(IEnumerable<T> rows, Func<T, TKey> key, Func<T, EntityId> id) where TKey : notnull
        => rows.GroupBy(key)
            .Where(group => group.Count() > 1)
            .Select(group => group.OrderBy(row => id(row).Value).ToArray());

    // Each single-value attribute is taken from the oldest duplicate that has it. The survivor's own state is what the
    // transaction checks adds against, so adding one value per duplicate would store two.
    private static void CarryMissingValues(ITransaction tx, NexusModsModPageMetadata.ReadOnly survivor, NexusModsModPageMetadata.ReadOnly[] duplicates)
    {
        if (!NexusModsModPageMetadata.FullSizedPictureUri.Contains(survivor))
        {
            foreach (var duplicate in duplicates)
            {
                if (!NexusModsModPageMetadata.FullSizedPictureUri.TryGetValue(duplicate, out var picture)) continue;
                tx.Add(survivor.Id, NexusModsModPageMetadata.FullSizedPictureUri, picture);
                break;
            }
        }

        if (!NexusModsModPageMetadata.ThumbnailUri.Contains(survivor))
        {
            foreach (var duplicate in duplicates)
            {
                if (!NexusModsModPageMetadata.ThumbnailUri.TryGetValue(duplicate, out var thumbnailUri)) continue;
                tx.Add(survivor.Id, NexusModsModPageMetadata.ThumbnailUri, thumbnailUri);
                break;
            }
        }

        if (!NexusModsModPageMetadata.ThumbnailResource.Contains(survivor))
        {
            foreach (var duplicate in duplicates)
            {
                if (!NexusModsModPageMetadata.ThumbnailResource.TryGetValue(duplicate, out var resource)) continue;
                tx.Add(survivor.Id, NexusModsModPageMetadata.ThumbnailResource, resource);
                break;
            }
        }
    }
}
