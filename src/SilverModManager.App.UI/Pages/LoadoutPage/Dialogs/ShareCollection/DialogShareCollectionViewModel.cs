using SilverModManager.UI.Sdk;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.Pages.LoadoutPage.Dialogs.ShareCollection;

public interface IDialogShareCollectionViewModel: IViewModelInterface
{
    bool IsListed { get; set; }
}

public partial class DialogShareCollectionViewModel : AViewModel<IDialogShareCollectionViewModel>, IDialogShareCollectionViewModel
{
    [Reactive] public partial bool IsListed { get; set; }

    public DialogShareCollectionViewModel(bool isVisible)
    {
        IsListed = isVisible;
    }
}
