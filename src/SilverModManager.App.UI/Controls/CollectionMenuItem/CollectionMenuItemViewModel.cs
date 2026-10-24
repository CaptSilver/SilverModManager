using SilverModManager.UI.Sdk;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.Controls.CollectionMenuItem;

public partial class CollectionMenuItemViewModel : AViewModel<ICollectionMenuItemViewModel>, ICollectionMenuItemViewModel
{
    [Reactive] public partial string CollectionName { get; set; } = string.Empty;
    [Reactive] public partial bool IsReadOnly { get; set; }
    [Reactive] public partial string ToolTipText { get; set; } = string.Empty;
    [Reactive] public partial bool? IsSelected { get; set; } = null;
}
