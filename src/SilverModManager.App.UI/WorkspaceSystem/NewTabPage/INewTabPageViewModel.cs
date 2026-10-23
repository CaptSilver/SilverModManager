using System.Collections.ObjectModel;
using SilverModManager.App.UI.Controls.Alerts;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.App.UI.WorkspaceSystem;

public interface INewTabPageViewModel : IPageViewModelInterface
{
    ReadOnlyObservableCollection<INewTabPageSectionViewModel> Sections { get; }

    AlertSettingsWrapper AlertSettingsWrapper { get; }
}
