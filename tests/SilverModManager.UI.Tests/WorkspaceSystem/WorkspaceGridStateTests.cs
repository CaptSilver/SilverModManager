using SilverModManager.App.UI.WorkspaceSystem;

namespace SilverModManager.UI.Tests.WorkspaceSystem;

public partial class WorkspaceGridStateTests
{
    private static WorkspaceGridState CreateState(bool isHorizontal, params PanelGridState[] panels)
    {
        return WorkspaceGridState.From(panels, isHorizontal);
    }
}
