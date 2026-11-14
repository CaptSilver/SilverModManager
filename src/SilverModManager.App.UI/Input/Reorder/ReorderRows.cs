using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Controls.Spine.Buttons.Image;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.App.UI.LeftMenu.Loadout;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.Input.Reorder;

/// <summary>Where each kind of ordering page keeps the key of a row, read from the focused element.</summary>
internal static class ReorderRows
{
    /// <summary>
    /// A row of a tree data grid of <see cref="CompositeItemModel{TKey}"/>. Focus inside the row (a button in a cell) names
    /// it; focus on the grid itself names the selected row, which is what a pad moves with the D-pad.
    /// </summary>
    public static RowReader<TKey> InGrid<TKey>() where TKey : notnull => (IInputElement focused, [MaybeNullWhen(false)] out TKey key, [NotNullWhen(true)] out Control? row) =>
    {
        foreach (var element in FocusAncestors.Of(focused))
        {
            if (element is TreeDataGridRow { Model: CompositeItemModel<TKey> model } found)
            {
                key = model.Key;
                row = found;
                return true;
            }

            if (element is not TreeDataGrid grid) continue;

            if (grid.RowSelection?.SelectedItem is CompositeItemModel<TKey> selected
                && grid.GetVisualDescendants().OfType<TreeDataGridRow>().FirstOrDefault(r => ReferenceEquals(r.Model, selected)) is { } selectedRow)
            {
                key = selected.Key;
                row = selectedRow;
                return true;
            }
        }

        key = default;
        row = null;
        return false;
    };

    /// <summary>A loadout's icon on the spine.</summary>
    public static bool InSpine(IInputElement focused, out LoadoutId key, [NotNullWhen(true)] out Control? row)
    {
        foreach (var element in FocusAncestors.Of(focused))
        {
            if (element is Control { DataContext: IImageButtonViewModel { LoadoutBadgeViewModel.LoadoutValue: { HasValue: true } loadout } } control)
            {
                key = loadout.Value.LoadoutId;
                row = control;
                return true;
            }
        }

        key = default;
        row = null;
        return false;
    }

    /// <summary>A collection entry of the left menu that the user can move.</summary>
    public static bool InCollections(IInputElement focused, out CollectionGroupId key, [NotNullWhen(true)] out Control? row)
    {
        foreach (var element in FocusAncestors.Of(focused))
        {
            if (element is Control { DataContext: CollectionLeftMenuItemViewModel { IsDefaultCollection: false } collection } control)
            {
                key = collection.CollectionGroupId;
                row = control;
                return true;
            }
        }

        key = default;
        row = null;
        return false;
    }
}
