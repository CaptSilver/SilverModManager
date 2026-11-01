using SilverModManager.Abstractions.Library;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;

namespace SilverModManager.App.UI.Pages.LibraryPage;

/// <summary>
/// Looks up a page on the site a library item came from, for the "View mod page" and "View changelog" actions on
/// the library and loadout pages.
/// </summary>
internal static class ModSourcePages
{
    /// <summary>
    /// The first address <paramref name="uriOf"/> yields for the items, in order. A parent row carries all of its
    /// children's ids and they share a page, so the first child that has one is enough. Null when no item has a
    /// source or none of them knows its page.
    /// </summary>
    public static Uri? Find(IDb db, ModSourceResolver resolver, IEnumerable<LibraryItemId> ids, Func<IModSource, LibraryItem.ReadOnly, Uri?> uriOf)
    {
        foreach (var id in ids)
        {
            var item = LibraryItem.Load(db, id);
            if (item.IsValid() is false) continue;

            var source = resolver.For(item);
            if (source is null) continue;

            var uri = uriOf(source, item);
            if (uri is not null) return uri;
        }

        return null;
    }
}
