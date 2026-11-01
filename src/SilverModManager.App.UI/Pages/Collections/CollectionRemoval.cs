using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Helpers;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.App.UI.Pages.Collections;

/// <summary>
/// What the page's and the menu entry's remove item show and do. The page calls <see cref="Remove"/> (no dialog);
/// a menu entry calls <see cref="ConfirmAndRemove"/>.
/// </summary>
public sealed class CollectionRemoval
{
    public required string Header { get; init; }
    public required IconValue Icon { get; init; }
    public required ContextMenuItemStyling Styling { get; init; }

    /// <summary>Shown on the page's item only.</summary>
    public string? ToolTip { get; init; }
    public required IObservable<bool> CanExecute { get; init; }
    public required Func<IWorkspaceController, Task> Remove { get; init; }

    /// <summary>
    /// Asks first, then removes. <see cref="Confirmed"/> asks <see cref="CollectionDeleteHelpers.ShowDeleteConfirmationDialogAsync"/>
    /// for the collection's name; a source whose removal has its own dialog replaces it.
    /// </summary>
    public required Func<IWindowManager, IWorkspaceController, Task> ConfirmAndRemove { get; init; }

    public static CollectionRemoval Confirmed(
        string header,
        IconValue icon,
        ContextMenuItemStyling styling,
        IObservable<bool> canExecute,
        Func<string> name,
        Func<IWorkspaceController, Task> remove,
        string? toolTip = null)
    {
        return new CollectionRemoval
        {
            Header = header,
            Icon = icon,
            Styling = styling,
            ToolTip = toolTip,
            CanExecute = canExecute,
            Remove = remove,
            ConfirmAndRemove = async (windowManager, controller) =>
            {
                if (await CollectionDeleteHelpers.ShowDeleteConfirmationDialogAsync(name(), windowManager))
                    await remove(controller);
            },
        };
    }
}
