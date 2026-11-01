using SilverModManager.Abstractions.Library;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;

namespace SilverModManager.App.UI.Pages.LibraryPage;

/// <summary>
/// What the hide-updates menu item does when clicked, for any source: show the updates if the row's items have them
/// hidden, hide them otherwise.
/// </summary>
internal static class HideUpdatesToggle
{
    /// <summary>
    /// Flips the state each source reports for its share of the items. A parent row's items go to one source
    /// together, so the state is the row's: a row that shows "hidden" because any of its children is, shows all of
    /// them again.
    /// </summary>
    public static async ValueTask Apply(IDb db, ModSourceResolver resolver, IEnumerable<LibraryItemId> ids, CancellationToken cancellationToken)
    {
        var bySource = new Dictionary<IModSource, List<LibraryItem.ReadOnly>>();
        foreach (var id in ids)
        {
            var item = LibraryItem.Load(db, id);
            if (item.IsValid() is false) continue;
            if (resolver.For(item) is not { } source) continue;

            if (bySource.TryGetValue(source, out var items) is false) bySource[source] = items = [];
            items.Add(item);
        }

        foreach (var (source, items) in bySource)
        {
            await source.SetUpdatesHidden(items, hidden: CurrentState(source, items).IsHidden is false, cancellationToken);
        }
    }

    // The state as it is now. A source emits it while being subscribed to, but one with nothing to say (Nexus with
    // no update known for the item) may emit nothing; waiting for that would hang the click, and "not hidden" is the
    // right reading of it.
    private static HideUpdatesState CurrentState(IModSource source, IReadOnlyList<LibraryItem.ReadOnly> items)
    {
        var state = default(HideUpdatesState);
        using (source.ObserveHideUpdates(items).Subscribe(latest => state = latest))
        {
        }
        return state;
    }
}
