using SilverModManager.UI.Sdk;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.Controls.CollectionMenuItem;

public partial class CollectionMenuItemDesignViewModel : AViewModel<ICollectionMenuItemViewModel>, ICollectionMenuItemViewModel
{
    [Reactive] public partial string CollectionName { get; set; } = "Vanilla Plus";
    [Reactive] public partial bool IsReadOnly { get; set; } = false;
    [Reactive] public partial string ToolTipText { get; set; } = "Read-only Nexus Mods Collection";
    [Reactive] public partial bool? IsSelected { get; set; } = true;
}
