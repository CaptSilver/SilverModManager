using JetBrains.Annotations;
using SilverModManager.Abstractions.Serialization.Attributes;

namespace SilverModManager.App.UI.WorkspaceSystem;

[PublicAPI]
public sealed record WindowData
{
    public required WorkspaceId? ActiveWorkspaceId { get; init; }

    public required WorkspaceData[] Workspaces { get; init; }
}
