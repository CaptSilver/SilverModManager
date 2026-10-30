using System.Diagnostics.CodeAnalysis;
using System.Reactive.Disposables;
using Avalonia.ReactiveUI;
using SilverModManager.App.UI.Controls.Trees.Common;
using SilverModManager.UI.Sdk;
using ReactiveUI;

namespace SilverModManager.Games.AdvancedInstaller.UI.Preview;

[ExcludeFromCodeCoverage]
public partial class PreviewTreeEntryView : ReactiveUserControl<IPreviewTreeEntryViewModel>
{
    public PreviewTreeEntryView()
    {
        InitializeComponent();
        this.WhenActivated(disposable =>
        {
            // Initialize the view if the view model is not null
            this.WhenAnyValue(view => view.ViewModel)
                .WhereNotNull()
                .Subscribe(InitView)
                .DisposeWith(disposable);

            this.BindCommand(ViewModel, vm => vm.RemoveMappingCommand,
                    v => v.XRoundedButton)
                .DisposeWith(disposable);

            this.OneWayBind(ViewModel, vm => vm.IsRemovable,
                    view => view.XRoundedButton.IsVisible)
                .DisposeWith(disposable);

            // FolderMerged pill, only show if it is a folder and it is merged.
            this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.IsFolderMerged))
                .Subscribe(isMerged => FolderMergedPill.IsVisible = isMerged && ViewModel is { IsDirectory: true })
                .DisposeWith(disposable);
        });
    }

    private void InitView(IPreviewTreeEntryViewModel vm)
    {
        FileNameTextBlock.Text = vm.DisplayName;
        NewPill.IsVisible = vm.IsNew;
        DupeFolderPill.IsVisible = vm.IsFolderDupe;
        FolderMergedPill.IsVisible = vm.IsFolderMerged && vm.IsDirectory;

        // Always show unlink button, it means unlink child nodes if it is a folder.
        if (vm.IsDirectory)
        {
            EntryIcon.Classes.Add("FolderOutline");

        }
        else
        {
            EntryIcon.Classes.Add(vm.GamePath.Extension.GetIconClass());
        }
    }
}
