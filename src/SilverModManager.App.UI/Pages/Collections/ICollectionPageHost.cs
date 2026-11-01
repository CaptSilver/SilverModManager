using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;

namespace SilverModManager.App.UI.Pages.Collections;

/// <summary>
/// What a <see cref="CollectionSource"/>'s menu items need from the page they sit on. <see cref="WorkspaceController"/>
/// is read when a command runs: the window id it needs is set after the page is constructed.
/// </summary>
public interface ICollectionPageHost
{
    IWindowManager WindowManager { get; }
    IWorkspaceController WorkspaceController { get; }
    WorkspaceId WorkspaceId { get; }
}
