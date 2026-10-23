using Avalonia.Controls;
using Examples.TreeDataGrid.SingleColumn.FileColumn;
using SilverModManager.UI.Sdk;

namespace Examples.TreeDataGrid.SingleColumn;

public interface IFileTreeViewModel : IViewModelInterface
{
    ITreeDataGridSource<IFileColumnViewModel>? TreeSource { get; }
}
