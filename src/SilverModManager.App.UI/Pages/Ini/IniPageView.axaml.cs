using Avalonia.Controls;
using Avalonia.ReactiveUI;
using JetBrains.Annotations;
using ReactiveUI;
using R3;

namespace SilverModManager.App.UI.Pages.Ini;

[UsedImplicitly]
public partial class IniPageView : ReactiveUserControl<IIniPageViewModel>
{
    public IniPageView()
    {
        InitializeComponent();

        FileTabs.SelectionChanged += (_, _) =>
        {
            if (ViewModel is { } vm && FileTabs.SelectedIndex >= 0 && FileTabs.SelectedIndex < vm.Files.Count)
                vm.SelectedFile = vm.Files[FileTabs.SelectedIndex];
        };

        this.WhenActivated(disposables =>
        {
            if (ViewModel is not { } viewModel) return;

            // One tab per file, each holding the view of that file's editor. The set is replaced as a whole.
            viewModel.WhenAnyValue(vm => vm.Files)
                .Subscribe(files => FileTabs.ItemsSource = files.Select(file => new TabItem
                {
                    Header = file.FileName,
                    Content = new IniFileEditorView { DataContext = file },
                }).ToArray())
                .AddTo(disposables);
        });
    }
}
