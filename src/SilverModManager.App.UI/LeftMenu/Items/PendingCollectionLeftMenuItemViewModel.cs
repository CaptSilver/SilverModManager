using Microsoft.Extensions.DependencyInjection;
using SilverModManager.App.UI.Pages.Collections;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;

namespace SilverModManager.App.UI.LeftMenu.Items;

/// <summary>
/// A collection that belongs to the loadout's game but is not installed in it: no toggle, a downloading icon, and the
/// page <see cref="CollectionSource.PendingPage"/> names. Removing it removes the collection from the library, not
/// from the loadout.
/// </summary>
public class PendingCollectionLeftMenuItemViewModel : LeftMenuItemWithRightIconViewModel
{
    public PendingCollectionLeftMenuItemViewModel(
        IWorkspaceController workspaceController,
        WorkspaceId workspaceId,
        CollectionSource source,
        IServiceProvider serviceProvider) : base(workspaceController, workspaceId, source.PendingPage ?? throw NeverPending(source))
    {
        var removal = source.PendingRemoval ?? throw NeverPending(source);

        AdditionalContextMenuItems =
        [
            removal.ToMenuItem(serviceProvider.GetRequiredService<IWindowManager>(), workspaceController),
        ];
    }

    private static InvalidOperationException NeverPending(CollectionSource source) =>
        new($"Collection '{source.InitialName}' is never pending, so it has no pending entry");
}
