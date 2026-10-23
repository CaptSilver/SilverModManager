using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.WorkspaceSystem;

namespace SilverModManager.App.UI.WorkspaceAttachments;

public class HomeAttachmentsFactory : IWorkspaceAttachmentsFactory<HomeContext>
{
    public string CreateTitle(HomeContext context)
    {
        return Language.HomeWorkspace_Title;
    }

    public string CreateSubtitle(HomeContext context)
    {
        return string.Empty;
    }
}
