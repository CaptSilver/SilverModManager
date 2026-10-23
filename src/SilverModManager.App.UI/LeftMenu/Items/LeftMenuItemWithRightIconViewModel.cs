using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.App.UI.LeftMenu.Items;

public class LeftMenuItemWithRightIconViewModel : LeftMenuItemViewModel
{
    public required IconValue RightIcon { get; init; }

    public LeftMenuItemWithRightIconViewModel(
        IWorkspaceController workspaceController,
        WorkspaceId workspaceId,
        PageData pageData) : base(workspaceController, workspaceId, pageData) { }
}
