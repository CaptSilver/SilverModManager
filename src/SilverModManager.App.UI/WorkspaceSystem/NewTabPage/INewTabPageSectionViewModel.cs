using System.Collections.ObjectModel;
using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI.WorkspaceSystem;

public interface INewTabPageSectionViewModel : IViewModelInterface
{
    public string SectionName { get; }

    public ReadOnlyObservableCollection<INewTabPageSectionItemViewModel> Items { get; }
}
