using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;
using SilverModManager.App.UI.WorkspaceSystem;

namespace SilverModManager.UI.Tests.WorkspaceSystem;

[UsedImplicitly]
[SuppressMessage("ReSharper", "HeapView.BoxingAllocation")]
public partial class GridUtilsTests
{
    private static WorkspaceGridState CreateState(bool isHorizontal, params PanelGridState[] panels)
    {
        return WorkspaceGridState.From(panels, isHorizontal);
    }
}
