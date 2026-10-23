using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.UI.Sdk;
using R3;

namespace SilverModManager.App.UI.Pages.CollectionDownload.Dialogs.PremiumDownloads;

public interface IDialogPremiumCollectionDownloadsViewModel : IViewModelInterface
{
}

public class DialogPremiumCollectionDownloadsViewModel : AViewModel<IDialogPremiumCollectionDownloadsViewModel>, IDialogPremiumCollectionDownloadsViewModel
{

    public DialogPremiumCollectionDownloadsViewModel()
    {
    }
}
