using JetBrains.Annotations;

namespace SilverModManager.App.UI.WorkspaceSystem;

[PublicAPI]
public sealed record WorkspaceData
{
    public required WorkspaceId Id { get; init; }

    public required IWorkspaceContext Context { get; init; }

    public required PanelData[] Panels { get; init; }
}
