using SilverModManager.App.UI.LeftMenu;
using SilverModManager.App.UI.WorkspaceSystem;

namespace SilverModManager.App.UI.WorkspaceAttachments;

public interface IWorkspaceAttachmentsFactoryManager
{
    public ILeftMenuViewModel? CreateLeftMenuFor(IWorkspaceContext context, WorkspaceId workspaceId, IWorkspaceController workspaceController);

    public string CreateTitleFor(IWorkspaceContext context);
    
    public string CreateSubtitleFor(IWorkspaceContext context);
}
