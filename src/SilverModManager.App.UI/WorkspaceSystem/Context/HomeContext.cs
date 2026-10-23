using SilverModManager.Abstractions.Serialization.Attributes;

namespace SilverModManager.App.UI.WorkspaceSystem;

[JsonName("SilverModManager.App.UI.WorkspaceSystem.HomeContext")]
public record HomeContext : IWorkspaceContext
{
    public bool IsValid(IServiceProvider serviceProvider) => true;
}
