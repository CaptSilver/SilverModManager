using SilverModManager.Wabbajack.Models;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Collections;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.LeftMenu.Loadout;

/// <summary>
/// How a loadout's collections are ordered in the left menu. The loadout's default collection ("My Mods") stays
/// first, the collections the user has placed follow in their saved order, the ones not placed yet follow those, and
/// entries that are not installed collections (a collection still downloading) come last.
/// </summary>
internal static class LoadoutCollectionOrder
{
    /// <summary>Identifies a loadout in <see cref="Settings.LoadoutCollectionOrderSettings.Orders"/>.</summary>
    public static string KeyOf(LoadoutId loadoutId) => loadoutId.Value.Value.ToString();

    /// <summary>
    /// The loadout's default collection: the oldest one the user can edit and that is not an imported modlist. The
    /// loadout is created with it, so it is the one the menu has always listed first. Null when the loadout has none
    /// left (it can be deleted once another editable collection exists, after which no collection is pinned).
    /// </summary>
    public static CollectionGroupId? DefaultCollectionOf(IDb db, LoadoutId loadoutId)
    {
        return CollectionGroup.All(db)
            .Where(group => !group.IsReadOnly
                && group.AsLoadoutItemGroup().AsLoadoutItem().LoadoutId == loadoutId
                && !group.TryGetAsWabbajackModlistLoadoutGroup(out _))
            .OrderBy(group => group.Id)
            .Select(group => (CollectionGroupId?)group.CollectionGroupId)
            .FirstOrDefault();
    }

    /// <summary>
    /// Whether an entry may be dragged or moved: an installed collection other than the pinned default one. The
    /// comparer keeps these together in the middle of the list.
    /// </summary>
    public static bool IsMovable(ILeftMenuItemViewModel item) => item is CollectionLeftMenuItemViewModel { IsDefaultCollection: false };

    /// <summary>
    /// Sorts by <see cref="Rank"/>, then by saved position, then by the order the menu used before collections could be
    /// moved (editable before read-only, oldest first). One instance lives as long as the list it sorts; a new saved
    /// order updates its positions in place (<see cref="SetSavedOrder"/>).
    /// </summary>
    public sealed class Comparer : IComparer<ILeftMenuItemViewModel>
    {
        private Dictionary<ulong, int> _positions = new();

        public void SetSavedOrder(IReadOnlyList<ulong> savedOrder) => _positions = savedOrder
            .Select((key, index) => (key, index))
            .DistinctBy(pair => pair.key)
            .ToDictionary(pair => pair.key, pair => pair.index);

        public int Compare(ILeftMenuItemViewModel? x, ILeftMenuItemViewModel? y)
        {
            if (x is null) return y is null ? 0 : -1;
            if (y is null) return 1;

            var byRank = Rank(x).CompareTo(Rank(y));
            if (byRank != 0) return byRank;

            if (x is CollectionLeftMenuItemViewModel a && y is CollectionLeftMenuItemViewModel b)
            {
                var byPosition = PositionOf(a).CompareTo(PositionOf(b));
                if (byPosition != 0) return byPosition;

                var byEditable = a.IsCollectionReadOnly.CompareTo(b.IsCollectionReadOnly);
                return byEditable != 0 ? byEditable : a.CollectionGroupId.Value.CompareTo(b.CollectionGroupId.Value);
            }

            return string.Compare(x.Text.Value.Value, y.Text.Value.Value, StringComparison.Ordinal);
        }

        private int PositionOf(CollectionLeftMenuItemViewModel item) => _positions.GetValueOrDefault(item.CollectionGroupId.Value.Value, int.MaxValue);

        private static int Rank(ILeftMenuItemViewModel item) => item switch
        {
            CollectionLeftMenuItemViewModel { IsDefaultCollection: true } => 0,
            CollectionLeftMenuItemViewModel => 1,
            _ => 2,
        };
    }
}
