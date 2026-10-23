using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI.LeftMenu;

public interface ILeftMenuViewModel : IViewModelInterface
{
    /// <summary>
    /// The Id of the workspace this left menu is attached to.
    /// </summary>
    public WorkspaceId WorkspaceId { get; }
}
