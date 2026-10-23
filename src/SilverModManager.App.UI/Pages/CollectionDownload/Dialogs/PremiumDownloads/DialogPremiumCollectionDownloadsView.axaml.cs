using System.Reactive.Disposables;
using Avalonia.ReactiveUI;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.App.UI.Resources;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.CollectionDownload.Dialogs.PremiumDownloads;

public partial class DialogPremiumCollectionDownloadsView : ReactiveUserControl<IDialogPremiumCollectionDownloadsViewModel>
{
    public DialogPremiumCollectionDownloadsView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
            {
                
            }
        );
    }
}
