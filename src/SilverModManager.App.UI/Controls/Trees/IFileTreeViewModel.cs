using System.Collections.ObjectModel;
using Avalonia.Controls;
using SilverModManager.App.UI.Controls.Trees.Files;
using SilverModManager.Sdk.Games;
using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI.Controls.Trees;

public interface IFileTreeViewModel : IViewModelInterface
{
    ITreeDataGridSource<IFileTreeNodeViewModel> TreeSource { get; }
    
    ReadOnlyObservableCollection<string> StatusBarStrings { get; }

    /// <summary>
    /// The nodes selected in the tree, folders and files alike. Follows the tree's selection; a refresh builds new
    /// node instances, so it empties then.
    /// </summary>
    IReadOnlyList<IFileTreeNodeViewModel> SelectedNodes { get; set; }
    
    /// <summary>
    /// The invalid GamePath used to represent the parent of a root entry.
    /// Necessary for DynamicData TransformToTree, we need a GamePath that is guaranteed not to represent another node.
    /// </summary>
    public static readonly GamePath RootParentGamePath = new(LocationId.Unknown, "");
}
