using SilverModManager.App.UI.Helpers.TreeDataGrid;
using SilverModManager.Sdk.Games;
using SilverModManager.UI.Sdk;

namespace Examples.TreeDataGrid.Basic.ViewModel;

public interface IFileViewModel : 
    IViewModelInterface, // For INotifyPropertyChanged and Reactive.
    IExpandableItem, // For ability to expand. (IsExpanded)
    
    // For child/parent creation via DynamicData.
    // Pass 'self' and unique key.
    // This key can be a GUID, auto incremented int, or something else unique.
    // In this example, we use a file/folder path.
    IDynamicDataTreeItem<IFileViewModel, GamePath> 
{
    /// <summary>
    ///     Name of the file in question.
    /// </summary>
    public string Name { get; }
}
