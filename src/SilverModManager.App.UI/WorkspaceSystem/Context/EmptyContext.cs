using SilverModManager.Abstractions.Serialization.Attributes;

namespace SilverModManager.App.UI.WorkspaceSystem;

[JsonName("SilverModManager.App.UI.WorkspaceSystem.EmptyContext")]
public record EmptyContext : IWorkspaceContext
{
    public static readonly IWorkspaceContext Instance = new EmptyContext();

    public bool IsValid(IServiceProvider serviceProvider) => true;
}
