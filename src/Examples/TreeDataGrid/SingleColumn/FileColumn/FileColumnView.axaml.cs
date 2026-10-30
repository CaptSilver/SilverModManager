using Avalonia.ReactiveUI;
using ReactiveUI;

namespace Examples.TreeDataGrid.SingleColumn.FileColumn;

public partial class FileColumnView : ReactiveUserControl<IFileColumnViewModel>
{
    public FileColumnView()
    {
        InitializeComponent();

        // Subscribe here if you need mutating.
        this.WhenActivated(_ =>
            {
                if (ViewModel is not { } vm)
                    return;

                EntryIcon.Classes.Add(vm.IsFile ? "File" : "FolderOutline");
                FileNameTextBlock.Text = vm.Name;
            }
        );
    }
}

