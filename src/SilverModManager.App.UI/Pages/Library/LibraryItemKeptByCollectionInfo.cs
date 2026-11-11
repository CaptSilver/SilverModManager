namespace SilverModManager.App.UI.Pages.Library;

/// <summary>A collection that owns its mods and links some of the archives being deleted; the delete unlinks the mods and keeps them.</summary>
/// <param name="CollectionName">The collection's name.</param>
/// <param name="ItemNames">The names of the archives to delete that the collection links.</param>
public record LibraryItemKeptByCollectionInfo(string CollectionName, List<string> ItemNames);
