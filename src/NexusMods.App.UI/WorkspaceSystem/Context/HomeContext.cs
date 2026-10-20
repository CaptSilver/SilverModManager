using NexusMods.Abstractions.Serialization.Attributes;

namespace NexusMods.App.UI.WorkspaceSystem;

[JsonName("SilverModManager.App.UI.WorkspaceSystem.HomeContext")]
public record HomeContext : IWorkspaceContext
{
    public bool IsValid(IServiceProvider serviceProvider) => true;
}
