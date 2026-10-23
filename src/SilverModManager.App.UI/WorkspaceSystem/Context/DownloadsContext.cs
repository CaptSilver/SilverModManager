using SilverModManager.Abstractions.Serialization.Attributes;

namespace SilverModManager.App.UI.WorkspaceSystem;

[JsonName("SilverModManager.App.UI.WorkspaceSystem.DownloadsContext")]
public record DownloadsContext : IWorkspaceContext
{
    public bool IsValid(IServiceProvider serviceProvider) => true;
}
