using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI.WorkspaceSystem;

public interface IAddPanelDropDownViewModel : IViewModelInterface
{
    public IReadOnlyList<IAddPanelButtonViewModel> AddPanelButtonViewModels { get; }

    public IAddPanelButtonViewModel? SelectedItem { get; set; }

    public int SelectedIndex { get; set; }
}
