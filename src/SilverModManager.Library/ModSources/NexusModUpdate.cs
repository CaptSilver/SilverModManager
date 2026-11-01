using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Library;

/// <summary>
/// The files of one Nexus Mods page that have a newer version, and the library items holding them.
/// </summary>
internal sealed record NexusModUpdate : ModUpdate
{
    public ModUpdatesOnModPage Updates { get; }

    private NexusModUpdate(ModUpdatesOnModPage updates, IReadOnlyList<LibraryItemId> currentItems)
        : base(currentItems, updates.MappingWithNewestFile().File.Version, updates.NewestFile().Version, updates.NewestFile().UploadedAt)
    {
        Updates = updates;
    }

    /// <summary>
    /// One update per file, however many library items hold it.
    /// </summary>
    public override int Count => Updates.NumberOfModFilesToUpdate;

    /// <summary>
    /// None when <paramref name="updates"/> has no file to update.
    /// </summary>
    public static NexusModUpdate? From(ModUpdatesOnModPage updates)
    {
        if (updates.HasAnyUpdates is false) return null;

        var items = new List<LibraryItemId>();
        foreach (var mapping in updates.FileMappings)
        {
            foreach (var item in NexusModsLibraryItem.FindByFileMetadata(mapping.File.Db, mapping.File))
                items.Add(item.AsLibraryItem().LibraryItemId);
        }
        return new NexusModUpdate(updates, items);
    }
}
