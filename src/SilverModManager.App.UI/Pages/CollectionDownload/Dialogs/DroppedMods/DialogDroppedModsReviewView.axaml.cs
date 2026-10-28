using System.Reactive.Disposables;
using Avalonia.ReactiveUI;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.CollectionDownload.Dialogs.DroppedMods;

public partial class DialogDroppedModsReviewView : ReactiveUserControl<IDialogDroppedModsReviewViewModel>
{
    public DialogDroppedModsReviewView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            this.OneWayBind(ViewModel, vm => vm.Mods, view => view.ModsList.ItemsSource).DisposeWith(disposables);
        });
    }
}
