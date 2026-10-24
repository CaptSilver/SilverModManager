using System.Collections.ObjectModel;
using SilverModManager.Sdk.Games;
using SilverModManager.UI.Sdk;
using ReactiveUI.SourceGenerators;

namespace Examples.TreeDataGrid.Basic.ViewModel;

public partial class FileDesignViewModel(GamePath fullPath) : AViewModel<IFileViewModel>, IFileViewModel
{
    // Name of this node.
    public string Name => Key.Name;

    // IDynamicDataTreeItem
    public ReadOnlyObservableCollection<IFileViewModel>? Children { get; set; }
    public IFileViewModel? Parent { get; set; }
    public GamePath Key { get; set; } = fullPath;

    // IExpandableViewModel
    [Reactive] public partial bool IsExpanded { get; set; }
}
