using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using DynamicData;
using DynamicData.Binding;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls.Spine;
using SilverModManager.App.UI.Helpers;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.App.UI.Settings;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.App.UI.LeftMenu.Loadout;

/// <summary>
/// The collection entries of one loadout's left menu, in the order the user chose. The list lives as long as the menu;
/// a changed order moves the entries already in it, so the view doesn't rebuild them.
/// </summary>
internal sealed class LoadoutCollectionList
{
    private readonly ISettingsManager _settingsManager;
    private readonly LoadoutId _loadoutId;
    private readonly ObservableCollectionExtended<ILeftMenuItemViewModel> _list = new();
    private readonly LoadoutCollectionOrder.Comparer _order = new();

    public LoadoutCollectionList(ISettingsManager settingsManager, LoadoutId loadoutId)
    {
        _settingsManager = settingsManager;
        _loadoutId = loadoutId;
        Items = new ReadOnlyObservableCollection<ILeftMenuItemViewModel>(_list);
    }

    public ReadOnlyObservableCollection<ILeftMenuItemViewModel> Items { get; }

    /// <summary>
    /// Fills the list from <paramref name="source"/> until the returned subscription is disposed. Call it on the UI
    /// thread: that is where the list is changed.
    /// </summary>
    public IDisposable Bind(IObservable<IChangeSet<ILeftMenuItemViewModel, EntityId>> source)
    {
        // A re-activated menu delivers its entries again; they must not be listed twice.
        _list.Clear();

        var key = LoadoutCollectionOrder.KeyOf(_loadoutId);
        var disposables = new CompositeDisposable();

        R3.ObservableExtensions
            .AsSystemObservable(_settingsManager.GetChanges<LoadoutCollectionOrderSettings>(prependCurrent: true))
            // Re-sorting the bound list is something only the UI thread may do.
            .OnUI()
            .SubscribeWithErrorLogging(settings =>
            {
                _order.SetSavedOrder(settings.Orders.GetValueOrDefault(key) ?? ImmutableList<ulong>.Empty);
                BoundListOrder.MoveIntoOrder(_list, _order);
            })
            .DisposeWith(disposables);

        source
            .OnUI()
            .SortAndBind(_list, _order)
            .SubscribeWithErrorLogging()
            .DisposeWith(disposables);

        return disposables;
    }

    /// <summary>Whether the entry at <paramref name="index"/> is a collection the user can move.</summary>
    public bool IsMovable(int index) => index >= 0 && index < _list.Count && LoadoutCollectionOrder.IsMovable(_list[index]);

    /// <summary>
    /// Moves the collection at <paramref name="fromIndex"/> to <paramref name="toIndex"/> and saves the order. Both
    /// have to be movable collections: the default collection and the entries after the collections keep their places.
    /// </summary>
    public void Move(int fromIndex, int toIndex)
    {
        if (!IsMovable(fromIndex)) throw new ArgumentException($"The menu entry at {fromIndex} is not a collection that can be moved", nameof(fromIndex));
        if (!IsMovable(toIndex)) throw new ArgumentException($"The menu entry at {toIndex} is not a place a collection can be moved to", nameof(toIndex));

        // Saving the displayed order is also what drops ids of deleted collections from the setting.
        var displayed = _list
            .OfType<CollectionLeftMenuItemViewModel>()
            .Where(LoadoutCollectionOrder.IsMovable)
            .Select(item => item.CollectionGroupId.Value.Value)
            .ToList();
        var firstMovable = _list.TakeWhile(item => !LoadoutCollectionOrder.IsMovable(item)).Count();
        var moved = LoadoutSpineOrder.Move(displayed, fromIndex - firstMovable, toIndex - firstMovable);

        _settingsManager.Update<LoadoutCollectionOrderSettings>(settings => settings with
        {
            Orders = settings.Orders.SetItem(LoadoutCollectionOrder.KeyOf(_loadoutId), moved.ToImmutableList()),
        });
    }

    /// <summary>Moves the collection one place up (negative <paramref name="offset"/>) or down; at the end of the movable ones it does nothing.</summary>
    public void MoveBy(CollectionGroupId collectionId, int offset)
    {
        var index = _list.ToList().FindIndex(item => item is CollectionLeftMenuItemViewModel collection && collection.CollectionGroupId == collectionId);
        var target = index + offset;
        if (!IsMovable(index) || !IsMovable(target)) return;
        Move(index, target);
    }
}
