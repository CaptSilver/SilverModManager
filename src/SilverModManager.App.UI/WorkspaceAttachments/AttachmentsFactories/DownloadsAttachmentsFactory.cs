using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.WorkspaceSystem;

namespace SilverModManager.App.UI.WorkspaceAttachments;

public class DownloadsAttachmentsFactory : IWorkspaceAttachmentsFactory<DownloadsContext>
{
    public string CreateTitle(DownloadsContext context)
    {
        return Language.Downloads_WorkspaceTitle;
    }

    public string CreateSubtitle(DownloadsContext context)
    {
        return string.Empty;
    }
}
